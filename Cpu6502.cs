using System;

namespace Atari2600Emu;

[Flags]
public enum StatusFlags : byte
{
    C = 1 << 0, // Carry
    Z = 1 << 1, // Zero
    I = 1 << 2, // IRQ Disable
    D = 1 << 3, // Decimal
    B = 1 << 4, // Break
    U = 1 << 5, // Unused (always 1 when pushed)
    V = 1 << 6, // Overflow
    N = 1 << 7  // Negative
}

/// <summary>
/// 6502/6507 CPU core (legal opcodes complete).
/// Called one CPU cycle at a time via Clock().
/// </summary>
public sealed class Cpu6502
{
    private readonly IBus _bus;

    // Registers
    public byte A { get; private set; }
    public byte X { get; private set; }
    public byte Y { get; private set; }
    public byte SP { get; private set; } = 0xFD;
    public ushort PC { get; private set; }
    public StatusFlags P { get; private set; } = StatusFlags.U | StatusFlags.I;

    // Cycle counter
    public int CyclesRemaining { get; private set; }

    // Debug
    public ushort LastOpPC { get; private set; }
    public byte LastOpcode { get; private set; }
    public uint UnknownOpcodeCount { get; private set; }

    public ushort ResetVector { get; private set; }
    public bool ResetVectorWasPatched { get; private set; }

    public bool EnableDecimalMode { get; set; } = true;

    private readonly Op[] _ops = new Op[256];
    private readonly bool[] _defined = new bool[256];

    private struct Op
    {
        public string Name;
        public Func<int> Exec;   // returns extra cycles
        public byte Cycles;      // base cycles
    }

    public Cpu6502(IBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        BuildOpcodeTable();
    }

    public void Reset()
    {
        A = X = Y = 0;
        SP = 0xFD;
        P = StatusFlags.U | StatusFlags.I;

        ushort lo = _bus.Read(0xFFFC);
        ushort hi = _bus.Read(0xFFFD);
        ResetVector = (ushort)(lo | (hi << 8));
        PC = ResetVector;

        // Some ROM dumps (or non-2600 binaries) may have zeroed vectors.
        // If reset vector is 0000, the CPU will BRK-loop in TIA space.
        // As a pragmatic compatibility hack, start at $F000 (mirrors to $1000 on 6507).
        if (PC == 0x0000)
        {
            PC = 0xF000;
            ResetVectorWasPatched = true;
        }
        else
        {
            ResetVectorWasPatched = false;
        }

        CyclesRemaining = 7;
    }

    /// <summary>
    /// Advance by one CPU cycle.
    /// </summary>
    public void Clock()
    {
        if (CyclesRemaining == 0)
        {
            LastOpPC = PC;
            byte opcode = FetchByte();
            LastOpcode = opcode;

            if (!_defined[opcode] && opcode != 0xEA)
                UnknownOpcodeCount++;

            var op = _ops[opcode];

            // Inform Bus about base instruction cycles so it can delay TIA writes.
            if (_bus is Bus b)
                b.SetTiaWriteDelayForCurrentInstruction(op.Cycles);

            int extra = op.Exec?.Invoke() ?? 0;
            CyclesRemaining = op.Cycles + extra;

            // U flag is always 1 logically
            SetFlag(StatusFlags.U, true);
        }

        CyclesRemaining--;
    }

    // -------- Flags --------

    private bool GetFlag(StatusFlags f) => (P & f) != 0;

    private void SetFlag(StatusFlags f, bool v)
    {
        if (v) P |= f;
        else P &= ~f;
    }

    private void SetZN(byte value)
    {
        SetFlag(StatusFlags.Z, value == 0);
        SetFlag(StatusFlags.N, (value & 0x80) != 0);
    }

    // -------- Bus helpers --------

    private byte Read(ushort addr) => _bus.Read(addr);
    private void Write(ushort addr, byte value) => _bus.Write(addr, value);

    private byte FetchByte()
    {
        byte v = Read(PC);
        PC++;
        return v;
    }

    private ushort FetchWord()
    {
        byte lo = FetchByte();
        byte hi = FetchByte();
        return (ushort)(lo | (hi << 8));
    }

