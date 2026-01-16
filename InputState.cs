namespace Atari2600Emu;

/// <summary>
/// Simple input model for Player0 joystick + console switches.
/// All switches are "active low" on real hardware; the emu will convert to bits.
/// </summary>
public sealed class InputState
{
    // Player 0 directions + fire
    public bool P0Up;
    public bool P0Down;
    public bool P0Left;
    public bool P0Right;
    public bool P0Fire;

    // Console switches
    public bool ResetPressed;
    public bool SelectPressed;

    // Toggle switches (persist)
    public bool ColorBwIsBw;          // true = BW, false = Color
    public bool DifficultyP0IsB;      // true = B (beginner), false = A
    public bool DifficultyP1IsB;      // true = B, false = A
}
