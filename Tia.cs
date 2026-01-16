using System;
using System.Collections.Generic;

namespace Atari2600Emu
{
    public sealed class Tia
    {
        private readonly InputState _input;

        // --- Write registers (subset) ---
        private byte _vsync;    // $00
        private byte _vblank;   // $01

        private byte _nusiz0;   // $04
        private byte _nusiz1;   // $05

        private byte _colup0;   // $06
        private byte _colup1;   // $07
        private byte _colupf;   // $08
        private byte _colubk;   // $09
        private byte _ctrlpf;   // $0A
        private byte _refp0;    // $0B
        private byte _refp1;    // $0C

        private byte _pf0;      // $0D
        private byte _pf1;      // $0E
        private byte _pf2;      // $0F

        // --- Audio (TIA) ---
        private byte _audc0;    // $15
        private byte _audc1;    // $16
        private byte _audf0;    // $17
        private byte _audf1;    // $18
        private byte _audv0;    // $19
        private byte _audv1;    // $1A

        private byte _grp0;     // $1B
        private byte _grp1;     // $1C

        // Missiles/Ball enable
        private byte _enam0;    // $1D (bit1)
        private byte _enam1;    // $1E (bit1)
        private byte _enabl;    // $1F
        private byte _resmp0;   // $28 (bit1)
        private byte _resmp1;   // $29 (bit1) (bit1)

        // --- Collision latches (TIA read registers $00-$07) ---
        // Latches are cleared by writing to CXCLR ($2C) and then set again
        // whenever the corresponding objects overlap on a visible pixel.
        private byte _cxm0p;   // D7=M0-P1, D6=M0-P0
        private byte _cxm1p;   // D7=M1-P0, D6=M1-P1
        private byte _cxp0fb;  // D7=P0-PF, D6=P0-BL
        private byte _cxp1fb;  // D7=P1-PF, D6=P1-BL
        private byte _cxm0fb;  // D7=M0-PF, D6=M0-BL
        private byte _cxm1fb;  // D7=M1-PF, D6=M1-BL
        private byte _cxblpf;  // D7=BL-PF
        private byte _cxppmm;  // D7=P0-P1, D6=M0-M1, D5=P0-M1, D4=P0-M0, D3=P1-M0, D2=P1-M1

        // Fine motion registers
        private byte _hmp0;     // $20
        private byte _hmp1;     // $21
        private byte _hmm0;     // $22
        private byte _hmm1;     // $23
        private byte _hmbl;     // $24

        // Vertical delay regs
        // NOTE: On real TIA, VDEL selects the "old" latch values that are
        // updated by writes to GRPx / ENABL (not a once-per-scanline delay).
        private byte _vdelp0;   // $25 (bit0)
        private byte _vdelp1;   // $26 (bit0)
        private byte _vdelbl;   // $27 (bit0)

        // Old latches (TIA updates these when writing GRP/ENABL; VDEL reads from old)
        private byte _grp0Old;
        private byte _grp1Old;
        private byte _enablOld;

        // HMOVE blanking (left 8 pixels) - simplified
        private int _hmoveBlankScanline = -1;
        // Player/Missile/Ball X positions in *visible pixel* space (0..159).
        // River Raid positions objects using RESPx/RESMx/RESBL during HBLANK.
        // If we keep positions in full 228 color-clock space, HBLANK strobes wrap
        // into the visible region and appear as repeated/ghost copies.
        private int _p0x = 20;
        private int _p1x = 100;

        private int _m0x = 40;
        private int _m1x = 120;
        private int _blx = 80;

        // timing counters
        private int _cc;               // color clock within scanline (0..227)
        private int _sl;               // scanline counter
        private bool _wsyncHold;       // WSYNC holds CPU until end of scanline
        private int _frameCounter;     // increments each time scanline wraps (frame)

        // Absolute color-clock time (monotonic). Used to schedule register writes
        // so they take effect on the next color clock (closer to real TIA timing).
        private long _absCc;

        // Most TIA writes become visible aligned to the pixel pipeline.
        // A good first-order model: 1 CPU cycle later => 3 color-clocks.
        // River Raid/Activision kernels rely on this to avoid split glyphs.
        private const int WriteDelayCc = 3;

        private readonly List<PendingWrite> _pendingWrites = new List<PendingWrite>(64);

        private readonly struct PendingWrite
        {
            public readonly long ApplyAt;
            public readonly byte Reg;
            public readonly byte Value;
            public PendingWrite(long applyAt, byte reg, byte value)
            {
                ApplyAt = applyAt;
                Reg = reg;
                Value = value;
            }
        }

        // VBLANK/VSYNC edge helpers
        private bool _vblankPrev;
        private bool _vsyncPrev;
        private bool _vblankFellThisFrame;
        private bool _startFrameNextScanline;

        // --- Audio generation (very small, dependency-free) ---
        // We step the TIA audio at color-clock granularity and resample to 44.1 kHz.
        private const int AudioSampleRate = 44100;
        private const double ColorClockHz = 3579545.0; // NTSC color clock
        private readonly List<short> _audioSamples = new List<short>(1024);
        private double _audioCcAccum;

