namespace Msync.Core;

[Flags]
public enum InstructionEffect
{
    None = 0,
    Read = 1,
    Write = 2,
    Invoke = 4,
    MayFail = 8,
    Control = 16,
    Capture = 32,
    Diverge = 64
}
