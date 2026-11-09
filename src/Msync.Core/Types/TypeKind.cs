namespace Msync.Core;

[Flags]
public enum TypeKind : ushort
{
    Never = 0, Null = 1, Bool = 2, String = 4, Path = 8, Range = 16,
    Function = 32, Scope = 64, Process = 128, Connection = 256, Type = 512, Number = 1024,
    Any = Null | Bool | String | Path | Range | Function | Scope | Process | Connection | Type | Number
}