        // Channel state
        private int _audDiv0, _audDiv1;
        private bool _audOut0, _audOut1;
        private ushort _lfsr4_0 = 0x000F, _lfsr4_1 = 0x000F;
        private ushort _lfsr5_0 = 0x001F, _lfsr5_1 = 0x001F;
        private ushort _lfsr9_0 = 0x01FF, _lfsr9_1 = 0x01FF;

        // Visible geometry (NTSC-ish)
        // River Raid uses part of overscan for HUD/logo; if we hard-crop to 192
        // lines, the bottom HUD may disappear. Use a taller window.
        public const int VisibleWidth = 160;
        public const int VisibleHeight = 230;
        public const int ColorClocksPerScanline = 228;
        public const int TotalScanlinesNTSC = 262;

        // 160 visible pixels correspond well to color clocks 68..227 inclusive (approx).
        private const int VisibleClockStart = 68;   // inclusive
        // Tune: -1/0/+1. For River Raid, 0 is usually closer than -1.
        // River Raid style kernels are extremely sensitive to the exact RESP strobe offset.
        // Make it runtime-tweakable (F3) instead of hard-coding a single value.
        private int _respStrobeOffset = 0;

        // Frame sync can be anchored to VSYNC (better for variable-scanline games) or
        // forced to a fixed 262-scanline cadence (useful for some test ROMs).
        private bool _syncToVsync = true;

        // Safety net if VSYNC never arrives (avoid runaway SL counter).
        private const int MaxScanlinesSafety = 400;
        private const int VisibleClockEnd = 228;    // exclusive => 160 clocks

        // Start the visible window higher so that VisibleHeight can still reach
        // the bottom of the 262-line frame (keeps the HUD/logo visible).
        private const int DefaultVisibleStart = 32;
        private int _visibleStart = DefaultVisibleStart;

        public byte[] FramebufferBgra { get; } = new byte[VisibleWidth * VisibleHeight * 4];

        // Debug toggles
        public bool IgnoreVBlank { get; set; }          // F1
        public bool IgnoreVisibleWindow { get; set; }   // F2

        public bool CpuHaltedByWsync { get { return _wsyncHold; } }

        // Debug exposing
        public int Scanline { get { return _sl; } }
        public int ColorClock { get { return _cc; } }
        public byte VBlankReg { get { return _vblank; } }
        public byte VSyncReg { get { return _vsync; } }
        public byte Colubk { get { return _colubk; } }
        public int VisibleStartScanline { get { return _visibleStart; } }
        public int FrameCounter { get { return _frameCounter; } }

        // Runtime tuning knobs (used by MainForm hotkeys)
        public int RespStrobeOffset { get { return _respStrobeOffset; } }
        public bool SyncToVsync { get { return _syncToVsync; } }
        public string FrameSyncModeLabel { get { return _syncToVsync ? "VSYNC" : "262"; } }

        /// <summary>
        /// Gets and clears the audio samples accumulated since the last call.
        /// Samples are 16-bit PCM, mono, 44.1 kHz.
        /// </summary>
        public short[] ConsumeAudioSamples()
        {
            if (_audioSamples.Count == 0) return Array.Empty<short>();
            var arr = _audioSamples.ToArray();
            _audioSamples.Clear();
            return arr;
        }

        public void CycleRespOffset()
        {
            // Cycle: 0 -> +1 -> +2 -> -1 -> 0
            _respStrobeOffset = _respStrobeOffset switch
            {
                0 => 1,
                1 => 2,
                2 => -1,
                _ => 0
            };
        }

        public void ToggleFrameSyncMode()
        {
            _syncToVsync = !_syncToVsync;
        }

        public Tia(InputState input)
        {
            _input = input;
            ClearToBackground();
            _grp0Old = 0;
            _grp1Old = 0;
            _enablOld = 0;
            _hmoveBlankScanline = -1;
            _frameCounter = 0;
            _absCc = 0;
            _pendingWrites.Clear();

            // Audio init
            _audioSamples.Clear();
            _audioCcAccum = 0;
            _audDiv0 = _audDiv1 = 0;
            _audOut0 = _audOut1 = false;

            ClearCollisions();
        }

        private void ClearCollisions()
        {
            _cxm0p = 0; _cxm1p = 0; _cxp0fb = 0; _cxp1fb = 0;
            _cxm0fb = 0; _cxm1fb = 0; _cxblpf = 0; _cxppmm = 0;
        }

        private void UpdateCollisions(bool p0, bool p1, bool m0, bool m1, bool bl, bool pf)
        {
            // Missile-to-player
            if (m0 && p0) _cxm0p |= 0x40;
            if (m0 && p1) _cxm0p |= 0x80;
            if (m1 && p1) _cxm1p |= 0x40;
            if (m1 && p0) _cxm1p |= 0x80;

            // Player-to-playfield/ball
            if (p0 && pf) _cxp0fb |= 0x80;
            if (p0 && bl) _cxp0fb |= 0x40;
            if (p1 && pf) _cxp1fb |= 0x80;
            if (p1 && bl) _cxp1fb |= 0x40;

            // Missile-to-playfield/ball
            if (m0 && pf) _cxm0fb |= 0x80;
            if (m0 && bl) _cxm0fb |= 0x40;
            if (m1 && pf) _cxm1fb |= 0x80;
            if (m1 && bl) _cxm1fb |= 0x40;

            // Ball-to-playfield
            if (bl && pf) _cxblpf |= 0x80;

            // Player-player / missile-missile / player-missile
            if (p0 && p1) _cxppmm |= 0x80;
            if (m0 && m1) _cxppmm |= 0x40;
            if (p0 && m1) _cxppmm |= 0x20;
            if (p0 && m0) _cxppmm |= 0x10;
            if (p1 && m0) _cxppmm |= 0x08;
            if (p1 && m1) _cxppmm |= 0x04;
        }