    private ushort ReadWordBug(ushort addr)
    {
        byte lo = Read(addr);
        ushort addr2 = (ushort)((addr & 0xFF00) | (byte)(addr + 1));
        byte hi = Read(addr2);
        return (ushort)(lo | (hi << 8));
    }

    // -------- Stack --------

    private void Push(byte v)
    {
        Write((ushort)(0x0100 | SP), v);
        SP--;
    }

    private byte Pop()
    {
        SP++;
        return Read((ushort)(0x0100 | SP));
    }

    private void PushWord(ushort v)
    {
        Push((byte)(v >> 8));
        Push((byte)(v & 0xFF));
    }

    private ushort PopWord()
    {
        byte lo = Pop();
        byte hi = Pop();
        return (ushort)(lo | (hi << 8));
    }

    // -------- Addressing modes (addr, pageCrossExtraCycle) --------

    private (ushort addr, bool pageCross) AddrZP()
        => ((ushort)FetchByte(), false);

    private (ushort addr, bool pageCross) AddrZPX()
        => ((ushort)((FetchByte() + X) & 0xFF), false);

    private (ushort addr, bool pageCross) AddrZPY()
        => ((ushort)((FetchByte() + Y) & 0xFF), false);

    private (ushort addr, bool pageCross) AddrABS()
        => (FetchWord(), false);

    private (ushort addr, bool addCycle) AddrABSX(bool addCycleOnPageCross)
    {
        ushort baseAddr = FetchWord();
        ushort addr = (ushort)(baseAddr + X);
        bool cross = (baseAddr & 0xFF00) != (addr & 0xFF00);
        return (addr, addCycleOnPageCross && cross);
    }

    private (ushort addr, bool addCycle) AddrABSY(bool addCycleOnPageCross)
    {
        ushort baseAddr = FetchWord();
        ushort addr = (ushort)(baseAddr + Y);
        bool cross = (baseAddr & 0xFF00) != (addr & 0xFF00);
        return (addr, addCycleOnPageCross && cross);
    }

    private (ushort addr, bool pageCross) AddrIND()
    {
        ushort ptr = FetchWord();
        ushort addr = ReadWordBug(ptr);
        return (addr, false);
    }

    private (ushort addr, bool pageCross) AddrINDX()
    {
        byte zp = FetchByte();
        byte ptr = (byte)(zp + X);
        byte lo = Read(ptr);
        byte hi = Read((byte)(ptr + 1));
        return ((ushort)(lo | (hi << 8)), false);
    }

    private (ushort addr, bool addCycle) AddrINDY(bool addCycleOnPageCross)
    {
        byte zp = FetchByte();
        byte lo = Read(zp);
        byte hi = Read((byte)(zp + 1));
        ushort baseAddr = (ushort)(lo | (hi << 8));
        ushort addr = (ushort)(baseAddr + Y);
        bool cross = (baseAddr & 0xFF00) != (addr & 0xFF00);
        return (addr, addCycleOnPageCross && cross);
    }

    // -------- Interrupt helpers (BRK/IRQ/NMI) --------

    private void DoInterrupt(ushort vector, bool setBreakFlagOnStack)
    {
        PushWord(PC);

        StatusFlags pToPush = P | StatusFlags.U;
        if (setBreakFlagOnStack) pToPush |= StatusFlags.B;
        else pToPush &= ~StatusFlags.B;

        Push((byte)pToPush);

        SetFlag(StatusFlags.I, true);

        ushort lo = Read(vector);
        ushort hi = Read((ushort)(vector + 1));
        PC = (ushort)(lo | (hi << 8));
    }

    // -------- ALU ops --------

