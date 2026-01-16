namespace Atari2600Emu;

public static class BuiltInRoms
{
    // Demo ROM: shows playfield + player0 and loops with WSYNC.
    // Needs only: LDA #imm (A9), STA zp (85), JMP abs (4C)
    public static byte[] DemoPlayfieldAndP0()
    {
        byte[] rom = new byte[4096];
        int pc = 0x000; // maps to $F000

        void Emit(params byte[] b)
        {
            for (int i = 0; i < b.Length; i++) rom[pc++] = b[i];
        }

        // BG color
        Emit(0xA9, 0x1E); // LDA #$1E
        Emit(0x85, 0x09); // STA COLUBK ($09)

        // PF color
        Emit(0xA9, 0x3E);
        Emit(0x85, 0x08); // COLUPF

        // P0 color
        Emit(0xA9, 0x4E);
        Emit(0x85, 0x06); // COLUP0

        // PF bits
        Emit(0xA9, 0xF0); Emit(0x85, 0x0D); // PF0
        Emit(0xA9, 0xAA); Emit(0x85, 0x0E); // PF1
        Emit(0xA9, 0x0F); Emit(0x85, 0x0F); // PF2

        // Reflect PF on right half
        Emit(0xA9, 0x01); Emit(0x85, 0x0A); // CTRLPF

        // P0 sprite
        Emit(0xA9, 0b11110000); Emit(0x85, 0x1B); // GRP0

        // Position player (RESP0 strobe)
        Emit(0xA9, 0x00); Emit(0x85, 0x10);

        // Loop: WSYNC every line
        int loopAddr = 0xF000 + pc;
        Emit(0xA9, 0x00);
        Emit(0x85, 0x02); // WSYNC
        Emit(0x4C, (byte)(loopAddr & 0xFF), (byte)((loopAddr >> 8) & 0xFF)); // JMP loop

        // vectors
        rom[0x0FFC] = 0x00; rom[0x0FFD] = 0xF0; // RESET -> $F000
        rom[0x0FFA] = 0x00; rom[0x0FFB] = 0xF0; // NMI
        rom[0x0FFE] = 0x00; rom[0x0FFF] = 0xF0; // IRQ/BRK

        return rom;
    }
}
