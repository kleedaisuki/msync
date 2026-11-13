using System.Text;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class NativeHirLowerer
{
    private sealed partial class Lowering
    {
        internal int Type(HirTypeId id, bool admitsNull = false)
        {
            int index;
            var type = hir.Types[id.Value];
            if ((type.RuntimeType.Kind & TypeKind.Storage) != 0)
                return HistoricalStorage(admitsNull ? HistoricalRuntime(id).WithNull : HistoricalRuntime(id));
            if ((type.RuntimeType.Kind & TypeKind.Scope) == 0 && type.Element < 0)
                return Type(admitsNull || type.Nullable ? type.RuntimeType.WithNull : type.RuntimeType);
            if (!admitsNull && mappedTypes.TryGetValue(id.Value, out index)) return index;
            string key = TypeKey(id, admitsNull);
            if (session.TypeKeys.TryGetValue(key, out index)) return index;
            index = session.Types.Count;
            session.TypeKeys.Add(key, index); session.Types.Add(new(TypeKind.Never));
            if (!admitsNull) mappedTypes.Add(id.Value, index);
            int element = type.Element >= 0 ? Type(new HirTypeId(type.Element))
                : type.RuntimeType.Element is { } nested ? Type(nested) : -1;
            int[]? layouts = null;
            if ((type.RuntimeType.Kind & TypeKind.Scope) != 0)
            {
                int[] contracts = ScopeContracts(type);
                if (contracts.Length == 0 || contracts.Any(layout => session.Layouts[layout].Fields.Count == 0))
                    session.StructuralTypes.Add(index);
                else
                {
                    session.ScopeContracts[index] = contracts;
                    layouts = session.NamespaceLayouts.Order().ToArray();
                }
            }
            var kind = type.RuntimeType.Kind | (admitsNull || type.Nullable ? TypeKind.Null : TypeKind.Never);
            session.Types[index] = new(kind, element, layouts);
            return index;
        }

        internal int Type(TypeRef type)
        {
            if ((type.Kind & TypeKind.Storage) != 0) return HistoricalStorage(type);
            if ((type.Kind & ~TypeKind.Any) != 0)
                throw new InvalidOperationException($"{type} has no native storage representation.");
            string key = "builtin:" + type;
            if (session.TypeKeys.TryGetValue(key, out int index)) return index;
            int element = type.Element is { } nested ? Type(nested) : -1;
            index = session.Types.Count;
            session.TypeKeys.Add(key, index); session.Types.Add(new(type.Kind, element)); return index;
        }

        internal int Pointer(int pointee)
        {
            string key = "pointer:" + pointee;
            if (session.TypeKeys.TryGetValue(key, out int index)) return index;
            index = session.Types.Count;
            session.TypeKeys.Add(key, index); session.Types.Add(new(TypeKind.Never, Pointee: pointee)); return index;
        }

        internal int Layout(int shape)
        {
            if (mappedLayouts.TryGetValue(shape, out int index)) return index;
            string key = LayoutKey(shape);
            if (session.LayoutKeys.TryGetValue(key, out index))
            {
                mappedLayouts.Add(shape, index); return index;
            }
            index = session.Layouts.Count;
            mappedLayouts.Add(shape, index); session.LayoutKeys.Add(key, index);
            session.Layouts.Add(new([]));
            var fields = hir.ScopeLayouts[shape].Fields;
            session.Layouts[index] = new(fields.Select(field =>
                new NativeCodeField(field.Name, Type(field.StorageType, field.AdmitsNull))).ToArray());
            return index;
        }

        private int[] ScopeContracts(HirType type) => type.Shape >= 0 ? [Layout(type.Shape)]
            : Enumerable.Range(0, type.ScopeDomain.Count).Select(index => Layout(type.ScopeDomain[index])).Distinct().ToArray();

        internal int ScopeLayout(int construction)
        {
            var scope = hir.Scopes[construction];
            if (scope.Site is { } site && session.ScopeStorage.TryGetValue(site, out int physical))
            {
                session.NamespaceLayouts.Add(physical);
                return physical;
            }
            int layout = Layout(scope.Layout);
            session.NamespaceLayouts.Add(layout);
            if (scope.Site is { } identity) session.ScopeStorage.Add(identity, layout);
            return layout;
        }

        internal int BindingLayout(int type)
        {
            string key = "binding:" + type;
            if (session.LayoutKeys.TryGetValue(key, out int index)) return index;
            index = session.Layouts.Count;
            session.LayoutKeys.Add(key, index); session.Layouts.Add(new([new("", type)])); return index;
        }

        internal int EnvironmentLayout(IEnumerable<int> captureTypes)
        {
            int[] types = captureTypes.ToArray();
            string key = "environment:" + string.Join(',', types);
            if (session.LayoutKeys.TryGetValue(key, out int index)) return index;
            index = session.Layouts.Count;
            session.LayoutKeys.Add(key, index);
            session.Layouts.Add(new(types.Select((type, ordinal) =>
                new NativeCodeField(ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), type)).ToArray()));
            return index;
        }

        internal int Field(int shape, string name, int hint)
        {
            var fields = hir.ScopeLayouts[shape].Fields;
            if ((uint)hint < (uint)fields.Length && fields[hint].Name == name) return hint;
            for (int i = 0; i < fields.Length; i++) if (fields[i].Name == name) return i;
            throw new InvalidOperationException($"Static layout #{shape} has no '{name}' field.");
        }

        internal (int Projection, int Type) Projection(HirNode node, HirNodeId receiver)
        {
            var type = hir.Types[hir.Node(receiver).Type.Value];
            List<NativeProjectionCase> cases = [];
            HashSet<int> fieldTypes = [];
            IEnumerable<int> layouts = session.NamespaceLayouts.Order();
            int[] contracts = ScopeContracts(type);
            if (contracts.Length != 0)
                layouts = layouts.Where(layout => contracts.Any(contract => LayoutAccepts(contract, layout)));
            foreach (int layout in layouts)
            {
                var fields = session.Layouts[layout].Fields;
                int field = -1;
                for (int i = 0; i < fields.Count; i++) if (fields[i].Name == node.Text) { field = i; break; }
                if (field < 0) continue;
                cases.Add(new(layout, field)); fieldTypes.Add(session.Layouts[layout].Fields[field].Type);
            }
            if (cases.Count == 0) return (-1, Type(new TypeRef(TypeKind.Never)));
            return Projection(cases, fieldTypes);
        }

        private (int Projection, int Type) Projection(IEnumerable<NativeProjectionCase> cases, IEnumerable<int> fieldTypes)
        {
            var alternatives = cases.Distinct().OrderBy(item => item.Layout).ToArray();
            string key = string.Join(';', alternatives.Select(item => item.Layout + ":" + item.Field));
            if (!projectionIndices.TryGetValue(key, out int index))
            {
                index = projections.Count; projections.Add(new(alternatives)); projectionIndices.Add(key, index);
            }
            return (index, Union(fieldTypes));
        }

        internal (int Projection, int Type) WriteProjection(HirNode node, HirNodeId receiver, int value)
        {
            var projection = Projection(node, receiver);
            if (projection.Projection < 0) return projection;
            NativeProjectionCase[] cases = projections[projection.Projection].Cases.Where(item =>
                MayStore(session.Layouts[item.Layout].Fields[item.Field].Type, value)).ToArray();
            return cases.Length == 0 ? (-1, Type(new TypeRef(TypeKind.Never)))
                : Projection(cases, cases.Select(item => session.Layouts[item.Layout].Fields[item.Field].Type));
        }

        internal IReadOnlyList<NativeProjectionCase> ProjectionCases(int projection) => projections[projection].Cases;
        internal int ProjectedType(NativeProjectionCase alternative) => session.Layouts[alternative.Layout].Fields[alternative.Field].Type;
        internal int ProjectCases(IEnumerable<NativeProjectionCase> cases) => Projection(cases,
            cases.Select(ProjectedType)).Projection;
        internal bool StorageAccepts(int destination, int source) => NativeTypeRelations.StorageAccepts(session.Types, destination, source);

        private bool MayStore(int destination, int source)
        {
            if (StorageAccepts(destination, source) || NativeTypeRelations.PathStringStore(session.Types, destination, source)) return true;
            NativeCodeType target = session.Types[destination], value = session.Types[source];
            if (target.Pointee >= 0 || value.Pointee >= 0) return false;
            const TypeKind text = TypeKind.Path | TypeKind.String;
            return (target.Kind & value.Kind) != 0 || (target.Kind & text) != 0 && (value.Kind & text) != 0;
        }

        internal int StorageAdmissionType(int type)
        {
            NativeCodeType target = session.Types[type];
            if ((target.Kind & TypeKind.Scope) == 0 || target.Layouts is null) return type;
            string key = "storage-admission:" + type;
            if (session.TypeKeys.TryGetValue(key, out int index)) return index;
            index = session.Types.Count;
            session.TypeKeys.Add(key, index); session.Types.Add(target with { Layouts = null });
            return index;
        }

        internal bool StoreConverts(int type, int source)
        {
            NativeCodeType target = session.Types[type];
            TypeKind value = session.Types[source].Kind;
            return (target.Kind & ~TypeKind.Null) == TypeKind.Path && (value & TypeKind.String) != 0
                || (target.Kind & ~TypeKind.Null) == TypeKind.String && (value & TypeKind.Path) != 0
                || (target.Kind & TypeKind.Range) != 0 && target.Element >= 0;
        }

        internal int ProjectionReceiverType(int projection, int type)
        {
            int[] layouts = projections[projection].Cases.Select(item => item.Layout).Distinct().Order().ToArray();
            var current = session.Types[type];
            if (current.Kind == TypeKind.Scope && current.Layouts is { } admitted && admitted.SequenceEqual(layouts))
                return type;
            string key = "projection-receiver:" + string.Join(',', layouts);
            if (session.TypeKeys.TryGetValue(key, out int index)) return index;
            index = session.Types.Count;
            session.TypeKeys.Add(key, index);
            session.Types.Add(new(TypeKind.Scope, Layouts: layouts));
            return index;
        }

        internal int Union(IEnumerable<int> alternatives)
        {
            int[] values = alternatives.Distinct().Order().ToArray();
            if (values.Length == 1) return values[0];
            TypeKind kind = TypeKind.Never;
            HashSet<int> layouts = [];
            bool openScope = false;
            foreach (int id in values)
            {
                var type = session.Types[id]; kind |= type.Kind;
                openScope |= (type.Kind & TypeKind.Scope) != 0 && (type.Layouts?.Count ?? 0) == 0;
                if (type.Layouts is not null) layouts.UnionWith(type.Layouts);
            }
            string key = "union:" + string.Join(',', values);
            if (session.TypeKeys.TryGetValue(key, out int index)) return index;
            index = session.Types.Count; session.TypeKeys.Add(key, index);
            session.Types.Add(new(kind, Layouts: openScope || layouts.Count == 0 ? null : layouts.Order().ToArray())); return index;
        }

        private bool LayoutAccepts(int expected, int actual)
        {
            if (expected == actual) return true;
            var fields = session.Layouts[actual].Fields;
            foreach (var contract in session.Layouts[expected].Fields)
            {
                var field = fields.FirstOrDefault(field => field.Name == contract.Name);
                if (field is null || !CarriersOverlap(contract.Type, field.Type)) return false;
            }
            return true;
        }

        private bool CarriersOverlap(int expected, int actual)
        {
            if (expected == actual) return true;
            var target = session.Types[expected];
            var source = session.Types[actual];
            if ((source.Kind & target.Kind) == 0 || target.Pointee != source.Pointee) return false;
            // A Scope field stores an address, not an invariant pointee graph.
            // The selected physical field is loaded before a dependent Narrow.
            return target.Element < 0 || source.Element < 0 || CarriersOverlap(target.Element, source.Element);
        }

        private string TypeKey(HirTypeId id, bool nullable)
        {
            StringBuilder text = new("type:");
            FingerprintType(id, text, [], []);
            if (nullable) text.Append('?');
            return text.ToString();
        }
        private string LayoutKey(int shape)
        {
            StringBuilder text = new("layout:");
            FingerprintLayout(shape, text, [], []); return text.ToString();
        }
        private void FingerprintType(HirTypeId id, StringBuilder text, Dictionary<int, int> seenTypes,
            Dictionary<int, int> seenLayouts)
        {
            if (seenTypes.TryGetValue(id.Value, out int seen)) { text.Append('t').Append(seen); return; }
            seenTypes.Add(id.Value, seenTypes.Count);
            var type = hir.Types[id.Value];
            text.Append('(').Append((int)type.RuntimeType.Kind).Append(type.Nullable ? '?' : '!');
            if (type.Element >= 0) FingerprintType(new(type.Element), text, seenTypes, seenLayouts);
            else if (type.RuntimeType.Element is { } element) text.Append(element);
            if ((type.RuntimeType.Kind & TypeKind.Scope) != 0)
            {
                if (type.ScopeDomain.Count == 0)
                {
                    text.Append("structural");
                }
                if (type.Shape >= 0)
                {
                    text.Append("schema:");
                    FingerprintLayout(type.Shape, text, seenTypes, seenLayouts);
                }
                for (int i = 0; i < type.ScopeDomain.Count; i++)
                    FingerprintLayout(type.ScopeDomain[i], text, seenTypes, seenLayouts);
            }
            text.Append(')');
        }
        private void FingerprintLayout(int shape, StringBuilder text, Dictionary<int, int> seenTypes,
            Dictionary<int, int> seenLayouts)
        {
            if (seenLayouts.TryGetValue(shape, out int seen)) { text.Append('l').Append(seen); return; }
            seenLayouts.Add(shape, seenLayouts.Count); text.Append('{');
            foreach (var field in hir.ScopeLayouts[shape].Fields)
            {
                text.Append(field.Name.Length).Append(':').Append(field.Name).Append(field.AdmitsNull ? '?' : '!');
                FingerprintType(field.StorageType, text, seenTypes, seenLayouts);
            }
            text.Append('}');
        }
    }
}