    private void ADC(byte value)
    {
        int carryIn = GetFlag(StatusFlags.C) ? 1 : 0;

        if (EnableDecimalMode && GetFlag(StatusFlags.D))
        {
            int lo = (A & 0x0F) + (value & 0x0F) + carryIn;
            int hi = (A >> 4) + (value >> 4);

            if (lo > 9) { lo -= 10; hi += 1; }
            if (hi > 9)
            {
                hi -= 10;
                SetFlag(StatusFlags.C, true);
            }
            else SetFlag(StatusFlags.C, false);

            byte result = (byte)((hi << 4) | (lo & 0x0F));

            int sum = A + value + carryIn;
            byte bin = (byte)sum;
            SetFlag(StatusFlags.V, (~(A ^ value) & (A ^ bin) & 0x80) != 0);

            A = result;
            SetZN(A);
            return;
        }

        int sum2 = A + value + carryIn;
        byte res2 = (byte)sum2;

        SetFlag(StatusFlags.C, sum2 > 0xFF);
        SetFlag(StatusFlags.V, (~(A ^ value) & (A ^ res2) & 0x80) != 0);

        A = res2;
        SetZN(A);
    }

    private void SBC(byte value) => ADC((byte)(value ^ 0xFF));

    private byte ASL(byte v)
    {
        SetFlag(StatusFlags.C, (v & 0x80) != 0);
        v = (byte)(v << 1);
        SetZN(v);
        return v;
    }

    private byte LSR(byte v)
    {
        SetFlag(StatusFlags.C, (v & 0x01) != 0);
        v = (byte)(v >> 1);
        SetZN(v);
        return v;
    }

    private byte ROL(byte v)
    {
        bool oldC = GetFlag(StatusFlags.C);
        SetFlag(StatusFlags.C, (v & 0x80) != 0);
        v = (byte)((v << 1) | (oldC ? 1 : 0));
        SetZN(v);
        return v;
    }

    private byte ROR(byte v)
    {
        bool oldC = GetFlag(StatusFlags.C);
        SetFlag(StatusFlags.C, (v & 0x01) != 0);
        v = (byte)((v >> 1) | (oldC ? 0x80 : 0));
        SetZN(v);
        return v;
    }

    private void CMP(byte reg, byte value)
    {
        int diff = reg - value;
        SetFlag(StatusFlags.C, reg >= value);
        SetZN((byte)diff);
    }

    private void BIT(byte value)
    {
        SetFlag(StatusFlags.Z, (A & value) == 0);
        SetFlag(StatusFlags.N, (value & 0x80) != 0);
        SetFlag(StatusFlags.V, (value & 0x40) != 0);
    }

    private int BranchIf(bool condition)
    {
        sbyte offset = unchecked((sbyte)FetchByte());
        if (!condition) return 0;

        ushort oldPC = PC;
        PC = (ushort)(PC + offset);

        int extra = 1;
        if ((oldPC & 0xFF00) != (PC & 0xFF00)) extra++;
        return extra;
    }

    // -------- Opcode table --------

