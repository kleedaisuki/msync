using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class NativeHirLowerer
{
    private sealed partial class Lowering
    {
        private void ReserveNativeLayouts()
        {
            HashSet<string> names = hir.Calls.Where(call => call.NativeName is not null)
                .Select(call => call.NativeName!).Concat(hir.Nodes.Where(node => node.Kind == HirKind.NativeFunction)
                    .Select(node => node.Text!)).ToHashSet(StringComparer.Ordinal);
            if (names.Contains("environment.variables"))
                NativeLayout("environment", NativeBuiltinLayouts.EnvironmentVariable);
            if (names.Contains("shell.process_table"))
                NativeLayout("processes", NativeBuiltinLayouts.Process);
            if (names.Contains("regex.find") || names.Contains("regex.matches"))
            {
                NativeLayout("regex-groups", NativeBuiltinLayouts.RegexGroup);
                NativeLayout("regex-match", NativeBuiltinLayouts.RegexMatch);
            }
            if (names.Contains("meta.signature_of"))
                NativeLayout("signature", NativeBuiltinLayouts.Signature);
            if (names.Contains("meta.members_of"))
                NativeLayout("members", NativeBuiltinLayouts.Member);
            foreach (HirNode node in hir.Nodes)
            {
                if (node.Kind != HirKind.Call) continue;
                string? name = hir.Calls[node.Data].NativeName;
                if (name == "ranges.zip") ReserveZipLayouts(node);
                if (name is "ranges.enumerate" or "ranges.enumerate_from")
                    foreach (TypeRef item in NativeCarriers(NativeItem(node, 1)))
                        NativeLayout("enumerate", NativeBuiltinLayouts.Enumerate(item));
            }
        }

        private void ReserveZipLayouts(HirNode node)
        {
            foreach (TypeRef first in NativeCarriers(NativeItem(node, 1)))
                foreach (TypeRef second in NativeCarriers(NativeItem(node, 2)))
                    NativeLayout("zip", NativeBuiltinLayouts.Zip(first, second));
        }

        private TypeRef NativeItem(HirNode node, int child)
        {
            HirType type = hir.Types[hir.Node(hir.Children(node)[child]).Type.Value];
            return type.Element >= 0 ? Runtime(new(type.Element)) : type.RuntimeType.Element ?? TypeRef.Any;
        }

        private IEnumerable<TypeRef> NativeCarriers(TypeRef item)
        {
            TypeKind allowed = CarrierKind(item.Kind);
            HashSet<TypeKind> kinds = [allowed];
            // A range view does not repack the selected producer's existing item ABI.
            foreach (HirType type in hir.Types)
            {
                TypeKind candidate = CarrierKind(type.RuntimeType.Kind | (type.Nullable ? TypeKind.Null : TypeKind.Never));
                if (candidate != TypeKind.Never && (candidate & allowed) == candidate) kinds.Add(candidate);
            }
            return kinds.Order().Select(kind => new TypeRef(kind));
        }

        private static TypeKind CarrierKind(TypeKind kind) => (kind & TypeKind.Storage) == 0
            ? kind : kind & ~TypeKind.Storage | TypeKind.Scope;

        private void NativeLayout(string name, NativeBuiltinRecordSchema schema)
        {
            IReadOnlyList<NativeBuiltinField> fields = schema.Fields;
            int[] types = fields.Select(field => Type(field.Type)).ToArray();
            string key = "native-record:" + name + ":" + string.Join(',', types);
            if (!session.LayoutKeys.TryGetValue(key, out int layout))
            {
                layout = session.Layouts.Count;
                session.LayoutKeys.Add(key, layout);
                session.Layouts.Add(new(fields.Select((field, index) => new NativeCodeField(field.Name, types[index])).ToArray()));
            }
            session.NamespaceLayouts.Add(layout);
        }
    }
}
