namespace Atari2600Emu;

public sealed class Atari2600
{
    public Bus Bus { get; }
    public Cpu6502 Cpu { get; }

    public Atari2600(Cartridge cart)
    {
        Bus = new Bus(cart);
        Cpu = new Cpu6502(Bus);
        Cpu.Reset();
    }

    // Very rough timing: run approx CPU cycles for one NTSC frame.
    public void RunFrameNtsc()
    {
        const int cpuCyclesPerFrame = 19876;

        for (int i = 0; i < cpuCyclesPerFrame; i++)
        {
            // CPU cycle (unless WSYNC holds it)
            if (!Bus.Tia.CpuHaltedByWsync)
                Cpu.Clock();

            // Per-CPU-cycle hardware tick (RIOT timer + delayed TIA writes)
            Bus.TickCpuCycle();

            // TIA runs at ~3 color clocks per CPU cycle
            Bus.Tia.TickColorClock();
            Bus.Tia.TickColorClock();
            Bus.Tia.TickColorClock();
        }
    }
}
