namespace Msync.Core;

public sealed record CellValue(Value Data) : Value
{
    public Value Data { get; set; } = Data;
    internal CodeStorageWitness? CodeStorage { get; init; }
    public override TypeRef Type => Data.Type;
    public override string ToString() => Data.ToString() ?? "null";
}

// Portable type IDs are image-local. A location retains the fixed structural
// contract, not the old image or the type of its current payload.
internal sealed class CodeStorageWitness(TypeKind kind, CodeStorageWitness? element, IReadOnlyList<int>? layouts)
{
    private readonly TypeKind kind = kind;
    private readonly CodeStorageWitness? element = element;
    private readonly IReadOnlyList<int> layouts = layouts ?? [];

    internal bool Same(CodeStorageWitness? other)
    {
        for (CodeStorageWitness? left = this; !ReferenceEquals(left, other); left = left.element, other = other.element)
        {
            if (left is null || other is null || left.kind != other.kind || left.layouts.Count != other.layouts.Count)
                return false;
            for (int i = 0; i < left.layouts.Count; i++)
                if (left.layouts[i] != other.layouts[i]) return false;
        }
        return true;
    }

    public override string ToString()
    {
        string domain = layouts.Count == 0 ? "" : $" layouts [{string.Join(", ", layouts)}]";
        return element is null ? kind + domain : $"{kind}<{element}>{domain}";
    }
}
