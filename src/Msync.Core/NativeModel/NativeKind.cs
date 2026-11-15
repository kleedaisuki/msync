namespace Msync.Core.Native;

public enum NativeKind : byte
{
    Null, Bool, Number, String, Path, Connection, Function, Range, Process, Type,
    Address,
    MachineInteger,
    MachineFloat
}
