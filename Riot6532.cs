using System;

namespace Atari2600Emu;

/// <summary>
/// RIOT/PIA subset:
/// - SWCHA ($0280): joystick directions (active low)
/// - SWCHB ($0282): console switches (active low)
/// - Timer: INTIM ($0284), INSTAT ($0285), TIM1T/TIM8T/TIM64T/T1024T ($0294-$0297)
/// </summary>
public sealed class Riot6532
{
    private readonly InputState _input;

    private byte _swacnt; // DDR A
    private byte _swbcnt; // DDR B

    // Timer state
    private byte _intim;          // current timer value (8-bit)
    private bool _timerRunning;
    private bool _underflow;      // sets INSTAT bit7 when underflow occurred
    private int _prescale;        // 1,8,64,1024 (cycles per decrement)
    private int _prescaleCounter; // counts down CPU cycles until next decrement

    public Riot6532(InputState input) => _input = input ?? throw new ArgumentNullException(nameof(input));

    /// <summary>
    /// Call once per CPU cycle (Phi2).
    /// </summary>
    public void TickCpuCycle()
    {
        if (!_timerRunning) return;

        _prescaleCounter--;
        if (_prescaleCounter > 0) return;

        _prescaleCounter = _prescale;

        // Decrement INTIM. When it underflows, hardware keeps counting (wraps) and sets underflow flag.
        if (_intim == 0x00)
        {
            _intim = 0xFF;
            _underflow = true;
        }
        else
        {
            _intim--;
        }
    }

    public byte Read(ushort addr)
    {
        addr &= 0x1FFF;

        return (addr & 0x00FF) switch
        {
            0x80 => ReadSWCHA(),
            0x81 => _swacnt,
            0x82 => ReadSWCHB(),
            0x83 => _swbcnt,
            0x84 => _intim,                                // INTIM
            0x85 => (byte)(_underflow ? 0x80 : 0x00),      // INSTAT (bit7)
            _ => 0x00
        };
    }

    public void Write(ushort addr, byte value)
    {
        addr &= 0x1FFF;

        switch (addr & 0x00FF)
        {
            case 0x81: _swacnt = value; break;
            case 0x83: _swbcnt = value; break;

            // Timer writes (set prescaler + load INTIM)
            case 0x94: StartTimer(value, 1); break;     // TIM1T
            case 0x95: StartTimer(value, 8); break;     // TIM8T
            case 0x96: StartTimer(value, 64); break;    // TIM64T
            case 0x97: StartTimer(value, 1024); break;  // T1024T

            default:
                break;
        }
    }

    private void StartTimer(byte value, int prescale)
    {
        _intim = value;
        _prescale = prescale;
        _prescaleCounter = prescale;
        _underflow = false;
        _timerRunning = true;
    }

    /// <summary>
    /// SWCHA layout:
    /// Player0 uses upper nibble: d7=Right d6=Left d5=Down d4=Up
    /// Player1 uses lower nibble: d3=Right d2=Left d1=Down d0=Up
    /// Bits are 1 when not pressed, 0 when pressed.
    /// </summary>
    private byte ReadSWCHA()
    {
        byte v = 0xFF;

        if (_input.P0Right) v = (byte)(v & ~(1 << 7));
        if (_input.P0Left)  v = (byte)(v & ~(1 << 6));
        if (_input.P0Down)  v = (byte)(v & ~(1 << 5));
        if (_input.P0Up)    v = (byte)(v & ~(1 << 4));

        return v;
    }

    /// <summary>
    /// SWCHB (common convention):
    /// d0=RESET, d1=SELECT, d3=COLOR/BW, d6=P0 Difficulty, d7=P1 Difficulty.
    /// Bits are 1 when switch is not active, 0 when active/pressed.
    /// </summary>
    private byte ReadSWCHB()
    {
        byte v = 0xFF;

        if (_input.ResetPressed)  v = (byte)(v & ~(1 << 0));
        if (_input.SelectPressed) v = (byte)(v & ~(1 << 1));
        if (_input.ColorBwIsBw)   v = (byte)(v & ~(1 << 3));
        if (_input.DifficultyP0IsB) v = (byte)(v & ~(1 << 6));
        if (_input.DifficultyP1IsB) v = (byte)(v & ~(1 << 7));

        return v;
    }
}