        public byte Read(byte reg)
        {
            // Collision latches
            switch (reg)
            {
                case 0x00: return _cxm0p;
                case 0x01: return _cxm1p;
                case 0x02: return _cxp0fb;
                case 0x03: return _cxp1fb;
                case 0x04: return _cxm0fb;
                case 0x05: return _cxm1fb;
                case 0x06: return _cxblpf;
                case 0x07: return _cxppmm;

                // Input ports (only INPT4 used for joystick fire in our current input model)
                // Bit7 is 0 when pressed, 1 when not pressed.
                case 0x0C: // INPT4
                    return _input.P0Fire ? (byte)0x00 : (byte)0x80;
                case 0x0D: // INPT5 (not wired in current model)
                    return 0x80;
            }

            return 0x00;
        }

        public void Write(byte reg, byte value)
        {
            // VSYNC/VBLANK/WSYNC affect frame timing and CPU hold; keep them immediate.
            if (reg == 0x00 || reg == 0x01 || reg == 0x02)
            {
                ApplyWriteImmediate(reg, value);
                return;
            }

            // Most TIA writes take effect aligned to a color-clock boundary.
            // Scheduling them one color clock later fixes mid-scanline composition
            // artifacts (split glyphs/gaps) in River Raid style kernels.
            _pendingWrites.Add(new PendingWrite(_absCc + WriteDelayCc, reg, value));
        }

        private void ApplyPendingWritesForThisClock()
        {
            if (_pendingWrites.Count == 0) return;

            // Apply in insertion order; list is tiny.
            int writeIndex = 0;
            while (writeIndex < _pendingWrites.Count)
            {
                var w = _pendingWrites[writeIndex];
                if (w.ApplyAt > _absCc) break;
                ApplyWriteImmediate(w.Reg, w.Value);
                writeIndex++;
            }

            if (writeIndex > 0)
                _pendingWrites.RemoveRange(0, writeIndex);
        }

