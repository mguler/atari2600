using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Atari2600Emu;

/// <summary>
/// Minimal WinMM waveOut audio output (mono 44.1kHz 16-bit PCM) with a few queued buffers.
/// No external dependencies.
/// </summary>
public sealed class WinMmAudioOutput : IDisposable
{
    private const int CALLBACK_EVENT = 0x00050000;
    private const uint WHDR_DONE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        // dwUser is DWORD_PTR (pointer-sized) on both x86 and x64.
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        // reserved is also DWORD_PTR (pointer-sized).
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);
    // NOTE: waveOut keeps a pointer to the WAVEHDR until playback completes.
    // Therefore we must keep each header in unmanaged memory (stable address).
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hWaveOut);
    [DllImport("winmm.dll")] private static extern int waveOutSetVolume(IntPtr hWaveOut, uint dwVolume);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hWaveOut);

    private readonly IntPtr _hWaveOut;
    private readonly AutoResetEvent _doneEvent;

    private readonly ConcurrentQueue<short[]> _queue = new();
    private readonly AutoResetEvent _haveData = new(false);
    private readonly Thread _worker;
    private volatile bool _running = true;
    private int _queuedChunks;

    private sealed class Buffer
    {
        public byte[] Data = Array.Empty<byte>();
        public GCHandle DataHandle;
        public IntPtr HeaderPtr;
        public bool InFlight;

        public void Dispose()
        {
            if (DataHandle.IsAllocated) DataHandle.Free();
            if (HeaderPtr != IntPtr.Zero) Marshal.FreeHGlobal(HeaderPtr);
        }
    }

    private readonly Buffer[] _buffers;

    public WinMmAudioOutput(int sampleRate = 44100, int bufferSamples = 1024, int numBuffers = 4)
    {
        _doneEvent = new AutoResetEvent(false);

        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = 1,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16,
        };
        fmt.nBlockAlign = (ushort)(fmt.nChannels * (fmt.wBitsPerSample / 8));
        fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;

        // CALLBACK_EVENT => dwCallback is an event handle that is signaled when a buffer finishes.
        IntPtr evtHandle = _doneEvent.SafeWaitHandle.DangerousGetHandle();
        int rc = waveOutOpen(out _hWaveOut, -1, ref fmt, evtHandle, IntPtr.Zero, CALLBACK_EVENT);
        if (rc != 0)
            throw new InvalidOperationException($"waveOutOpen failed: {rc}");

        // Ensure the wave output isn't effectively muted. Volume is 0xFFFF for each channel.
        // For mono, WinMM still expects a packed left|right volume.
        try { waveOutSetVolume(_hWaveOut, 0xFFFFFFFF); } catch { }

        _buffers = new Buffer[numBuffers];
        int bytesPerBuffer = bufferSamples * 2; // mono 16-bit

        for (int i = 0; i < numBuffers; i++)
        {
            var b = new Buffer();
            b.Data = new byte[bytesPerBuffer];
            b.DataHandle = GCHandle.Alloc(b.Data, GCHandleType.Pinned);

            // Allocate a stable unmanaged WAVEHDR.
            var hdr = new WAVEHDR
            {
                lpData = b.DataHandle.AddrOfPinnedObject(),
                dwBufferLength = (uint)b.Data.Length,
                dwFlags = 0
            };
            b.HeaderPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            Marshal.StructureToPtr(hdr, b.HeaderPtr, false);

            rc = waveOutPrepareHeader(_hWaveOut, b.HeaderPtr, (uint)Marshal.SizeOf<WAVEHDR>());
            if (rc != 0)
                throw new InvalidOperationException($"waveOutPrepareHeader failed: {rc}");
            b.InFlight = false;
            _buffers[i] = b;
        }

        // Audio worker thread (keeps UI thread responsive; Enqueue() is non-blocking).
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "WinMmAudioOutput"
        };
        _worker.Start();
    }

    public void EnqueuePcm16(short[] samples)
    {
        if (samples == null || samples.Length == 0) return;
        if (!_running) return;

        // Bound queue growth (drop audio if UI produces faster than device consumes).
        if (Interlocked.Increment(ref _queuedChunks) > 16)
        {
            Interlocked.Decrement(ref _queuedChunks);
            return;
        }

        _queue.Enqueue(samples);
        _haveData.Set();
    }

    // Back-compat alias for older call sites.
    public void Enqueue(short[] samples) => EnqueuePcm16(samples);

    private void WorkerLoop()
    {
        try
        {
            while (_running)
            {
                // Wait until we have something to play.
                _haveData.WaitOne(50);
                while (_running && _queue.TryDequeue(out var samples))
                {
                    Interlocked.Decrement(ref _queuedChunks);
                    WritePcmBlocking(samples);
                }
            }
        }
        catch
        {
            // Swallow worker exceptions: audio should not kill the emulator.
        }
    }

    private void WritePcmBlocking(short[] samples)
    {
        if (samples.Length == 0) return;

        int byteCount = samples.Length * 2;
        int offsetBytes = 0;

        // Chunk into our fixed buffers.
        while (offsetBytes < byteCount && _running)
        {
            Buffer buf = GetFreeBufferBlocking();

            int toCopy = Math.Min(buf.Data.Length, byteCount - offsetBytes);

            // Copy shorts -> bytes (little-endian)
            // NOTE: this file defines a nested type named Buffer, so use System.Buffer explicitly.
            System.Buffer.BlockCopy(samples, offsetBytes, buf.Data, 0, toCopy);

            // Update header (length) in unmanaged memory.
            var hdr = Marshal.PtrToStructure<WAVEHDR>(buf.HeaderPtr);
            hdr.dwBufferLength = (uint)toCopy;
            // Do NOT wipe all flags: WHDR_PREPARED must remain set after waveOutPrepareHeader.
            // Only clear WHDR_DONE if it is set.
            hdr.dwFlags &= ~WHDR_DONE;
            Marshal.StructureToPtr(hdr, buf.HeaderPtr, false);

            int rc = waveOutWrite(_hWaveOut, buf.HeaderPtr, (uint)Marshal.SizeOf<WAVEHDR>());
            if (rc != 0)
                return; // don't throw from audio path

            buf.InFlight = true;
            offsetBytes += toCopy;
        }
    }

    private Buffer GetFreeBufferBlocking()
    {
        while (true)
        {
            for (int i = 0; i < _buffers.Length; i++)
            {
                var b = _buffers[i];
                if (!b.InFlight) return b;

                // WOM_DONE sets WHDR_DONE in the same unmanaged WAVEHDR.
                var hdr = Marshal.PtrToStructure<WAVEHDR>(b.HeaderPtr);
                if ((hdr.dwFlags & WHDR_DONE) != 0)
                {
                    // Mark free. Unprepare/prepare cycle is not necessary when reusing the same header,
                    // but clearing InFlight makes our allocator work.
                    b.InFlight = false;
                    return b;
                }
            }

            // Wait for any buffer completion.
            _doneEvent.WaitOne(10);
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _haveData.Set(); } catch { }
        try { _worker.Join(200); } catch { }

        try { waveOutReset(_hWaveOut); } catch { }

        foreach (var b in _buffers)
        {
            try { waveOutUnprepareHeader(_hWaveOut, b.HeaderPtr, (uint)Marshal.SizeOf<WAVEHDR>()); } catch { }
            b.Dispose();
        }

        try { waveOutClose(_hWaveOut); } catch { }
        _doneEvent.Dispose();
        _haveData.Dispose();
    }
}
