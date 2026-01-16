using System;

namespace Atari2600Emu;

public enum BankSwitchScheme
{
    None,
    F8_8K,   // 2x4K banks, hotspots $1FF8/$1FF9
    F6_16K,  // 4x4K banks, hotspots $1FF6-$1FF9
}

public sealed class Cartridge
{
    public byte[] Rom { get; }
    public BankSwitchScheme Scheme { get; }
    public int CurrentBank { get; private set; } // 0..N-1
    public int BankCount { get; }

    public Cartridge(byte[] rom)
    {
        if (rom is null || rom.Length == 0)
            throw new ArgumentException("ROM is empty.");
        Rom = rom;

        if (rom.Length == 2048)
        {
            Scheme = BankSwitchScheme.None;
            BankCount = 1;
        }
        else if (rom.Length == 4096)
        {
            Scheme = BankSwitchScheme.None;
            BankCount = 1;
        }
        else if (rom.Length == 8192)
        {
            Scheme = BankSwitchScheme.F8_8K;
            BankCount = 2;
            CurrentBank = 1; // many F8 carts power up bank 1 visible at $F000
        }
        else if (rom.Length == 16384)
        {
            Scheme = BankSwitchScheme.F6_16K;
            BankCount = 4;
            CurrentBank = 0;
        }
        else
        {
            // unknown size: default to first 4K
            Scheme = BankSwitchScheme.None;
            BankCount = 1;
        }
    }

    /// <summary>
    /// Any access (read or write) to hotspot can switch banks.
    /// addr is 13-bit ($0000-$1FFF). ROM window is $1000-$1FFF.
    /// </summary>
    public void ObserveAccess(ushort addr)
    {
        addr &= 0x1FFF;
        if (addr < 0x1000) return;

        int off = addr & 0x0FFF;

        if (Scheme == BankSwitchScheme.F8_8K)
        {
            if (off == 0x0FF8) CurrentBank = 0;
            else if (off == 0x0FF9) CurrentBank = 1;
        }
        else if (Scheme == BankSwitchScheme.F6_16K)
        {
            if (off == 0x0FF6) CurrentBank = 0;
            else if (off == 0x0FF7) CurrentBank = 1;
            else if (off == 0x0FF8) CurrentBank = 2;
            else if (off == 0x0FF9) CurrentBank = 3;
        }
    }

    public byte ReadRom(ushort addr)
    {
        addr &= 0x1FFF;
        ObserveAccess(addr);

        int off4k = addr & 0x0FFF;

        if (Rom.Length == 2048) return Rom[off4k & 0x07FF];
        if (Rom.Length == 4096) return Rom[off4k];

        if (Scheme == BankSwitchScheme.F8_8K)
        {
            int bankBase = CurrentBank * 4096;
            return Rom[bankBase + off4k];
        }

        if (Scheme == BankSwitchScheme.F6_16K)
        {
            int bankBase = CurrentBank * 4096;
            return Rom[bankBase + off4k];
        }

        return Rom[off4k % Rom.Length];
    }

    public void WriteHotspot(ushort addr)
    {
        ObserveAccess(addr);
    }
}
