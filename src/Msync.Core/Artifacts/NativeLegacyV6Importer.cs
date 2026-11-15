namespace Msync.Core.Native;

/// <summary>Shared emission helpers for HIR storage adapters.</summary>
public static partial class NativeLegacyV6Importer
{
    private sealed partial class Importer
    {
        internal readonly LegacyV6Module Module;
        internal readonly List<NativeCodeType> Types;
        internal readonly List<NativeCodeLayout> Layouts;
        internal readonly List<NativeConstant> Constants = [];
        internal readonly List<NativeAllocationPlan> Allocations = [];
        internal readonly List<NativeProjection> Projections = [];
        internal readonly HashSet<(int Layout, int Field)> IndirectFields = [];
        internal readonly List<NativeStatic> Statics = [];
        internal readonly Dictionary<(int Layout, int Field), (int Function, int Environment)> FieldFacts = [];
        internal bool FactsChanged;
        private readonly Dictionary<int, int> constantStatics = [];
        private readonly List<FunctionImport> functions = [];
        private readonly List<NativeFunctionCode?> adapters = [];
        private readonly Dictionary<string, int> adapterIds = new(StringComparer.Ordinal);

        internal Importer(LegacyV6Module module)
        {
            Module = module;
            Statics.AddRange(module.Statics.Select((layout, declaration) => new NativeStatic(layout, declaration)));
            Types = module.Types.Select(type => new NativeCodeType((TypeKind)(type.Kinds & ~(ushort)TypeKind.Storage),
                type.Element, type.Layouts)).ToList();
            Layouts = module.Layouts.Select(layout => new NativeCodeLayout(layout.Fields.Select(field =>
                new NativeCodeField(field.Name, field.Type)).ToArray())).ToList();
            PrepareHostShapes();
            PrepareStorage();
        }

        internal NativeProgram Run()
        {
            for (int index = 0; index < Module.Functions.Count; index++) functions.Add(new(this, index));
            do
            {
                FactsChanged = false;
                foreach (FunctionImport function in functions) function.Infer();
            } while (FactsChanged);
            ResolveIndirectFields();
            NativeFunctionCode[] imported = functions.Select(function => function.Run()).ToArray();
            NativeFunctionCode[] code = imported.Concat(adapters.Select(adapter => adapter!)).ToArray();
            return new(Constants.ToArray(), code, Types.ToArray(), Layouts.ToArray(), Module.EntryPoint)
            {
                Projections = Projections.ToArray(),
                Statics = Statics.ToArray(),
                Allocations = Allocations.ToArray(), Context = Module.Context, Stage = Module.Stage,
                Semantics = Module.Semantics, OptimizationLevel = Module.OptimizationLevel,
                ExecutionPlans = Module.Plans.Select(plan => new NativeExecutionPlan(plan.Function,
                    functions[plan.Function].Map[plan.Start], functions[plan.Function].Map[plan.End],
                    plan.Transfers.Select(transfer => new NativePlannedTransfer(transfer.Id, PlanValue(transfer.Source),
                        transfer.Destination, transfer.Kind, transfer.Reads, transfer.Writes, transfer.Predecessors,
                        transfer.Span, transfer.Mode)).ToArray(), plan.Batches, plan.EliminatedTransfers, plan.Results)).ToArray()
            };
        }

        private void ResolveIndirectFields()
        {
            for (int layout = 0; layout < Module.Layouts.Count; layout++)
                Layouts[layout] = new(Module.Layouts[layout].Fields.Select((field, ordinal) =>
                    new NativeCodeField(field.Name, IndirectFields.Contains((layout, ordinal)) ? Pointer(field.Type) : field.Type)).ToArray());
        }

        internal int ConstantStatic(int constant)
        {
            if (constantStatics.TryGetValue(constant, out int declaration)) return declaration;
            int layout = Layouts.Count;
            Layouts.Add(new([new("value", ValueType(Module.Constants[constant]))]));
            declaration = Statics.Count; Statics.Add(new(layout, declaration));
            constantStatics.Add(constant, declaration); return declaration;
        }

        internal IReadOnlyList<NativeProjectionCase> Cases(int legacy)
        {
            LegacyV6Projection projection = Module.Projections[legacy];
            if (projection.Cases.Count != 0) return projection.Cases;
            return Layouts.Select((layout, identity) => (layout, identity)).Where(item => item.identity < Module.Layouts.Count || hostLayouts.Contains(item.identity))
                .SelectMany(entry => entry.layout.Fields.Select((field, ordinal) => (field, ordinal, identity: entry.identity))
                .Where(item => item.field.Name == projection.Name).Select(item => new NativeProjectionCase(item.identity, item.ordinal))).ToArray();
        }

