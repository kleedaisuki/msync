namespace Msync.Core;

public sealed record CellValue(Value Data) : Value
{
    public Value Data { get; set; } = Data;
    public override TypeRef Type => Data.Type;
    public override string ToString() => Data.ToString() ?? "null";
}
