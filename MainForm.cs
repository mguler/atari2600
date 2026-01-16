using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Atari2600Emu;

public sealed class MainForm : Form
{
    private Atari2600 _emu;
    private readonly Bitmap _bmp;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly WinMmAudioOutput? _audio;

    private int _frames;
    private long _lastFpsTick = Environment.TickCount64;
    private int _fps;

    public MainForm(Atari2600 emu)
    {
        _emu = emu;

        // Start audio output (mono 44.1kHz). If this fails, the emulator still runs.
        try
        {
            _audio = new WinMmAudioOutput();
        }
        catch
        {
            _audio = null!;
        }

        Text = "Atari 2600 - RIOT Timer + Video Debug (F1/F2) - drop ROM";
        ClientSize = new Size(Tia.VisibleWidth * 3, Tia.VisibleHeight * 3);
        DoubleBuffered = true;

        KeyPreview = true;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        _bmp = new Bitmap(Tia.VisibleWidth, Tia.VisibleHeight, PixelFormat.Format32bppArgb);

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, __) =>
        {
            _emu.RunFrameNtsc();

            // Feed audio (if available). Always consume to avoid unbounded growth.
            var samples = _emu.Bus.Tia.ConsumeAudioSamples();
            if (_audio != null && samples.Length > 0)
                _audio.Enqueue(samples);

            Blit();

            _frames++;
            long now = Environment.TickCount64;
            if (now - _lastFpsTick >= 1000)
            {
                _fps = _frames;
                _frames = 0;
                _lastFpsTick = now;
            }

            Invalidate();
        };
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Stop();
            if (_audio != null)
            {
                try { _audio.Dispose(); } catch { }
            }
            _bmp?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var input = _emu.Bus.Input;

        switch (e.KeyCode)
        {
            case Keys.Up: input.P0Up = true; break;
            case Keys.Down: input.P0Down = true; break;
            case Keys.Left: input.P0Left = true; break;
            case Keys.Right: input.P0Right = true; break;

            case Keys.Z:
            case Keys.Space:
                input.P0Fire = true; break;

            case Keys.Enter:
                input.ResetPressed = true; break;
            case Keys.Tab:
                input.SelectPressed = true; break;

            case Keys.C:
                input.ColorBwIsBw = !input.ColorBwIsBw; break;

            // Video debug toggles
            case Keys.F1:
                _emu.Bus.Tia.IgnoreVBlank = !_emu.Bus.Tia.IgnoreVBlank;
                break;
            case Keys.F2:
                _emu.Bus.Tia.IgnoreVisibleWindow = !_emu.Bus.Tia.IgnoreVisibleWindow;
                break;

            // Runtime tuning (River Raid kernel helpers)
            case Keys.F3:
                _emu.Bus.Tia.CycleRespOffset();
                break;
            case Keys.F4:
                _emu.Bus.Tia.ToggleFrameSyncMode();
                break;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var input = _emu.Bus.Input;

        switch (e.KeyCode)
        {
            case Keys.Up: input.P0Up = false; break;
            case Keys.Down: input.P0Down = false; break;
            case Keys.Left: input.P0Left = false; break;
            case Keys.Right: input.P0Right = false; break;

            case Keys.Z:
            case Keys.Space:
                input.P0Fire = false; break;

            case Keys.Enter:
                input.ResetPressed = false; break;
            case Keys.Tab:
                input.SelectPressed = false; break;
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                var path = files[0];
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is not ".bin" and not ".a26" and not ".rom")
                {
                    MessageBox.Show("Please drop a .bin/.a26/.rom ROM file.");
                    return;
                }

                var rom = File.ReadAllBytes(path);
                _emu = new Atari2600(new Cartridge(rom));
                Text = $"Atari 2600 - {Path.GetFileName(path)}";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ROM Load Error");
        }
    }

    private void Blit()
    {
        var rect = new Rectangle(0, 0, _bmp.Width, _bmp.Height);
        var data = _bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(_emu.Bus.Tia.FramebufferBgra, 0, data.Scan0, _emu.Bus.Tia.FramebufferBgra.Length);
        _bmp.UnlockBits(data);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.DrawImage(_bmp, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));

        var cpu = _emu.Cpu;
        var tia = _emu.Bus.Tia;
        var riot = _emu.Bus.Riot;
        var cart = _emu.Bus.Cart;

        byte swcha = riot.Read(0x0280);
        byte swchb = riot.Read(0x0282);
        byte intim = riot.Read(0x0284);
        byte instat = riot.Read(0x0285);

        string line1 = $"FPS:{_fps}  Frame:{tia.FrameCounter}  SL:{tia.Scanline} CC:{tia.ColorClock}";
        string line2 = $"PC:{cpu.PC:X4} A:{cpu.A:X2} X:{cpu.X:X2} Y:{cpu.Y:X2} SP:{cpu.SP:X2} P:{(byte)cpu.P:X2}";
        string line3 = $"SWCHA:{swcha:X2} SWCHB:{swchb:X2}  INTIM:{intim:X2} INSTAT:{instat:X2}";
        string line4 = $"VBLANK:{tia.VBlankReg:X2} VSYNC:{tia.VSyncReg:X2} COLUBK:{tia.Colubk:X2}  VStart:{tia.VisibleStartScanline}  ROM:{cart.Rom.Length}B";
        string line5 = $"VideoDbg: IgnoreVBlank(F1)={tia.IgnoreVBlank}  IgnoreWindow(F2)={tia.IgnoreVisibleWindow}  RespOff(F3)={tia.RespStrobeOffset}  Sync(F4)={tia.FrameSyncModeLabel}";
        string line6 = $"LastOp:{cpu.LastOpcode:X2} @ {cpu.LastOpPC:X4}  UnknownOps:{cpu.UnknownOpcodeCount}";
        string line7 = $"ResetVec:{cpu.ResetVector:X4}  Patched:{cpu.ResetVectorWasPatched}";

        using var bg = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        using var fg = new SolidBrush(Color.White);
        using var font = new Font("Consolas", 10, FontStyle.Regular);

        var pad = 6;
        var boxH = 7 * (int)font.GetHeight() + pad * 2;
        e.Graphics.FillRectangle(bg, 8, 8, 980, boxH);

        float y = 8 + pad;
        e.Graphics.DrawString(line1, font, fg, 8 + pad, y); y += font.GetHeight();
        e.Graphics.DrawString(line2, font, fg, 8 + pad, y); y += font.GetHeight();
        e.Graphics.DrawString(line3, font, fg, 8 + pad, y); y += font.GetHeight();
        e.Graphics.DrawString(line4, font, fg, 8 + pad, y); y += font.GetHeight();
        e.Graphics.DrawString(line5, font, fg, 8 + pad, y); y += font.GetHeight();
        e.Graphics.DrawString(line6, font, fg, 8 + pad, y); y += font.GetHeight();
        e.Graphics.DrawString(line7, font, fg, 8 + pad, y);
    }
}
