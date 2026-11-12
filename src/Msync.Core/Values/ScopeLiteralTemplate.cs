namespace Msync.Core;

/// <summary>Shares immutable literal storage, never a guest Scope identity.</summary>
public sealed class ScopeLiteralTemplate
{
    private readonly ScopeLayout layout;
    private readonly Value[] fields;
    private readonly int allocationSite;

    private ScopeLiteralTemplate(ScopeLayout layout, Value[] fields, int allocationSite)
        => (this.layout, this.fields, this.allocationSite) = (layout, fields, allocationSite);

    /// <summary>Builds a template only for an exact layout of immutable scalar values.</summary>
    public static ScopeLiteralTemplate? TryCreate(ScopeLayout layout, ReadOnlySpan<Value> fields, int allocationSite)
    {
        if (layout.Count != fields.Length) return null;
        foreach (Value value in fields)
            if (value is not (NumberValue or BoolValue or NullValue or StringValue)) return null;
        return new(layout, fields.ToArray(), allocationSite);
    }

    public ScopeLayout Layout => layout;

    public ScopeValue Create() => ScopeValue.FromLiteralFields(layout, fields, allocationSite);

    public bool TryGetNumber(int offset, out double number)
    {
        if (fields[offset] is NumberValue scalar)
        {
            number = scalar.Data;
            return true;
        }
        number = default;
        return false;
    }

    /// <summary>Read a ready Number from this literal without creating a Scope instance.</summary>
    public bool TryGetNumber(int offset, string name, out double number)
    {
        if (layout.IsField(offset, name) && fields[offset] is NumberValue scalar)
        {
            number = scalar.Data;
            return true;
        }
        number = default;
        return false;
    }
}
