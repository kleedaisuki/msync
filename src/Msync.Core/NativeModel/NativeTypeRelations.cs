namespace Msync.Core.Native;

public static class NativeTypeRelations
{
    public static bool Same(IReadOnlyList<NativeCodeType> types, int left, int right) =>
        Accepts(types, left, right) && Accepts(types, right, left);

    public static bool Accepts(IReadOnlyList<NativeCodeType> types, int destination, int source)
    {
        Stack<(int Destination, int Source)> pending = new();
        HashSet<(int, int)> seen = [];
        pending.Push((destination, source));
        while (pending.TryPop(out var pair))
        {
            if (pair.Destination == pair.Source || !seen.Add(pair)) continue;
            NativeCodeType target = types[pair.Destination], value = types[pair.Source];
            if (target.Pointee >= 0 || value.Pointee >= 0)
            {
                if (target.Pointee < 0 || value.Pointee < 0) return false;
                pending.Push((target.Pointee, value.Pointee));
                pending.Push((value.Pointee, target.Pointee));
                continue;
            }
            if ((target.Kind & value.Kind) != value.Kind) return false;
            if ((value.Kind & TypeKind.Scope) != 0 && target.Layouts is not null)
            {
                if (value.Layouts is null) return false;
                foreach (int layout in value.Layouts) if (!target.Layouts.Contains(layout)) return false;
            }
            if (target.Element >= 0 && (value.Kind & TypeKind.Range) != 0)
            {
                if (value.Element < 0) return false;
                pending.Push((target.Element, value.Element));
            }
        }
        return true;
    }

    public static bool StorageAccepts(IReadOnlyList<NativeCodeType> types, int destination, int source)
    {
        NativeCodeType target = types[destination], value = types[source];
        if (target.Pointee >= 0 || value.Pointee >= 0) return Same(types, destination, source);
        if ((target.Kind & value.Kind) != value.Kind) return false;
        // Referent layout domains are checked at dependent access, not stored in the address carrier.
        return target.Element < 0 || (value.Kind & TypeKind.Range) == 0
            || value.Element >= 0 && Accepts(types, target.Element, value.Element);
    }

    public static bool PathStringStore(IReadOnlyList<NativeCodeType> types, int destination, int source)
    {
        NativeCodeType target = types[destination], value = types[source];
        const TypeKind kinds = TypeKind.Path | TypeKind.String | TypeKind.Null;
        if (target.Pointee >= 0 || value.Pointee >= 0 || (target.Kind & ~kinds) != 0 || (value.Kind & ~kinds) != 0)
            return false;
        return ((value.Kind & TypeKind.Null) == 0 || (target.Kind & TypeKind.Null) != 0)
            && (target.Kind & (TypeKind.Path | TypeKind.String)) != 0;
    }
}