    private void BuildOpcodeTable()
    {
        for (int i = 0; i < 256; i++)
            _ops[i] = new Op { Name = "NOP", Cycles = 2, Exec = () => 0 };

        void Set(byte opcode, string name, byte cycles, Func<int> exec)
        {
            _ops[opcode] = new Op { Name = name, Cycles = cycles, Exec = exec };
            _defined[opcode] = true;
        }

        // NOP
        Set(0xEA, "NOP", 2, () => 0);

        // -------- Loads --------
        Set(0xA9, "LDA #", 2, () => { A = FetchByte(); SetZN(A); return 0; });
        Set(0xA5, "LDA zp", 3, () => { var (a, _) = AddrZP(); A = Read(a); SetZN(A); return 0; });
        Set(0xB5, "LDA zp,X", 4, () => { var (a, _) = AddrZPX(); A = Read(a); SetZN(A); return 0; });
        Set(0xAD, "LDA abs", 4, () => { var (a, _) = AddrABS(); A = Read(a); SetZN(A); return 0; });
        Set(0xBD, "LDA abs,X", 4, () => { var (a, add) = AddrABSX(true); A = Read(a); SetZN(A); return add ? 1 : 0; });
        Set(0xB9, "LDA abs,Y", 4, () => { var (a, add) = AddrABSY(true); A = Read(a); SetZN(A); return add ? 1 : 0; });
        Set(0xA1, "LDA (zp,X)", 6, () => { var (a, _) = AddrINDX(); A = Read(a); SetZN(A); return 0; });
        Set(0xB1, "LDA (zp),Y", 5, () => { var (a, add) = AddrINDY(true); A = Read(a); SetZN(A); return add ? 1 : 0; });

        Set(0xA2, "LDX #", 2, () => { X = FetchByte(); SetZN(X); return 0; });
        Set(0xA6, "LDX zp", 3, () => { var (a, _) = AddrZP(); X = Read(a); SetZN(X); return 0; });
        Set(0xB6, "LDX zp,Y", 4, () => { var (a, _) = AddrZPY(); X = Read(a); SetZN(X); return 0; });
        Set(0xAE, "LDX abs", 4, () => { var (a, _) = AddrABS(); X = Read(a); SetZN(X); return 0; });
        Set(0xBE, "LDX abs,Y", 4, () => { var (a, add) = AddrABSY(true); X = Read(a); SetZN(X); return add ? 1 : 0; });

        Set(0xA0, "LDY #", 2, () => { Y = FetchByte(); SetZN(Y); return 0; });
        Set(0xA4, "LDY zp", 3, () => { var (a, _) = AddrZP(); Y = Read(a); SetZN(Y); return 0; });
        Set(0xB4, "LDY zp,X", 4, () => { var (a, _) = AddrZPX(); Y = Read(a); SetZN(Y); return 0; });
        Set(0xAC, "LDY abs", 4, () => { var (a, _) = AddrABS(); Y = Read(a); SetZN(Y); return 0; });
        Set(0xBC, "LDY abs,X", 4, () => { var (a, add) = AddrABSX(true); Y = Read(a); SetZN(Y); return add ? 1 : 0; });

        // -------- Stores --------
        Set(0x85, "STA zp", 3, () => { var (a, _) = AddrZP(); Write(a, A); return 0; });
        Set(0x95, "STA zp,X", 4, () => { var (a, _) = AddrZPX(); Write(a, A); return 0; });
        Set(0x8D, "STA abs", 4, () => { var (a, _) = AddrABS(); Write(a, A); return 0; });
        Set(0x9D, "STA abs,X", 5, () => { var (a, _) = AddrABSX(false); Write(a, A); return 0; });
        Set(0x99, "STA abs,Y", 5, () => { var (a, _) = AddrABSY(false); Write(a, A); return 0; });
        Set(0x81, "STA (zp,X)", 6, () => { var (a, _) = AddrINDX(); Write(a, A); return 0; });
        Set(0x91, "STA (zp),Y", 6, () => { var (a, _) = AddrINDY(false); Write(a, A); return 0; });

        Set(0x86, "STX zp", 3, () => { var (a, _) = AddrZP(); Write(a, X); return 0; });
        Set(0x96, "STX zp,Y", 4, () => { var (a, _) = AddrZPY(); Write(a, X); return 0; });
        Set(0x8E, "STX abs", 4, () => { var (a, _) = AddrABS(); Write(a, X); return 0; });

        Set(0x84, "STY zp", 3, () => { var (a, _) = AddrZP(); Write(a, Y); return 0; });
        Set(0x94, "STY zp,X", 4, () => { var (a, _) = AddrZPX(); Write(a, Y); return 0; });
        Set(0x8C, "STY abs", 4, () => { var (a, _) = AddrABS(); Write(a, Y); return 0; });

        // -------- Transfers --------
        Set(0xAA, "TAX", 2, () => { X = A; SetZN(X); return 0; });
        Set(0x8A, "TXA", 2, () => { A = X; SetZN(A); return 0; });
        Set(0xA8, "TAY", 2, () => { Y = A; SetZN(Y); return 0; });
        Set(0x98, "TYA", 2, () => { A = Y; SetZN(A); return 0; });
        Set(0xBA, "TSX", 2, () => { X = SP; SetZN(X); return 0; });
        Set(0x9A, "TXS", 2, () => { SP = X; return 0; });

        // -------- Stack --------
        Set(0x48, "PHA", 3, () => { Push(A); return 0; });
        Set(0x68, "PLA", 4, () => { A = Pop(); SetZN(A); return 0; });
        Set(0x08, "PHP", 3, () => { Push((byte)(P | StatusFlags.B | StatusFlags.U)); return 0; });
        Set(0x28, "PLP", 4, () =>
        {
            P = (StatusFlags)Pop();
            SetFlag(StatusFlags.U, true);
            SetFlag(StatusFlags.B, false);
            return 0;
        });

        // -------- Jumps / calls --------
        Set(0x4C, "JMP abs", 3, () => { var (a, _) = AddrABS(); PC = a; return 0; });
        Set(0x6C, "JMP (ind)", 5, () => { var (a, _) = AddrIND(); PC = a; return 0; });

        Set(0x20, "JSR", 6, () =>
        {
            ushort target = FetchWord();
            PushWord((ushort)(PC - 1));
            PC = target;
            return 0;
        });

        Set(0x60, "RTS", 6, () =>
        {
            PC = (ushort)(PopWord() + 1);
            return 0;
        });

        Set(0x40, "RTI", 6, () =>
        {
            P = (StatusFlags)Pop();
            SetFlag(StatusFlags.U, true);
            SetFlag(StatusFlags.B, false);
            PC = PopWord();
            return 0;
        });

        // -------- BRK --------
        Set(0x00, "BRK", 7, () =>
        {
            PC++;
            DoInterrupt(0xFFFE, setBreakFlagOnStack: true);
            return 0;
        });

        // -------- Branches --------
        Set(0x10, "BPL", 2, () => BranchIf(!GetFlag(StatusFlags.N)));
        Set(0x30, "BMI", 2, () => BranchIf(GetFlag(StatusFlags.N)));
        Set(0x50, "BVC", 2, () => BranchIf(!GetFlag(StatusFlags.V)));
        Set(0x70, "BVS", 2, () => BranchIf(GetFlag(StatusFlags.V)));
        Set(0x90, "BCC", 2, () => BranchIf(!GetFlag(StatusFlags.C)));
        Set(0xB0, "BCS", 2, () => BranchIf(GetFlag(StatusFlags.C)));
        Set(0xD0, "BNE", 2, () => BranchIf(!GetFlag(StatusFlags.Z)));
        Set(0xF0, "BEQ", 2, () => BranchIf(GetFlag(StatusFlags.Z)));

        // -------- Flag ops --------
        Set(0x18, "CLC", 2, () => { SetFlag(StatusFlags.C, false); return 0; });
        Set(0x38, "SEC", 2, () => { SetFlag(StatusFlags.C, true); return 0; });
        Set(0x58, "CLI", 2, () => { SetFlag(StatusFlags.I, false); return 0; });
        Set(0x78, "SEI", 2, () => { SetFlag(StatusFlags.I, true); return 0; });
        Set(0xB8, "CLV", 2, () => { SetFlag(StatusFlags.V, false); return 0; });
        Set(0xD8, "CLD", 2, () => { SetFlag(StatusFlags.D, false); return 0; });
        Set(0xF8, "SED", 2, () => { SetFlag(StatusFlags.D, true); return 0; });

        // -------- Inc/Dec regs --------
        Set(0xE8, "INX", 2, () => { X++; SetZN(X); return 0; });
        Set(0xCA, "DEX", 2, () => { X--; SetZN(X); return 0; });
        Set(0xC8, "INY", 2, () => { Y++; SetZN(Y); return 0; });
        Set(0x88, "DEY", 2, () => { Y--; SetZN(Y); return 0; });

        // -------- INC/DEC memory --------
        Set(0xE6, "INC zp", 5, () => { var (a, _) = AddrZP(); byte v = (byte)(Read(a) + 1); Write(a, v); SetZN(v); return 0; });
        Set(0xF6, "INC zp,X", 6, () => { var (a, _) = AddrZPX(); byte v = (byte)(Read(a) + 1); Write(a, v); SetZN(v); return 0; });
        Set(0xEE, "INC abs", 6, () => { var (a, _) = AddrABS(); byte v = (byte)(Read(a) + 1); Write(a, v); SetZN(v); return 0; });
        Set(0xFE, "INC abs,X", 7, () => { var (a, _) = AddrABSX(false); byte v = (byte)(Read(a) + 1); Write(a, v); SetZN(v); return 0; });

        Set(0xC6, "DEC zp", 5, () => { var (a, _) = AddrZP(); byte v = (byte)(Read(a) - 1); Write(a, v); SetZN(v); return 0; });
        Set(0xD6, "DEC zp,X", 6, () => { var (a, _) = AddrZPX(); byte v = (byte)(Read(a) - 1); Write(a, v); SetZN(v); return 0; });
        Set(0xCE, "DEC abs", 6, () => { var (a, _) = AddrABS(); byte v = (byte)(Read(a) - 1); Write(a, v); SetZN(v); return 0; });
        Set(0xDE, "DEC abs,X", 7, () => { var (a, _) = AddrABSX(false); byte v = (byte)(Read(a) - 1); Write(a, v); SetZN(v); return 0; });

        // -------- AND / ORA / EOR --------
        Set(0x29, "AND #", 2, () => { A = (byte)(A & FetchByte()); SetZN(A); return 0; });
        Set(0x25, "AND zp", 3, () => { var (a, _) = AddrZP(); A = (byte)(A & Read(a)); SetZN(A); return 0; });
        Set(0x35, "AND zp,X", 4, () => { var (a, _) = AddrZPX(); A = (byte)(A & Read(a)); SetZN(A); return 0; });
        Set(0x2D, "AND abs", 4, () => { var (a, _) = AddrABS(); A = (byte)(A & Read(a)); SetZN(A); return 0; });
        Set(0x3D, "AND abs,X", 4, () => { var (a, add) = AddrABSX(true); A = (byte)(A & Read(a)); SetZN(A); return add ? 1 : 0; });
        Set(0x39, "AND abs,Y", 4, () => { var (a, add) = AddrABSY(true); A = (byte)(A & Read(a)); SetZN(A); return add ? 1 : 0; });
        Set(0x21, "AND (zp,X)", 6, () => { var (a, _) = AddrINDX(); A = (byte)(A & Read(a)); SetZN(A); return 0; });
        Set(0x31, "AND (zp),Y", 5, () => { var (a, add) = AddrINDY(true); A = (byte)(A & Read(a)); SetZN(A); return add ? 1 : 0; });

        Set(0x09, "ORA #", 2, () => { A = (byte)(A | FetchByte()); SetZN(A); return 0; });
        Set(0x05, "ORA zp", 3, () => { var (a, _) = AddrZP(); A = (byte)(A | Read(a)); SetZN(A); return 0; });
        Set(0x15, "ORA zp,X", 4, () => { var (a, _) = AddrZPX(); A = (byte)(A | Read(a)); SetZN(A); return 0; });
        Set(0x0D, "ORA abs", 4, () => { var (a, _) = AddrABS(); A = (byte)(A | Read(a)); SetZN(A); return 0; });
        Set(0x1D, "ORA abs,X", 4, () => { var (a, add) = AddrABSX(true); A = (byte)(A | Read(a)); SetZN(A); return add ? 1 : 0; });
        Set(0x19, "ORA abs,Y", 4, () => { var (a, add) = AddrABSY(true); A = (byte)(A | Read(a)); SetZN(A); return add ? 1 : 0; });
        Set(0x01, "ORA (zp,X)", 6, () => { var (a, _) = AddrINDX(); A = (byte)(A | Read(a)); SetZN(A); return 0; });
        Set(0x11, "ORA (zp),Y", 5, () => { var (a, add) = AddrINDY(true); A = (byte)(A | Read(a)); SetZN(A); return add ? 1 : 0; });

        Set(0x49, "EOR #", 2, () => { A = (byte)(A ^ FetchByte()); SetZN(A); return 0; });
        Set(0x45, "EOR zp", 3, () => { var (a, _) = AddrZP(); A = (byte)(A ^ Read(a)); SetZN(A); return 0; });
        Set(0x55, "EOR zp,X", 4, () => { var (a, _) = AddrZPX(); A = (byte)(A ^ Read(a)); SetZN(A); return 0; });
        Set(0x4D, "EOR abs", 4, () => { var (a, _) = AddrABS(); A = (byte)(A ^ Read(a)); SetZN(A); return 0; });
        Set(0x5D, "EOR abs,X", 4, () => { var (a, add) = AddrABSX(true); A = (byte)(A ^ Read(a)); SetZN(A); return add ? 1 : 0; });
        Set(0x59, "EOR abs,Y", 4, () => { var (a, add) = AddrABSY(true); A = (byte)(A ^ Read(a)); SetZN(A); return add ? 1 : 0; });
        Set(0x41, "EOR (zp,X)", 6, () => { var (a, _) = AddrINDX(); A = (byte)(A ^ Read(a)); SetZN(A); return 0; });
        Set(0x51, "EOR (zp),Y", 5, () => { var (a, add) = AddrINDY(true); A = (byte)(A ^ Read(a)); SetZN(A); return add ? 1 : 0; });

        // -------- ADC / SBC --------
        Set(0x69, "ADC #", 2, () => { ADC(FetchByte()); return 0; });
        Set(0x65, "ADC zp", 3, () => { var (a, _) = AddrZP(); ADC(Read(a)); return 0; });
        Set(0x75, "ADC zp,X", 4, () => { var (a, _) = AddrZPX(); ADC(Read(a)); return 0; });
        Set(0x6D, "ADC abs", 4, () => { var (a, _) = AddrABS(); ADC(Read(a)); return 0; });
        Set(0x7D, "ADC abs,X", 4, () => { var (a, add) = AddrABSX(true); ADC(Read(a)); return add ? 1 : 0; });
        Set(0x79, "ADC abs,Y", 4, () => { var (a, add) = AddrABSY(true); ADC(Read(a)); return add ? 1 : 0; });
        Set(0x61, "ADC (zp,X)", 6, () => { var (a, _) = AddrINDX(); ADC(Read(a)); return 0; });
        Set(0x71, "ADC (zp),Y", 5, () => { var (a, add) = AddrINDY(true); ADC(Read(a)); return add ? 1 : 0; });

        Set(0xE9, "SBC #", 2, () => { SBC(FetchByte()); return 0; });
        Set(0xE5, "SBC zp", 3, () => { var (a, _) = AddrZP(); SBC(Read(a)); return 0; });
        Set(0xF5, "SBC zp,X", 4, () => { var (a, _) = AddrZPX(); SBC(Read(a)); return 0; });
        Set(0xED, "SBC abs", 4, () => { var (a, _) = AddrABS(); SBC(Read(a)); return 0; });
        Set(0xFD, "SBC abs,X", 4, () => { var (a, add) = AddrABSX(true); SBC(Read(a)); return add ? 1 : 0; });
        Set(0xF9, "SBC abs,Y", 4, () => { var (a, add) = AddrABSY(true); SBC(Read(a)); return add ? 1 : 0; });
        Set(0xE1, "SBC (zp,X)", 6, () => { var (a, _) = AddrINDX(); SBC(Read(a)); return 0; });
        Set(0xF1, "SBC (zp),Y", 5, () => { var (a, add) = AddrINDY(true); SBC(Read(a)); return add ? 1 : 0; });

        // -------- Shifts / rotates --------
        Set(0x0A, "ASL A", 2, () => { A = ASL(A); return 0; });
        Set(0x06, "ASL zp", 5, () => { var (a, _) = AddrZP(); byte v = ASL(Read(a)); Write(a, v); return 0; });
        Set(0x16, "ASL zp,X", 6, () => { var (a, _) = AddrZPX(); byte v = ASL(Read(a)); Write(a, v); return 0; });
        Set(0x0E, "ASL abs", 6, () => { var (a, _) = AddrABS(); byte v = ASL(Read(a)); Write(a, v); return 0; });
        Set(0x1E, "ASL abs,X", 7, () => { var (a, _) = AddrABSX(false); byte v = ASL(Read(a)); Write(a, v); return 0; });

        Set(0x4A, "LSR A", 2, () => { A = LSR(A); return 0; });
        Set(0x46, "LSR zp", 5, () => { var (a, _) = AddrZP(); byte v = LSR(Read(a)); Write(a, v); return 0; });
        Set(0x56, "LSR zp,X", 6, () => { var (a, _) = AddrZPX(); byte v = LSR(Read(a)); Write(a, v); return 0; });
        Set(0x4E, "LSR abs", 6, () => { var (a, _) = AddrABS(); byte v = LSR(Read(a)); Write(a, v); return 0; });
        Set(0x5E, "LSR abs,X", 7, () => { var (a, _) = AddrABSX(false); byte v = LSR(Read(a)); Write(a, v); return 0; });

        Set(0x2A, "ROL A", 2, () => { A = ROL(A); return 0; });
        Set(0x26, "ROL zp", 5, () => { var (a, _) = AddrZP(); byte v = ROL(Read(a)); Write(a, v); return 0; });
        Set(0x36, "ROL zp,X", 6, () => { var (a, _) = AddrZPX(); byte v = ROL(Read(a)); Write(a, v); return 0; });
        Set(0x2E, "ROL abs", 6, () => { var (a, _) = AddrABS(); byte v = ROL(Read(a)); Write(a, v); return 0; });
        Set(0x3E, "ROL abs,X", 7, () => { var (a, _) = AddrABSX(false); byte v = ROL(Read(a)); Write(a, v); return 0; });

        Set(0x6A, "ROR A", 2, () => { A = ROR(A); return 0; });
        Set(0x66, "ROR zp", 5, () => { var (a, _) = AddrZP(); byte v = ROR(Read(a)); Write(a, v); return 0; });
        Set(0x76, "ROR zp,X", 6, () => { var (a, _) = AddrZPX(); byte v = ROR(Read(a)); Write(a, v); return 0; });
        Set(0x6E, "ROR abs", 6, () => { var (a, _) = AddrABS(); byte v = ROR(Read(a)); Write(a, v); return 0; });
        Set(0x7E, "ROR abs,X", 7, () => { var (a, _) = AddrABSX(false); byte v = ROR(Read(a)); Write(a, v); return 0; });

        // -------- BIT --------
        Set(0x24, "BIT zp", 3, () => { var (a, _) = AddrZP(); BIT(Read(a)); return 0; });
        Set(0x2C, "BIT abs", 4, () => { var (a, _) = AddrABS(); BIT(Read(a)); return 0; });

        // -------- Compare --------
        Set(0xC9, "CMP #", 2, () => { CMP(A, FetchByte()); return 0; });
        Set(0xC5, "CMP zp", 3, () => { var (a, _) = AddrZP(); CMP(A, Read(a)); return 0; });
        Set(0xD5, "CMP zp,X", 4, () => { var (a, _) = AddrZPX(); CMP(A, Read(a)); return 0; });
        Set(0xCD, "CMP abs", 4, () => { var (a, _) = AddrABS(); CMP(A, Read(a)); return 0; });
        Set(0xDD, "CMP abs,X", 4, () => { var (a, add) = AddrABSX(true); CMP(A, Read(a)); return add ? 1 : 0; });
        Set(0xD9, "CMP abs,Y", 4, () => { var (a, add) = AddrABSY(true); CMP(A, Read(a)); return add ? 1 : 0; });
        Set(0xC1, "CMP (zp,X)", 6, () => { var (a, _) = AddrINDX(); CMP(A, Read(a)); return 0; });
        Set(0xD1, "CMP (zp),Y", 5, () => { var (a, add) = AddrINDY(true); CMP(A, Read(a)); return add ? 1 : 0; });

        Set(0xE0, "CPX #", 2, () => { CMP(X, FetchByte()); return 0; });
        Set(0xE4, "CPX zp", 3, () => { var (a, _) = AddrZP(); CMP(X, Read(a)); return 0; });
        Set(0xEC, "CPX abs", 4, () => { var (a, _) = AddrABS(); CMP(X, Read(a)); return 0; });

        Set(0xC0, "CPY #", 2, () => { CMP(Y, FetchByte()); return 0; });
        Set(0xC4, "CPY zp", 3, () => { var (a, _) = AddrZP(); CMP(Y, Read(a)); return 0; });
        Set(0xCC, "CPY abs", 4, () => { var (a, _) = AddrABS(); CMP(Y, Read(a)); return 0; });

        // Note: Remaining official opcodes (like LDA/STA/ADC/etc.) are covered above;
        // unknown/illegal opcodes remain as NOP in this core.
    }
}