        private void ApplyWriteImmediate(byte reg, byte value)
        {
            switch (reg)
            {
                case 0x00: // VSYNC
                    {
                        bool vsNow = (value & 0x02) != 0;
                        // Use VSYNC to anchor the start of a new frame.
                        // Many games (incl. River Raid) vary total scanlines; if we
                        // only reset on a fixed 262-count, the image will roll/drift.
                        if (_syncToVsync && _vsyncPrev && !vsNow)
                        {
                            _startFrameNextScanline = true;
                            _vblankFellThisFrame = false;
                            _visibleStart = DefaultVisibleStart;
                        }
                        _vsyncPrev = vsNow;
                        _vsync = value;
                        break;
                    }

                case 0x01: // VBLANK
                    {
                        bool vbNow = (value & 0x02) != 0;
                        if (_vblankPrev && !vbNow)
                        {
                            if (!_vblankFellThisFrame && _sl < 120)
                            {
                                _visibleStart = _sl;
                                _vblankFellThisFrame = true;
                            }
                        }
                        _vblankPrev = vbNow;
                        _vblank = value;
                        break;
                    }

                case 0x02: // WSYNC
                    _wsyncHold = true;
                    break;

                case 0x04: _nusiz0 = value; break;
                case 0x05: _nusiz1 = value; break;

                case 0x06: _colup0 = value; break;
                case 0x07: _colup1 = value; break;
                case 0x08: _colupf = value; break;
                case 0x09: _colubk = value; break;
                case 0x0A: _ctrlpf = value; break;
                case 0x0B: _refp0 = value; break;
                case 0x0C: _refp1 = value; break;

                case 0x0D: _pf0 = value; break;
                case 0x0E: _pf1 = value; break;
                case 0x0F: _pf2 = value; break;

                // Audio registers
                case 0x15: _audc0 = (byte)(value & 0x0F); break; // AUDC0
                case 0x16: _audc1 = (byte)(value & 0x0F); break; // AUDC1
                case 0x17: _audf0 = (byte)(value & 0x1F); break; // AUDF0
                case 0x18: _audf1 = (byte)(value & 0x1F); break; // AUDF1
                case 0x19: _audv0 = (byte)(value & 0x0F); break; // AUDV0
                case 0x1A: _audv1 = (byte)(value & 0x0F); break; // AUDV1

                case 0x10: // RESP0
                    _p0x = CurrentVisibleXForStrobe();
                    if ((_resmp0 & 0x02) != 0) _m0x = CenterMissileOnPlayer(_p0x, _nusiz0);
                    break;
                case 0x11: // RESP1
                    _p1x = CurrentVisibleXForStrobe();
                    if ((_resmp1 & 0x02) != 0) _m1x = CenterMissileOnPlayer(_p1x, _nusiz1);
                    break;
                case 0x12: _m0x = CurrentVisibleXForStrobe(); break; // RESM0
                case 0x13: _m1x = CurrentVisibleXForStrobe(); break; // RESM1
                case 0x14: _blx = CurrentVisibleXForStrobe(); break; // RESBL

                // TIA "scoreboard" side-effects:
                // - Writing GRP0 also copies current GRP1 into the GRP1 old latch.
                // - Writing GRP1 also copies current GRP0 into the GRP0 old latch and
                //   copies current ENABL into the ENABL old latch.
                // VDEL reads from these *old* latches.
                case 0x1B: // GRP0
                    _grp0 = value;
                    _grp1Old = _grp1;
                    break;
                case 0x1C: // GRP1
                    _grp1 = value;
                    _grp0Old = _grp0;
                    _enablOld = _enabl;
                    break;
                case 0x1D: _enam0 = value; break; // ENAM0
                case 0x1E: _enam1 = value; break; // ENAM1
                case 0x1F: _enabl = value; break; // ENABL

                case 0x20: _hmp0 = value; break;
                case 0x21: _hmp1 = value; break;
                case 0x22: _hmm0 = value; break;
                case 0x23: _hmm1 = value; break;
                case 0x24: _hmbl = value; break;

                case 0x25: _vdelp0 = value; break; // VDELP0
                case 0x26: _vdelp1 = value; break; // VDELP1
                case 0x27: _vdelbl = value; break; // VDELBL

                case 0x28: // RESMP0 (bit1)
                    _resmp0 = value;
                    if ((_resmp0 & 0x02) != 0)
                        _m0x = CenterMissileOnPlayer(_p0x, _nusiz0);
                    break;
                case 0x29: // RESMP1 (bit1)
                    _resmp1 = value;
                    if ((_resmp1 & 0x02) != 0)
                        _m1x = CenterMissileOnPlayer(_p1x, _nusiz1);
                    break;

                case 0x2A: // HMOVE
                    // Apply fine motion within the visible 160-pixel domain.
                    _p0x = WrapVisible(_p0x + DecodeHMove(_hmp0));
                    _p1x = WrapVisible(_p1x + DecodeHMove(_hmp1));
                    _m0x = WrapVisible(_m0x + DecodeHMove(_hmm0));
                    _m1x = WrapVisible(_m1x + DecodeHMove(_hmm1));
                    _blx = WrapVisible(_blx + DecodeHMove(_hmbl));
                    break;

                case 0x2B: // HMCLR
                    _hmp0 = _hmp1 = _hmm0 = _hmm1 = _hmbl = 0x00;
                    break;

                case 0x2C: // CXCLR - clear collision latches
                    ClearCollisions();
                    break;
            }
        }

        public void TickColorClock()
        {
            // If VSYNC just ended, start a new frame on the next scanline boundary.
            if (_syncToVsync && _cc == 0 && _startFrameNextScanline)
            {
                _startFrameNextScanline = false;
                _sl = 0;
                _frameCounter++;
                _vblankFellThisFrame = false;
                _visibleStart = DefaultVisibleStart;
            }

            ApplyPendingWritesForThisClock();
            TickAudioForThisClock();
            RenderPixelIfVisible();
            _cc++;
            _absCc++;
            if (_cc >= ColorClocksPerScanline)
            {
                _cc = 0;
                _sl++;

                _wsyncHold = false;
                if (!_syncToVsync)
                {
                    if (_sl >= TotalScanlinesNTSC)
                    {
                        _sl = 0;
                        _frameCounter++;
                        _vblankFellThisFrame = false;
                        _visibleStart = DefaultVisibleStart;
                    }
                }
                else
                {
                    // Safety net only; real frame boundaries come from VSYNC edges.
                    if (_sl >= MaxScanlinesSafety)
                    {
                        _sl = 0;
                        _frameCounter++;
                        _vblankFellThisFrame = false;
                        _visibleStart = DefaultVisibleStart;
                    }
                }
            }
        }

        private static int DecodeHMove(byte hm)
        {
            int n = (hm >> 4) & 0x0F;
            if (n >= 8) n -= 16; // -8..+7
            return -n;// TIA HM values move opposite to signed nibble in our pixel domain
        }

        // Wrap a color-clock position within the full scanline domain (0..227).
        private static int WrapCc(int cc)
        {
            cc %= ColorClocksPerScanline;
            if (cc < 0) cc += ColorClocksPerScanline;
            return cc;
        }

        // Wrap a visible-pixel X within the 160-pixel visible domain (0..159).
        private static int WrapVisible(int x)
        {
            x %= VisibleWidth;
            if (x < 0) x += VisibleWidth;
            return x;
        }

        private int CurrentVisibleX()
        {
            // Map current beam position into 0..159 visible pixel space.
            // If we're in HBLANK, treat it as X=0 (RESP during HBLANK anchors to left).
            int x = _cc - VisibleClockStart;
            if (x < 0) x = 0;
            if (x >= VisibleWidth) x = VisibleWidth - 1;
            return x;
        }

