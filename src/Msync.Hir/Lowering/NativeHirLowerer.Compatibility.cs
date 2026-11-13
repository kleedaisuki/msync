using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class NativeHirLowerer
{
    private sealed partial class Lowering
    {
        private HistoricalBindings? historical = null;
        private readonly Dictionary<int, int[]> historicalScopeContracts = [];

        private sealed record HistoricalBindings(int FunctionOffset, NativeFunctionCode[] Functions,
            IReadOnlyDictionary<string, int> Names, IReadOnlyDictionary<TypeKind, int> StorageTypes,
            int LogicalTypeFunction, int TypeTestFunction);

        private void PrepareHistoricalBindings()
        {
            string[] names = hir.Nodes.Where(node => node.Kind == HirKind.NativeFunction).Select(node => node.Text)
                .Concat(hir.Calls.Select(call => call.NativeName)).Where(HistoricalName)
                .Select(name => name!).Distinct(StringComparer.Ordinal).ToArray();
            HirTypeId[] owners = Enumerable.Range(0, hir.Types.Length)
                .Where(index => (hir.Types[index].RuntimeType.Kind & TypeKind.Storage) != 0)
                .Select(index => new HirTypeId(index)).ToArray();
            if (names.Length == 0 && owners.Length == 0) return;
            NativeHistoricalBindingBundle bundle = NativeHistoricalBindings.Lower(names,
                owners.Select(id => HistoricalRuntime(id).Element ?? TypeRef.Any));
            int typeOffset = session.Types.Count;
            int functionOffset = Locations.NextCallable;
            NativeFunctionCode[] functions = LinkCompatibility(bundle.Program, functionOffset);
            historical = new(functionOffset, functions, bundle.Functions,
                bundle.StorageTypes.ToDictionary(pair => pair.Key, pair => checked(typeOffset + pair.Value)),
                checked(functionOffset + bundle.LogicalTypeFunction), checked(functionOffset + bundle.TypeTestFunction));
        }

        private static bool HistoricalName(string? name) => name is not null
            && (name.StartsWith("storage.", StringComparison.Ordinal)
                || name.StartsWith("arena.", StringComparison.Ordinal)
                || name.StartsWith("slots.", StringComparison.Ordinal));

        private TypeRef HistoricalRuntime(HirTypeId id)
        {
            HirType type = hir.Types[id.Value];
            TypeRef result = type.RuntimeType;
            if ((result.Kind & (TypeKind.Range | TypeKind.Storage)) != 0 && type.Element >= 0)
                result = result with { Element = HistoricalRuntime(new(type.Element)) };
            return type.Nullable ? result.WithNull : result;
        }

        internal bool HistoricalFunction(string? name, out int identity)
        {
            if (name is not null && historical is { } bindings && bindings.Names.TryGetValue(name, out int function))
            { identity = checked(bindings.FunctionOffset + function); return true; }
            identity = -1;
            return false;
        }

        internal int HistoricalResult(int function) => historical!.Functions[function - historical.FunctionOffset].Signature.Result;

        internal int HistoricalTypeTest => historical?.TypeTestFunction ?? -1;
        internal bool HasHistoricalBindings => historical is not null;

        private void FinalizeHistoricalTypes()
        {
            foreach (var (type, headers) in historicalScopeContracts)
                session.Types[type] = session.Types[type] with
                { Layouts = session.NamespaceLayouts.Concat(headers).Distinct().Order().ToArray() };
        }

        internal int HistoricalRangeAbi(int type)
        {
            NativeCodeType value = session.Types[type];
            return value.Kind == TypeKind.Range && value.Element < 0
                ? Type(new TypeRef(TypeKind.Range, TypeRef.Any)) : type;
        }

        internal NativeFunctionReflectionSignature? HistoricalSignature(HirFunction function)
        {
            if (historical is null) return null;
            TypeRef[] parameters = function.ParameterEntryTypes.IsDefaultOrEmpty
                ? function.Parameters.Select(parameter =>
                {
                    HirLocation location = hir.Locations[parameter.Value];
                    TypeRef type = HistoricalRuntime(location.StorageType);
                    return location.AdmitsNull ? type.WithNull : type;
                }).ToArray()
                : function.ParameterEntryTypes.Select(HistoricalRuntime).ToArray();
            return new(parameters, HistoricalRuntime(function.Result));
        }

        internal int HistoricalAdmission(int source, int target)
        {
            if (historical is null) return source;
            NativeCodeType actual = session.Types[source], expected = session.Types[target];
            return actual.Pointee < 0 && expected.Pointee < 0
                && (actual.Kind & expected.Kind & TypeKind.Scope) != 0
                && (expected.Kind & ~actual.Kind) == 0 ? Union([source, target]) : source;
        }

        internal TypeRef? HistoricalElement(HirTypeId type)
        {
            if (historical is null) return null;
            TypeRef logical = HistoricalRuntime(type);
            return (logical.Kind & ~TypeKind.Null) == TypeKind.Range
                && ContainsHistorical(logical.Element) ? logical.Element : null;
        }

        private static bool ContainsHistorical(TypeRef? type) => type is not null
            && ((type.Kind & TypeKind.Storage) != 0 || ContainsHistorical(type.Element));

        private int HistoricalStorage(TypeRef type)
        {
            if (historical is null)
                throw new InvalidOperationException("Storage is historical type metadata, not a native storage representation.");
            TypeKind element = type.Element?.Kind ?? TypeKind.Any;
            if (!historical.StorageTypes.TryGetValue(element, out int storage))
                throw new InvalidOperationException($"Historical HIR requires an unprepared element contract {type.Element}.");
            NativeCodeType carrier = session.Types[storage];
            TypeKind kind = (type.Kind & ~TypeKind.Storage) | TypeKind.Scope;
            int nested = (kind & TypeKind.Range) != 0 && type.Element is not null ? Type(type.Element) : -1;
            string key = "historical:" + type;
            if (session.TypeKeys.TryGetValue(key, out int existing)) return existing;
            int result = session.Types.Count;
            session.Types.Add(new(TypeKind.Never));
            session.TypeKeys.Add(key, result);
            IReadOnlyList<int>? layouts = carrier.Layouts;
            if ((type.Kind & TypeKind.Scope) != 0)
            {
                int[] headers = (carrier.Layouts ?? []).ToArray();
                historicalScopeContracts.Add(result, headers);
                layouts = session.NamespaceLayouts.Concat(headers).Distinct().Order().ToArray();
            }
            session.Types[result] = new(kind, nested, layouts);
            return result;
        }

        private NativeFunctionCode[] LinkCompatibility(NativeProgram fragment, int functionOffset)
        {
            if (fragment.Statics.Count != 0 || fragment.ExecutionPlans is { Count: > 0 }
                || fragment.SessionRoots is { Count: > 0 } || fragment.SessionDispatches is { Count: > 0 })
                throw new InvalidOperationException("Historical binding fragments cannot own application state.");
            int typeOffset = session.Types.Count, layoutOffset = session.Layouts.Count;
            int projectionOffset = projections.Count, constantOffset = constants.Count;
            int TypeIndex(int value) => value < 0 ? value : checked(typeOffset + value);
            int LayoutIndex(int value) => value < 0 ? value : checked(layoutOffset + value);
            int FunctionIndex(int value) => value < 0 ? value : checked(functionOffset + value);
            session.Types.AddRange(fragment.Types.Select(type => type with
            {
                Element = TypeIndex(type.Element),
                Pointee = TypeIndex(type.Pointee),
                Layouts = type.Layouts?.Select(LayoutIndex).ToArray()
            }));
            session.Layouts.AddRange(fragment.Layouts.Select(layout => new NativeCodeLayout(
                layout.Fields.Select(field => field with { Type = TypeIndex(field.Type) }).ToArray())));
            projections.AddRange(fragment.Projections.Select(projection => new NativeProjection(
                projection.Cases.Select(item => item with { Layout = LayoutIndex(item.Layout) }).ToArray())));
            foreach (NativeConstant constant in fragment.Constants) constants.Add(constant);
            foreach (NativeAllocationPlan allocation in fragment.Allocations)
                allocations.Add(allocation with
                { Layout = LayoutIndex(allocation.Layout), Function = FunctionIndex(allocation.Function) });
            return fragment.Functions.Select(function => function with
            {
                Slots = function.Slots.Select(slot => slot with { Type = TypeIndex(slot.Type) }).ToArray(),
                Signature = function.Signature with
                {
                    Parameters = function.Signature.Parameters.Select(TypeIndex).ToArray(),
                    Result = TypeIndex(function.Signature.Result),
                    Captures = function.Signature.Captures.Select(capture => capture with
                    { Type = TypeIndex(capture.Type) }).ToArray()
                },
                Instructions = function.Instructions.Select(Relocate).ToArray()
            }).ToArray();

            NativeInstruction Relocate(NativeInstruction instruction)
            {
                instruction = instruction with { Type = TypeIndex(instruction.Type) };
                return instruction.Op switch
                {
                    NativeOp.Constant => instruction with { A = checked(constantOffset + instruction.A) },
                    NativeOp.Allocate => instruction with { A = LayoutIndex(instruction.A) },
                    NativeOp.AddressOffset or NativeOp.EnvironmentAddress => instruction with { B = LayoutIndex(instruction.B) },
                    NativeOp.IndexAddress or NativeOp.RangeFromMemory => instruction with { C = LayoutIndex(instruction.C) },
                    NativeOp.CopyMemory or NativeOp.ClearMemory => instruction with { Immediate = LayoutIndex(checked((int)instruction.Immediate)) },
                    NativeOp.ProjectAddress => instruction with { B = checked(projectionOffset + instruction.B) },
                    NativeOp.Unary when instruction.Immediate == (long)NativeUnary.HasLayout => instruction with { B = LayoutIndex(instruction.B) },
                    NativeOp.MakeFunction => instruction with { A = FunctionIndex(instruction.A), B = LayoutIndex(instruction.B) },
                    NativeOp.Call or NativeOp.AdaptFunction => instruction with
                    { B = FunctionIndex(instruction.B), Symbol = RelocateDispatch(instruction.Symbol) },
                    NativeOp.TestStatic or NativeOp.StaticAddress or NativeOp.PublishStatic or NativeOp.ExecutePlan or NativeOp.PlanResult =>
                        throw new InvalidOperationException("Historical binding fragments cannot reference application state."),
                    _ => instruction
                };
            }

            string? RelocateDispatch(string? dispatch) => dispatch is null ? null : FunctionDispatch.Encode(
                FunctionDispatch.Targets(dispatch).Select(pair =>
                    KeyValuePair.Create(FunctionIndex(pair.Key), FunctionIndex(pair.Value))));
        }
    }
}
