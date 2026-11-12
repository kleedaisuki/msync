using System.Collections.Immutable;
using Msync.Core;

namespace Msync.Hir;

/// <summary>The finite set of possible construction layouts, or opaque when empty.</summary>
public readonly struct HirScopeDomain : IEquatable<HirScopeDomain>
{
    private readonly int? singleton;
    private readonly ImmutableArray<int> layouts;

    private HirScopeDomain(int? singleton, ImmutableArray<int> layouts) =>
        (this.singleton, this.layouts) = (singleton, layouts);

    public int Count => singleton.HasValue ? 1 : layouts.IsDefault ? 0 : layouts.Length;
    public int this[int index] => singleton is { } only && index == 0 ? only : layouts.AsSpan()[index];

    public static HirScopeDomain Exact(int layout) => new(layout, default);

    internal static HirScopeDomain FromEncoded(ImmutableArray<int> layouts) => layouts.Length switch
    {
        0 => default,
        1 => Exact(layouts[0]),
        _ => new(null, layouts)
    };

    public static HirScopeDomain Join(HirType left, HirType right)
    {
        if ((left.RuntimeType.Kind & TypeKind.Scope) == 0) return right.ScopeDomain;
        if ((right.RuntimeType.Kind & TypeKind.Scope) == 0) return left.ScopeDomain;
        return left.ScopeDomain.Union(right.ScopeDomain);
    }

    private HirScopeDomain Union(HirScopeDomain other)
    {
        int left = Count, right = other.Count;
        if (left == 0 || right == 0) return default;
        if (Equals(other)) return this;
        var result = ImmutableArray.CreateBuilder<int>(left + right);
        int i = 0, j = 0;
        while (i < left || j < right)
        {
            if (j == right || i < left && this[i] < other[j]) result.Add(this[i++]);
            else if (i == left || other[j] < this[i]) result.Add(other[j++]);
            else { result.Add(this[i++]); j++; }
        }
        if (result.Count == left) return this;
        if (result.Count == right) return other;
        return new(null, result.ToImmutable());
    }

    public bool Equals(HirScopeDomain other) => singleton == other.singleton
        && layouts.AsSpan().SequenceEqual(other.layouts.AsSpan());

    public override bool Equals(object? obj) => obj is HirScopeDomain other && Equals(other);

    public override int GetHashCode()
    {
        if (layouts.IsDefault) return singleton.GetHashCode();
        HashCode hash = new();
        foreach (int layout in layouts) hash.Add(layout);
        return hash.ToHashCode();
    }
}