        private int CurrentVisibleXForStrobe()
        {
            // RESPx/RESMx/RESBL strobes set horizontal positions relative to the
            // visible window. Modeling in 0..159 avoids ghost copies caused by
            // 228-clock wrapping when the strobe happens in HBLANK.
            return WrapVisible(CurrentVisibleX() + RespStrobeOffset);
        }

        // ---------------------- AUDIO ----------------------
        // TIA audio shifts at either pixelclock/114 (~31.44kHz NTSC) or CPUclock/114 (~10.48kHz NTSC).
        // In color-clock units (pixel clock), that is every 114 CCs or every 342 CCs.
        private int _audBaseDiv0 = 114;
        private int _audBaseDiv1 = 114;

        // Clock modifier state (AUDC D1:D0):
        //  - div31 is an approximation of the irregular /31 divider described in classic TIASOUND notes.
        //  - poly5mod is used when the clock modifier is "5-bit polynomial".
        private int _audDiv31_0 = 1;
        private int _audDiv31_1 = 1;
        private ushort _lfsr5mod_0 = 0x1F;
        private ushort _lfsr5mod_1 = 0x1F;

        private void TickAudioForThisClock()
        {
            StepAudioChannel(ref _audBaseDiv0, ref _audDiv0, ref _audDiv31_0, ref _audOut0, ref _lfsr4_0, ref _lfsr5_0, ref _lfsr9_0, ref _lfsr5mod_0, _audc0, _audf0);
            StepAudioChannel(ref _audBaseDiv1, ref _audDiv1, ref _audDiv31_1, ref _audOut1, ref _lfsr4_1, ref _lfsr5_1, ref _lfsr9_1, ref _lfsr5mod_1, _audc1, _audf1);

            // Resample: emit one 44.1kHz sample every ~81.18 color-clocks.
            _audioCcAccum += 1.0;
            double ccPerSample = ColorClockHz / AudioSampleRate;
            while (_audioCcAccum >= ccPerSample)
            {
                _audioCcAccum -= ccPerSample;
                _audioSamples.Add(MixAudioSample());
            }
        }

        private short MixAudioSample()
        {
            int v0 = (_audv0 & 0x0F);
            int v1 = (_audv1 & 0x0F);

            // True silence when both volumes are zero.
            if (v0 == 0 && v1 == 0) return 0;

            // TIA channels are unipolar (0..V). Use the expected mean (V/2 per channel)
            // as the DC baseline to avoid constant background crackle/hiss.
            int s0 = _audOut0 ? v0 : 0;
            int s1 = _audOut1 ? v1 : 0;

            // signed2 keeps half-step precision in integer math:
            // baseline = (v0/2 + v1/2) = (v0 + v1) / 2
            // signed = (s0+s1) - baseline
            int signed2 = (2 * (s0 + s1)) - (v0 + v1); // roughly -30..+30

            // Scale to 16-bit with headroom.
            int pcm = signed2 * 900; // ~ -27000..+27000
            if (pcm > short.MaxValue) pcm = short.MaxValue;
            if (pcm < short.MinValue) pcm = short.MinValue;
            return (short)pcm;
        }

        private static void StepAudioChannel(
            ref int baseDiv,
            ref int freqDiv,
            ref int div31Counter,
            ref bool outBit,
            ref ushort lfsr4,
            ref ushort lfsr5,
            ref ushort lfsr9,
            ref ushort lfsr5mod,
            byte audc,
            byte audf)
        {
            int mode = audc & 0x0F;

            // Select base clock per distortion group.
            // From the classic VCS sound guide: modes 12-15 use CPUclock/114 (~10.48kHz NTSC), others use pixelclock/114 (~31.44kHz NTSC).
            int basePeriodCc = (mode >= 12) ? 342 : 114;
            if (baseDiv <= 0) baseDiv = basePeriodCc;

            // Count color clocks down to a "base" audio shift.
            baseDiv--;
            if (baseDiv > 0) return;
            baseDiv = basePeriodCc;

            // Now apply AUDF divider on top of the base shift.
            int period = (audf & 0x1F) + 1;
            if (freqDiv <= 0) freqDiv = period;

            freqDiv--;
            if (freqDiv > 0) return;
            freqDiv = period;

            // Implement the AUDC logic following the classic TIASOUND description:
            //  - Bits D1:D0 select the clock modifier (none, /31, 5-bit poly)
            //  - Bits D3:D2 select the source pattern (4-bit poly, 5-bit poly, or "pure" toggling)
            //  - Special cases: AUDC=0 => constant high (volume-only), AUDC=8 => 9-bit poly, AUDC=B => constant high.

            // Special cases
            if (mode == 0 || mode == 0x0B)
            {
                outBit = true;
                return;
            }

            // Apply clock modifier (D1:D0)
            bool clockTick;
            int clockMod = mode & 0x03;
            switch (clockMod)
            {
                case 0:
                case 1:
                    clockTick = true;
                    break;
                case 2:
                    // Approximate the irregular /31 divider with a simple 31-step counter.
                    if (div31Counter <= 0) div31Counter = 31;
                    div31Counter--;
                    clockTick = (div31Counter == 0);
                    break;
                default: // 3 => 5-bit polynomial clock modifier
                    lfsr5mod = NextLfsr(lfsr5mod, 5, 2);
                    clockTick = (lfsr5mod & 0x0001) != 0;
                    break;
            }

            if (!clockTick)
                return;

            // Select source pattern (D3:D2)
            if (mode == 0x08)
            {
                // 9-bit poly (white noise)
                lfsr9 = NextLfsr(lfsr9, 9, 5);
                outBit = (lfsr9 & 0x0001) != 0;
                return;
            }

            int sourceSel = (mode >> 2) & 0x03;
            switch (sourceSel)
            {
                case 0: // 4-bit poly
                    lfsr4 = NextLfsr(lfsr4, 4, 1);
                    outBit = (lfsr4 & 0x0001) != 0;
                    return;
                case 1: // pure (~Q)
                    outBit = !outBit;
                    return;
                case 2: // 5-bit poly
                    lfsr5 = NextLfsr(lfsr5, 5, 2);
                    outBit = (lfsr5 & 0x0001) != 0;
                    return;
                default: // 3 => pure (~Q)
                    outBit = !outBit;
                    return;
            }
        }

