namespace Msync.Core.Native;

public static partial class NativeLegacyV6Importer
{
    private sealed partial class Importer
    {
        private readonly HashSet<int> hostLayouts = [];
        private readonly Dictionary<string, int> hostResults = new(StringComparer.Ordinal);

        private void PrepareHostShapes()
        {
            int number = Type(TypeKind.Number), text = Type(TypeKind.String), boolean = Type(TypeKind.Bool), type = Type(TypeKind.Type);
            int Record(params (string Name, int Type)[] fields)
            {
                int layout = Layouts.Count;
                Layouts.Add(new(fields.Select(field => new NativeCodeField(field.Name, field.Type)).ToArray()));
                hostLayouts.Add(layout); return Scope(layout);
            }
            int group = Record(("name", text), ("value", text), ("index", number), ("length", number), ("success", boolean));
            int match = Record(("value", text), ("index", number), ("length", number), ("groups", Type(TypeKind.Range, group)));
            hostResults.Add("regex.find", Join(match, Type(TypeKind.Null)));
            hostResults.Add("regex.matches", Type(TypeKind.Range, match));
            int signature = Record(("parameters", Type(TypeKind.Range, type)), ("arity", number), ("result", type),
                ("result_declared", boolean), ("variadic", boolean));
            hostResults.Add("meta.signature_of", Join(signature, Type(TypeKind.Null)));
            int member = Record(("name", text), ("type", type), ("storage_type", type), ("admits_null", boolean));
            hostResults.Add("meta.members_of", Type(TypeKind.Range, member));
            hostResults.Add("environment.variables", Type(TypeKind.Range, Record(("name", text), ("value", text))));
            hostResults.Add("shell.process_table", Type(TypeKind.Range, Record(("pid", number), ("name", text))));
        }

        internal int PayloadType(int layout, int field) => layout < Module.Layouts.Count
            ? Module.Layouts[layout].Fields[field].Type : Layouts[layout].Fields[field].Type;
    }
}