        internal int Type(TypeKind kind, int element = -1, IReadOnlyList<int>? layouts = null, int pointee = -1)
        {
            for (int index = 0; index < Types.Count; index++)
            {
                NativeCodeType candidate = Types[index];
                if (candidate.Kind == kind && candidate.Element == element && candidate.Pointee == pointee
                    && (candidate.Layouts ?? []).SequenceEqual(layouts ?? [])) return index;
            }
            Types.Add(new(kind, element, layouts, pointee)); return Types.Count - 1;
        }
        internal int Type(TypeRef type) => Type(type.Kind, type.Element is null ? -1 : Type(type.Element));
        internal int Pointer(int payload) => Type(TypeKind.Never, pointee: payload);
        internal int Scope(int layout) => Type(TypeKind.Scope, layouts: [layout]);
        internal int Join(int left, int right)
        {
            if (left == right) return left;
            NativeCodeType a = Types[left], b = Types[right];
            if (a.Kind == TypeKind.Never && a.Pointee < 0) return right;
            if (b.Kind == TypeKind.Never && b.Pointee < 0) return left;
            if (a.Pointee >= 0 || b.Pointee >= 0)
            {
                if (a.Pointee != b.Pointee) throw new MsyncException("Format-6 control-flow merges incompatible capture locations");
                return left;
            }
            int element = a.Element < 0 ? b.Element : b.Element < 0 ? a.Element : Join(a.Element, b.Element);
            bool openScope = (a.Kind & TypeKind.Scope) != 0 && (a.Layouts?.Count ?? 0) == 0
                || (b.Kind & TypeKind.Scope) != 0 && (b.Layouts?.Count ?? 0) == 0;
            return Type(a.Kind | b.Kind, element, openScope ? [] : (a.Layouts ?? []).Concat(b.Layouts ?? []).Distinct().Order().ToArray());
        }
        internal int Meet(int left, int right)
        {
            NativeCodeType a = Types[left], b = Types[right];
            if (a.Pointee >= 0 || b.Pointee >= 0) return left;
            TypeKind kind = a.Kind & b.Kind;
            int element = (kind & TypeKind.Range) == 0 ? -1 : a.Element < 0 ? b.Element
                : b.Element < 0 ? a.Element : Meet(a.Element, b.Element);
            int[] layouts = (kind & TypeKind.Scope) == 0 ? [] : (a.Layouts?.Count ?? 0) == 0 ? (b.Layouts ?? []).ToArray()
                : (b.Layouts?.Count ?? 0) == 0 ? a.Layouts!.ToArray() : a.Layouts!.Intersect(b.Layouts!).Order().ToArray();
            return Type(kind, element, layouts);
        }

        internal bool Accepts(int destination, int source)
        {
            Stack<(int Destination, int Source)> pending = new(); HashSet<(int, int)> seen = [];
            pending.Push((destination, source));
            while (pending.TryPop(out var pair))
            {
                if (pair.Destination == pair.Source || !seen.Add(pair)) continue;
                NativeCodeType target = Types[pair.Destination], value = Types[pair.Source];
                if (target.Pointee >= 0 || value.Pointee >= 0)
                {
                    if (target.Pointee < 0 || value.Pointee < 0) return false;
                    pending.Push((target.Pointee, value.Pointee)); pending.Push((value.Pointee, target.Pointee)); continue;
                }
                if ((target.Kind & value.Kind) != value.Kind) return false;
                if ((value.Kind & TypeKind.Scope) != 0 && target.Layouts is { Count: > 0 })
                {
                    if (value.Layouts is not { Count: > 0 } || value.Layouts.Any(layout => !target.Layouts.Contains(layout))) return false;
                }
                if (target.Element >= 0 && (value.Kind & TypeKind.Range) != 0)
                {
                    if (value.Element < 0) return false;
                    pending.Push((target.Element, value.Element));
                }
            }
            return true;
        }

        internal TypeRef ReflectionType(int type)
        {
            LegacyV6CodeType contract = Module.Types[type];
            return new((TypeKind)contract.Kinds, contract.Element < 0 ? null : ReflectionType(contract.Element));
        }

        internal int ValueType(LegacyV6Value value) => value.Kind switch
        {
            LegacyV6ValueKind.Namespace => Scope(value.Layout),
            LegacyV6ValueKind.Range => Type(TypeKind.Range, value.Type is null ? -1 : Type(value.Type.RuntimeType)),
            _ => Type(value.SourceKind)
        };
        internal static NativeConstant Scalar(LegacyV6Value value) => value.Kind switch
        {
            LegacyV6ValueKind.Null => new(NativeKind.Null),
            LegacyV6ValueKind.Bool => new(NativeKind.Bool, Boolean: value.Boolean),
            LegacyV6ValueKind.Number => new(NativeKind.Number, Number: value.Number),
            LegacyV6ValueKind.String => new(NativeKind.String, Text: value.Text),
            LegacyV6ValueKind.Path => new(NativeKind.Path, Text: value.Path!.Value.Raw) { Path = value.Path },
            LegacyV6ValueKind.Connection => new(NativeKind.Connection, Text: value.Connection!.Value.Address,
                ConnectionKind: value.Connection.Value.Kind),
            LegacyV6ValueKind.Type => new(NativeKind.Type, Type: value.Type!.RuntimeType),
            _ => throw new MsyncException($"Format-6 {value.Kind} is a construction recipe, not a scalar constant")
        };
        private static NativePlanValue PlanValue(LegacyV6Value value) => value.Kind switch
        {
            LegacyV6ValueKind.Path => NativePlanValue.FromPath(value.Path!.Value),
            LegacyV6ValueKind.Range => NativePlanValue.Snapshot((value.Items ?? []).Select(PlanValue), value.Type!.RuntimeType),
            _ => NativePlanValue.Scalar(Scalar(value))
        };
    }
}