        private static ushort NextLfsr(ushort state, int bits, int tap)
        {
            // Generic Fibonacci LFSR with simple XOR taps.
            ushort mask = (ushort)((1 << bits) - 1);
            int b0 = state & 1;
            int bt = (state >> tap) & 1;
            int newBit = (b0 ^ bt) & 1;
            state >>= 1;
            state |= (ushort)(newBit << (bits - 1));
            state &= mask;
            if (state == 0) state = mask; // avoid lockup
            return state;
        }

        private int CenterMissileOnPlayer(int playerCc, byte nusiz)
{
    // On real TIA, RESMPx aligns the missile a few color-clocks to the right of the player.
    // Using 4*sizeMul matches common kernels (River Raid, etc.) much better.
    int mode = nusiz & 0x07;
    int sizeMul = 1;
    if (mode == 5) sizeMul = 2;       // double sized player
    else if (mode == 7) sizeMul = 4;  // quad sized player

    int offset = 4 * sizeMul;
    return WrapVisible(playerCc + offset);
}


        private void ClearToBackground()
        {
            uint argb = TiaPalette.ArgbFromTia(_colubk);
            byte b = (byte)(argb & 0xFF);
            byte g = (byte)((argb >> 8) & 0xFF);
            byte r = (byte)((argb >> 16) & 0xFF);

            for (int i = 0; i < FramebufferBgra.Length; i += 4)
            {
                FramebufferBgra[i + 0] = b;
                FramebufferBgra[i + 1] = g;
                FramebufferBgra[i + 2] = r;
                FramebufferBgra[i + 3] = 0xFF;
            }
        }

        private void RenderPixelIfVisible()
        {
            if (!IgnoreVBlank && (_vblank & 0x02) != 0) return;

            // Visible window start is set either to the default (covers full frame)
            // or to the scanline where VBLANK falls (when the game provides it).
            // Do not "auto-lock" to scanline 0 when VBLANK starts low; that can
            // crop the bottom HUD/logo in River Raid.

            int y;
            if (!IgnoreVisibleWindow)
            {
                int start = _visibleStart;
                int end = start + VisibleHeight;
                if (_sl < start || _sl >= end) return;
                y = _sl - start;
            }
            else
            {
                if (_sl >= VisibleHeight) return;
                y = _sl;
            }

            if (_cc < VisibleClockStart || _cc >= VisibleClockEnd) return;
            int x = _cc - VisibleClockStart;

            bool pfOn = IsPlayfieldDotOn(x);

            bool scoreMode = (_ctrlpf & 0x02) != 0;
            bool pfPriority = (_ctrlpf & 0x04) != 0;

            uint bg = TiaPalette.ArgbFromTia(_colubk);

            uint pfColor;
            if (pfOn)
            {
                if (scoreMode)
                {
                    pfColor = (x < 80)
                        ? TiaPalette.ArgbFromTia(_colup0)
                        : TiaPalette.ArgbFromTia(_colup1);
                }
                else
                {
                    pfColor = TiaPalette.ArgbFromTia(_colupf);
                }
            }
            else
            {
                pfColor = bg;
            }
            // Objects are rendered in the visible 160-pixel domain.
            bool p0 = IsPlayer0PixelOn(x);
            bool p1 = IsPlayer1PixelOn(x);
            bool m0 = IsMissile0On(x);
            bool m1 = IsMissile1On(x);
            bool bl = IsBallOn(x);

            // Collision latches are updated based on *logical* object overlap,
            // independent of priority/score-mode color output.
            UpdateCollisions(p0, p1, m0, m1, bl, pfOn);

// Ball uses COLUPF
uint ballColor = TiaPalette.ArgbFromTia(_colupf);

// Object colors
uint p0c = TiaPalette.ArgbFromTia(_colup0);
uint p1c = TiaPalette.ArgbFromTia(_colup1);

// Base pixel is playfield color (or background if PF off)
uint outc = pfColor;

if (pfPriority)
{
    // PF/Ball over objects
    if (bl) outc = ballColor;

    if (!pfOn && !bl)
    {
        // PF group off: objects can show
        if (p0 || m0) outc = p0c;
        if (p1 || m1) outc = p1c;
    }
}
else
{
    // Objects over PF/Ball
    if (bl) outc = ballColor;
    if (p0 || m0) outc = p0c;
    if (p1 || m1) outc = p1c;
}

WriteBgraPixel(x, y, outc);

}

