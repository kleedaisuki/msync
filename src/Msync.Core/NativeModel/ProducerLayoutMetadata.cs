namespace Msync.Core.Native;

public enum NativeUnary : byte { Not, Negate, IsNull, IsType, HasLayout, ValueType }

public enum NativeBinary : byte
{
    Add, Subtract, Multiply, Divide, Remainder, Power,
    Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
    RegexMatch, PathJoin, Qualify,
    BoolAnd, BoolOr
}

// Namespace domains describe admitted referents, not the address carrier's storage.
// Direct backend offsets need physical producer/admission proof, not this domain alone.
public sealed record NativeCodeType(TypeKind Kind, int Element = -1,
    IReadOnlyList<int>? Layouts = null, int Pointee = -1);

public sealed record NativeCodeField(string Name, int Type);
public sealed record NativeCodeLayout(IReadOnlyList<NativeCodeField> Fields);
public readonly record struct NativeProjectionCase(int Layout, int Field);
public sealed record NativeProjection(IReadOnlyList<NativeProjectionCase> Cases);

public sealed record NativeConstant(NativeKind Kind, string? Text = null,
    TypeRef? Type = null, ConnectionKind ConnectionKind = ConnectionKind.Local)
{
    public NativePath? Path { get; init; }
    public int OriginId { get; init; } = -1;
    public int OriginOffset { get; init; }
}
