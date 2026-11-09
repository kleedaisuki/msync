namespace Msync.Core;

public sealed record TypeValue(TypeRef Data) : Value
{
    public override TypeRef Type => TypeRef.Type;
    public override string ToString() => Data.ToString();
}