        private void WriteBgraPixel(int x, int y, uint argb)
        {
            int i = (y * VisibleWidth + x) * 4;
            FramebufferBgra[i + 0] = (byte)(argb & 0xFF);
            FramebufferBgra[i + 1] = (byte)((argb >> 8) & 0xFF);
            FramebufferBgra[i + 2] = (byte)((argb >> 16) & 0xFF);
            FramebufferBgra[i + 3] = 0xFF;
        }

        private bool IsPlayfieldDotOn(int pixelX)
        {
            // Playfield is 20 bits per half, each bit spans 4 visible pixels.
            // Keep the mapping strictly aligned to pixelX/4.
            int dot = pixelX >> 2; // 0..39
            if (dot < 0) dot = 0;
            if (dot > 39) dot = 39;
            int halfDot = dot < 20 ? dot : dot - 20;

            bool reflect = (_ctrlpf & 0x01) != 0;
            if (dot >= 20)
                halfDot = reflect ? (19 - halfDot) : halfDot;

            if (halfDot < 4)
            {
                // PF0 uses bits 4..7. Display order is reversed relative to the byte.
                // Most kernels assume PF0 bit4 is the rightmost of the PF0 nibble.
                // Mapping: halfDot 0..3 -> PF0 bits 4..7 (LSB->MSB)
                int bit = 4 + halfDot; // 4,5,6,7
                return ((_pf0 >> bit) & 1) != 0;
            }
            if (halfDot < 12)
            {
                int i = halfDot - 4;  // 0..7
                int bit = 7 - i;      // PF1 MSB first
                return ((_pf1 >> bit) & 1) != 0;
            }

            int j = halfDot - 12;     // 0..7
            return ((_pf2 >> j) & 1) != 0; // PF2 LSB first
        }

        private static IEnumerable<int> GetCopiesBaseX(int mode, int x)
{
    // NUSIZx bits 0-2 mapping (Player/Missile copy spacing + player size encoding):
    // 0: one copy
    // 1: two copies - close        (x, x+16)
    // 2: two copies - medium       (x, x+32)
    // 3: three copies - close      (x, x+16, x+32)
    // 4: two copies - wide         (x, x+64)
    // 5: double sized player       (single copy, size handled elsewhere)
    // 6: three copies - medium     (x, x+32, x+64)
    // 7: quad sized player         (single copy, size handled elsewhere)
    mode &= 7;

	    x = WrapVisible(x);
	    yield return x;

	    if (mode == 1) { yield return WrapVisible(x + 16); yield break; }
	    if (mode == 2) { yield return WrapVisible(x + 32); yield break; }
	    if (mode == 3) { yield return WrapVisible(x + 16); yield return WrapVisible(x + 32); yield break; }
	    if (mode == 4) { yield return WrapVisible(x + 64); yield break; }
	    if (mode == 6) { yield return WrapVisible(x + 32); yield return WrapVisible(x + 64); yield break; }
}


        private static int SizeMulFromNusiz(byte nusiz)
        {
            int sizeBits = (nusiz >> 4) & 0x03;
            if (sizeBits == 1) return 2;
            if (sizeBits == 2) return 4;
            return 1;
        }

        private static bool PlayerPixelOn(int pixelX, int baseX, byte grp, byte refp, byte nusiz)
        {
            int sizeMul = SizeMulFromNusiz(nusiz);

            // Horizontal positions wrap within the 160-pixel visible range.
            int dx = pixelX - baseX;
            if (dx < 0) dx += VisibleWidth;

            int width = 8 * sizeMul;
            if (dx >= width) return false;

            int bitX = dx / sizeMul; // 0..7

            bool reflect = (refp & 0x08) != 0;
            int bitIndex = reflect ? bitX : (7 - bitX);

            return ((grp >> bitIndex) & 1) != 0;
        }


private int MissileWidthFromNusiz(byte nusiz)
{
    // Missile size is NUSIZx bits 4..5:
    // 00=1, 01=2, 10=4, 11=8
    int s = (nusiz >> 4) & 0x03;
    return (s == 1) ? 2 : (s == 2) ? 4 : (s == 3) ? 8 : 1;
}
        private bool IsPlayer0PixelOn(int pixelX)
{
    // VDELP0 selects the "old" latch captured on the previous GRP0 write.
    byte grp = (((_vdelp0 & 0x01) != 0) ? _grp0Old : _grp0);
    if (grp == 0) return false;

    int mode = _nusiz0 & 0x07;

    int sizeMul = 1;
    if (mode == 5) sizeMul = 2;       // double sized player
    else if (mode == 7) sizeMul = 4;  // quad sized player

    bool reflect = (_refp0 & 0x08) != 0;

    foreach (int baseX in GetCopiesBaseX(mode, _p0x))
    {
        int dx = pixelX - baseX;
        if (dx < 0) dx += VisibleWidth;

        int bitIndex = dx / sizeMul; // 0..7
        if (bitIndex >= 8) continue;

        int b = reflect ? bitIndex : (7 - bitIndex);
        if (((grp >> b) & 1) != 0) return true;
    }

    return false;
}


