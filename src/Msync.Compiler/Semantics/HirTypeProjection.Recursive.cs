using System.Collections.Immutable;
using System.Text;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class HirTypeProjection
{
    private HirTypeId CloseRecursiveScope(int construction, ImmutableArray<HirField> fields, HirTypeId previous)
    {
        string identity = RecursiveIdentity(construction, fields, previous);
        return hir.RecursiveScope(identity, self => new(fields.Select(field => field with
        {
            StorageType = ReplaceRecursiveType(field.StorageType, previous, self, [])
        }).ToImmutableArray()));
    }

    private string RecursiveIdentity(int construction, ImmutableArray<HirField> fields, HirTypeId previous)
    {
        StringBuilder key = new();
        key.Append("flow-scope:").Append(construction).Append(':');
        Dictionary<HirTypeId, int> visited = [];
        foreach (HirField field in fields)
        {
            key.Append(field.Name.Length).Append(':').Append(field.Name).Append(field.AdmitsNull ? '?' : '!');
            Write(field.StorageType);
        }
        return key.ToString();

        void Write(HirTypeId id)
        {
            HirType type = hir.Types[id.Value];
            key.Append('[').Append((ushort)type.RuntimeType.Kind).Append(type.Nullable ? '?' : '!');
            if (id == previous || type.Shape >= 0 && type.Shape == hir.Types[previous.Value].Shape)
            {
                key.Append("self]");
                return;
            }
            if (visited.TryGetValue(id, out int index))
            {
                key.Append('^').Append(index).Append(']');
                return;
            }
            visited.Add(id, visited.Count);
            key.Append('p').Append(type.TypeParameter).Append('c').Append(type.Concept);
            if (type.Element >= 0) Write(new(type.Element));
            key.Append(type.Shape >= 0 ? 's' : '-');
            if (type.Shape >= 0)
                foreach (HirField field in hir.ScopeLayouts[type.Shape].Fields)
                {
                    key.Append(field.Name.Length).Append(':').Append(field.Name).Append(field.AdmitsNull ? '?' : '!');
                    Write(field.StorageType);
                }
            if (type.Signature >= 0)
            {
                HirSignature signature = hir.Signatures[type.Signature];
                foreach (HirTypeId parameter in signature.Parameters) Write(parameter);
                key.Append("->");
                Write(signature.Result);
            }
            foreach (HirTypeId alternative in type.Alternatives.AsSpan()) Write(alternative);
            key.Append(']');
        }
    }

    private HirTypeId ReplaceRecursiveType(HirTypeId source, HirTypeId previous, HirTypeId self,
        Dictionary<HirTypeId, HirTypeId> visited)
    {
        HirType type = hir.Types[source.Value];
        if (source == previous || type.Shape >= 0 && type.Shape == hir.Types[previous.Value].Shape)
            return hir.Type(hir.Types[self.Value] with { RuntimeType = type.RuntimeType, Nullable = type.Nullable });
        if (visited.TryGetValue(source, out var known)) return known;
        visited.Add(source, source);
        int element = type.Element < 0 ? -1 : ReplaceRecursiveType(new(type.Element), previous, self, visited).Value;
        int shape = type.Shape;
        if (shape >= 0)
        {
            var fields = hir.ScopeLayouts[shape].Fields;
            var rewritten = fields.Select(field => field with
            {
                StorageType = ReplaceRecursiveType(field.StorageType, previous, self, visited)
            }).ToImmutableArray();
            if (!fields.AsSpan().SequenceEqual(rewritten.AsSpan())) shape = hir.InternScopeLayout(new(rewritten));
        }
        ImmutableArray<HirTypeId> alternatives = type.Alternatives.IsDefaultOrEmpty ? type.Alternatives
            : type.Alternatives.Select(item => ReplaceRecursiveType(item, previous, self, visited)).ToImmutableArray();
        HirTypeId result = hir.Type(type with { Element = element, Shape = shape, Alternatives = alternatives });
        visited[source] = result;
        return result;
    }
}
