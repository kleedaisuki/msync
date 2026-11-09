using System.Collections.Immutable;

namespace Msync.Compiler.Semantics;

// Location captures identify cells; their Value is a flow hint, not another
// component of closure identity. Value captures instead identify a snapshot.
internal sealed class SemanticCallableIdentity : IEqualityComparer<SemanticCallable>
{
    internal static SemanticCallableIdentity Instance { get; } = new();

    public bool Equals(SemanticCallable? first, SemanticCallable? second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first is null || second is null || first.Template != second.Template
            || first.NativeName != second.NativeName || first.RuntimeIdentity != second.RuntimeIdentity) return false;
        if (first.NativeName is not null) return true;
        int count = first.Captures.IsDefault ? 0 : first.Captures.Length;
        if (count != (second.Captures.IsDefault ? 0 : second.Captures.Length)) return false;
        for (int index = 0; index < count; index++)
        {
            SemanticCapture left = first.Captures[index], right = second.Captures[index];
            if (left.Mode != right.Mode) return false;
            if (left.Mode == Msync.Hir.HirCaptureMode.Location)
            {
                if (left.Source != right.Source) return false;
            }
            else if (!ValueEquals(left.Value, right.Value)) return false;
        }
        return true;
    }

    public int GetHashCode(SemanticCallable value)
    {
        HashCode hash = new();
        hash.Add(value.Template); hash.Add(value.NativeName); hash.Add(value.RuntimeIdentity);
        if (value.NativeName is not null) return hash.ToHashCode();
        foreach (SemanticCapture capture in value.Captures.IsDefault ? [] : value.Captures)
        {
            hash.Add(capture.Mode);
            if (capture.Mode == Msync.Hir.HirCaptureMode.Location) hash.Add(capture.Source);
            else
            {
                hash.Add(capture.Value.Type); hash.Add(capture.Value.Literal);
                hash.Add(capture.Value.DenotedType); hash.Add(capture.Value.EagerRange);
                hash.Add(capture.Value.Scope?.Region); hash.Add(capture.Value.Scope?.Layout);
                hash.Add(capture.Value.IndexedRegion);
            }
        }
        return hash.ToHashCode();
    }

    private bool ValueEquals(SemanticValue first, SemanticValue second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first.Type != second.Type || first.Literal != second.Literal || first.Observation != second.Observation
            || first.DenotedType != second.DenotedType || first.EagerRange != second.EagerRange
            || first.IndexedRegion != second.IndexedRegion
            || !first.IndexedAliases.AsSpan().SequenceEqual(second.IndexedAliases.AsSpan())) return false;
        if (first.Scope is { } left)
        {
            if (second.Scope is not { } right || left.Region != right.Region || left.Layout != right.Layout) return false;
            if (left.Region < 0 && !FieldsEqual(left, right)) return false;
        }
        else if (second.Scope is not null) return false;
        if (first.Element is { } element)
        {
            if (second.Element is not { } other || !ValueEquals(element, other)) return false;
        }
        else if (second.Element is not null) return false;
        int count = first.Callables.IsDefault ? 0 : first.Callables.Length;
        if (count != (second.Callables.IsDefault ? 0 : second.Callables.Length)) return false;
        for (int index = 0; index < count; index++)
            if (!Equals(first.Callables[index], second.Callables[index])) return false;
        return true;
    }

    private bool FieldsEqual(SemanticScope first, SemanticScope second)
    {
        if (ReferenceEquals(first.Fields, second.Fields)) return true;
        if (first.Fields.Count != second.Fields.Count) return false;
        foreach (var pair in first.Fields)
            if (!second.Fields.TryGetValue(pair.Key, out SemanticValue? value) || !ValueEquals(pair.Value, value)) return false;
        return true;
    }
}
