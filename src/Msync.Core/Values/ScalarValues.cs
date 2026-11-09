using System.Globalization;

namespace Msync.Core;

public sealed record NullValue : Value
{
    public override TypeRef Type => TypeRef.Null;
    public override string ToString() => "null";
}
public sealed record BoolValue(bool Data) : Value
{
    public override TypeRef Type => TypeRef.Bool;
    public override string ToString() => Data ? "true" : "false";
}
public sealed record StringValue(string Data) : Value
{
    public override TypeRef Type => TypeRef.String;
    public override string ToString() => Data;
}
public sealed record NumberValue(double Data) : Value
{
    public override TypeRef Type => TypeRef.Number;
    public override string ToString() => Data.ToString(CultureInfo.InvariantCulture);
}
