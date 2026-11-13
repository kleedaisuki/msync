namespace Msync.Core.Native;

public static partial class NativeLegacyV6Importer
{
    private sealed partial class Importer
    {
        private LegacyV6Value? builtinStd;
        internal LegacyV6Value BuiltinStd()
        {
            if (builtinStd is not null) return builtinStd;
            List<NativeCodeField> fields = []; List<LegacyV6Value> namespaces = [];
            foreach (string group in new[] { "fs", "path", "bool", "string", "ranges", "process" })
            {
                Msync.Core.NativeSignature[] natives = NativeSignatures.All.Where(signature =>
                    signature.Name.StartsWith(group + ".", StringComparison.Ordinal)).ToArray();
                int layout = Layouts.Count;
                Layouts.Add(new(natives.Select(native => new NativeCodeField(native.Name[(group.Length + 1)..], Type(TypeKind.Function))).ToArray()));
                hostLayouts.Add(layout); fields.Add(new(group, Scope(layout)));
                namespaces.Add(new(LegacyV6ValueKind.Namespace, Layout: layout, Items: natives.Select(native =>
                    new LegacyV6Value(LegacyV6ValueKind.Function, NativeName: native.Name)).ToArray()));
            }
            int root = Layouts.Count; Layouts.Add(new(fields.ToArray())); hostLayouts.Add(root);
            builtinStd = new(LegacyV6ValueKind.Namespace, Layout: root, Items: namespaces.ToArray()); return builtinStd;
        }
    }
}
