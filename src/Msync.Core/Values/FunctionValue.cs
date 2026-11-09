namespace Msync.Core;

public sealed record FunctionValue(int Function, IReadOnlyList<Value>? Captures = null, string? NativeName = null) : Value
{
    public override TypeRef Type => TypeRef.Function;
    public override string ToString() => NativeName is null ? $"fn #{Function}" : $"native fn {NativeName}";
}