        private bool IsPlayer1PixelOn(int pixelX)
{
    // VDELP1 selects the "old" latch captured on the previous GRP1 write.
    byte grp = (((_vdelp1 & 0x01) != 0) ? _grp1Old : _grp1);
    if (grp == 0) return false;

    int mode = _nusiz1 & 0x07;

    int sizeMul = 1;
    if (mode == 5) sizeMul = 2;       // double sized player
    else if (mode == 7) sizeMul = 4;  // quad sized player

    bool reflect = (_refp1 & 0x08) != 0;

    foreach (int baseX in GetCopiesBaseX(mode, _p1x))
    {
        int dx = pixelX - baseX;
        if (dx < 0) dx += VisibleWidth;

        int bitIndex = dx / sizeMul; // 0..7
        if (bitIndex >= 8) continue;

        int b = reflect ? bitIndex : (7 - bitIndex);
        if (((grp >> b) & 1) != 0) return true;
    }

    return false;
}


        private bool IsMissile0On(int pixelX)
{
    if ((_enam0 & 0x02) == 0) return false;

    int mode = _nusiz0 & 0x07;
    int w = MissileWidthFromNusiz(_nusiz0);

    if (mode == 5 || mode == 7)
    {
        int dx0 = pixelX - _m0x;
        if (dx0 < 0) dx0 += VisibleWidth;
        return dx0 < w;
    }

    foreach (int baseX in GetCopiesBaseX(mode, _m0x))
    {
        int dx = pixelX - baseX;
        if (dx < 0) dx += VisibleWidth;
        if (dx < w) return true;
    }
    return false;
}


        private bool IsMissile1On(int pixelX)
{
    if ((_enam1 & 0x02) == 0) return false;

    int mode = _nusiz1 & 0x07;
    int w = MissileWidthFromNusiz(_nusiz1);

    if (mode == 5 || mode == 7)
    {
        int dx0 = pixelX - _m1x;
        if (dx0 < 0) dx0 += VisibleWidth;
        return dx0 < w;
    }

    foreach (int baseX in GetCopiesBaseX(mode, _m1x))
    {
        int dx = pixelX - baseX;
        if (dx < 0) dx += VisibleWidth;
        if (dx < w) return true;
    }
    return false;
}


        private bool IsBallOn(int pixelX)
{
    // VDELBL selects the "old" ENABL latch captured on the previous ENABL write.
    byte en = ((_vdelbl & 0x01) != 0) ? _enablOld : _enabl;
    if ((en & 0x02) == 0) return false;

    // CTRLPF bits 4..5: ball size (1,2,4,8)
    int bs = (_ctrlpf >> 4) & 0x03;
    int w = (bs == 1) ? 2 : (bs == 2) ? 4 : (bs == 3) ? 8 : 1;

    int dx = pixelX - _blx;
    if (dx < 0) dx += VisibleWidth;
    return dx < w;
}
    }

    public static class TiaPalette
{
    // NTSC-ish YIQ conversion.
    // This is not a perfect CRT simulation, but it's tuned to match common
    // emulator palettes better (less magenta shift, more accurate blues/greys).
    //
    // Notes:
    // - TIA color is effectively 7-bit; bit0 is ignored on hardware. We mask it.
    // - Hue 0 behaves like a grayscale ramp.
    public static uint ArgbFromTia(byte tiaColor)
    {
        // Ignore bit0 to mimic the real TIA DAC behavior.
        byte c = (byte)(tiaColor & 0xFE);

        int hue = (c >> 4) & 0x0F;
        int lum = c & 0x0F;

        // Luma: 0..15 -> 0..1.
        // Keep a small pedestal and a mild gamma so dark levels don't crush.
        double y = lum / 15.0;
        y = 0.06 + Math.Pow(y, 1.05) * 0.94;

        if (hue == 0)
        {
            byte g = (byte)Math.Clamp((int)(y * 255.0), 0, 255);
            return 0xFF000000u | ((uint)g << 16) | ((uint)g << 8) | g;
        }

        // Chroma: keep it lower in dark luma levels.
        // The previous tuning was too saturated and shifted toward magenta.
        double sat = 0.42 * (0.25 + 0.75 * y);

        // 16 hue steps. Use a small negative phase offset to better align
        // River Raid's sky/river hues with typical NTSC palettes.
        double ang = ((hue * 22.5) - 10.0) * Math.PI / 180.0;
        double i = sat * Math.Cos(ang);
        double q = sat * Math.Sin(ang);

        // YIQ -> RGB
        double r = y + 0.956 * i + 0.621 * q;
        double g2 = y - 0.272 * i - 0.647 * q;
        double b = y - 1.106 * i + 1.703 * q;

        byte rr = ToByte01(r);
        byte gg = ToByte01(g2);
        byte bb = ToByte01(b);

        return 0xFF000000u | ((uint)rr << 16) | ((uint)gg << 8) | bb;
    }

    private static byte ToByte01(double v)
    {
        if (v < 0) v = 0;
        if (v > 1) v = 1;
        return (byte)Math.Max(0, Math.Min(255, (int)(v * 255.0)));
    }
}

}
