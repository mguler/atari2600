using System;

namespace Atari2600Emu;

public interface IBus
{
    byte Read(ushort addr);
    void Write(ushort addr, byte value);
}

public sealed class Bus : IBus
{
    private readonly Cartridge _cart;

    // RIOT RAM is 128 bytes and is mirrored into both the top half of page0 ($0080-$00FF)
    // and the top half of page1 ($0180-$01FF). This is crucial because the 6502 stack
    // lives at $01xx.
    private readonly byte[] _ram = new byte[128];

    public InputState Input { get; } = new InputState();

    public Tia Tia { get; }
    public Riot6532 Riot { get; }
    public Cartridge Cart => _cart;

    public Bus(Cartridge cart)
    {
        _cart = cart ?? throw new ArgumentNullException(nameof(cart));
        Tia = new Tia(Input);
        Riot = new Riot6532(Input);
    }

    // Older timing experiment hook: keep method so CPU can call it, but we do not delay TIA writes here.
    public void SetTiaWriteDelayForCurrentInstruction(int baseCycles)
    {
        // Intentionally no-op. TIA writes are applied immediately for correct kernel timing.
    }

    public void TickCpuCycle()
    {
        // RIOT timer advances on Phi2 (~once per CPU cycle)
        Riot.TickCpuCycle();
    }

    public byte Read(ushort addr)
    {
        addr &= 0x1FFF;

        // ROM window $1000-$1FFF
        if (addr >= 0x1000)
            return _cart.ReadRom(addr);

        // RIOT RAM mirror: $0080-$00FF and $0180-$01FF
        if (addr < 0x0200 && (addr & 0x00FF) >= 0x80)
            return _ram[addr & 0x007F];

        // TIA $0000-$007F (mirrored). We only decode low 6 bits.
        if ((addr & 0x00FF) <= 0x7F)
            return Tia.Read((byte)(addr & 0x3F));

        // RIOT I/O + timer (page2)
        if (addr >= 0x0280 && addr <= 0x0297)
            return Riot.Read(addr);

        return 0x00;
    }

    public void Write(ushort addr, byte value)
    {
        addr &= 0x1FFF;

        // ROM is read-only, but allow bank-switch hotspots
        if (addr >= 0x1000)
        {
            _cart.WriteHotspot(addr);
            return;
        }

        // RIOT RAM
        if (addr < 0x0200 && (addr & 0x00FF) >= 0x80)
        {
            _ram[addr & 0x007F] = value;
            return;
        }

        // TIA registers
        if ((addr & 0x00FF) <= 0x7F)
        {
            byte reg = (byte)(addr & 0x3F);
            Tia.Write(reg, value);
            return;
        }

        // RIOT I/O + timer
        if (addr >= 0x0280 && addr <= 0x0297)
        {
            Riot.Write(addr, value);
            return;
        }
    }
}
