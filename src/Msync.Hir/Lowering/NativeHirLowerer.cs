using System.Collections.Immutable;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

public sealed record NativeHirLoweringOptions(int OptimizationLevel = 2, bool PreserveLocals = false);
public sealed record NativeHirLoweringResult(NativeProgram Program, HirLoweringLayout Layout);

public sealed class NativeHirLoweringSession
{
    internal HirLoweringMap Locations { get; } = new();
    internal List<NativeCodeType> Types { get; } = [];
    internal Dictionary<string, int> TypeKeys { get; } = new(StringComparer.Ordinal);
    internal List<NativeCodeLayout> Layouts { get; } = [];
    internal HashSet<int> NamespaceLayouts { get; } = [];
    internal HashSet<int> StructuralTypes { get; } = [];
    internal Dictionary<int, int[]> ScopeContracts { get; } = [];
    internal Dictionary<string, int> LayoutKeys { get; } = new(StringComparer.Ordinal);
    internal List<NativeStatic> Statics { get; } = [];
    internal Dictionary<int, int> StaticDeclarations { get; } = [];
    internal Dictionary<HirLocationId, int> StorageTypes { get; } = [];
    internal Dictionary<HirAllocationSiteId, int> ScopeStorage { get; } = [];
    internal Dictionary<(HirTemplateId Template, int Capture), HirLocationId> RootCaptures { get; } = [];

    public NativeHirLoweringResult Lower(HirModule module, NativeHirLoweringOptions? options = null) =>
        NativeHirLowerer.Lower(module, options, this);

    public NativeHirLoweringResult Lower(HirModule module, NativeHirLoweringOptions? options, CompilationTrace? trace) =>
        NativeHirLowerer.Lower(module, options, this, trace);
}

public static partial class NativeHirLowerer
{
    public static NativeHirLoweringResult Lower(HirModule module, NativeHirLoweringOptions? options = null,
        NativeHirLoweringSession? session = null)
        => Lower(module, options, session, null);

    public static NativeHirLoweringResult Lower(HirModule module, NativeHirLoweringOptions? options,
        NativeHirLoweringSession? session, CompilationTrace? trace)
    {
        if (!module.SessionDispatches.IsDefaultOrEmpty && session is null)
            throw new InvalidOperationException("Open callable signatures require an active native HIR session.");
        var state = session ?? new NativeHirLoweringSession();
        var missing = module.UnresolvedImports.Where(location =>
            !state.Locations.Locations.ContainsKey(new(module.Locations[location.Value].Owner, location))).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException("Typed HIR requires session locations: " +
                string.Join(", ", missing.Select(id => module.Locations[id.Value].Name).Distinct(StringComparer.Ordinal)));
        return new Lowering(module, options ?? new(), state, trace?.Detail).Run();
    }

    private sealed partial class Lowering(HirModule hir, NativeHirLoweringOptions options,
        NativeHirLoweringSession session, CompilationTrace? trace)
    {
        private readonly List<NativeConstant> constants = [];
        private readonly Dictionary<NativeConstant, int> constantIndices = new(new ConstantComparer());
        private readonly List<NativeProjection> projections = [];
        private readonly Dictionary<string, int> projectionIndices = new(StringComparer.Ordinal);
        private readonly List<NativeAllocationPlan> allocations = [];
        private readonly Dictionary<int, int> mappedTypes = [];
        private readonly Dictionary<int, int> mappedLayouts = [];
        private readonly Dictionary<HirLocationId, int> fieldStorage = [];
        private readonly HashSet<HirLocationId> captured = Captured(hir);
        private HashSet<HirLocationId> fieldBindings = [];
        private readonly Dictionary<HirFunctionId, HirLocationId[]> functionLocations = hir.Locations
            .Select((location, index) => (location, id: new HirLocationId(index)))
            .Where(item => item.location.TemplateOwner is null)
            .GroupBy(item => item.location.Owner)
            .ToDictionary(group => group.Key, group => group.Select(item => item.id).ToArray());
        private readonly HashSet<HirLocationId> roots = hir.IsSession
            ? hir.Exports.Select(export => export.Location).ToHashSet() : [];
        private HirLoweringMap Locations => session.Locations;
        internal HirModule Hir => hir;
        internal NativeHirLoweringOptions Options => options;

        internal NativeHirLoweringResult Run()
        {
            Stage("compiler.lower.field_bindings", static self => self.PrepareFieldBindings());
            Stage("compiler.lower.reserve", static self =>
            {
                self.SeedPathOrigins();
                self.ReserveCallables();
                self.PrepareHistoricalBindings();
            });
            Stage("compiler.lower.layouts", static self =>
            {
                self.ReserveNativeLayouts();
                for (int i = 0; i < self.Hir.Scopes.Length; i++) self.ScopeLayout(i);
                for (int i = 0; i < self.Hir.ScopeLayouts.Length; i++) self.Layout(i);
                self.FinalizeHistoricalTypes();
                self.BindFieldContracts();
                self.FinalizeScopeTypes();
                self.RecordLayouts();
            });
            NativeFunctionCode[] functions = new NativeFunctionCode[Locations.NextCallable + (historical?.Functions.Length ?? 0)];
            if (historical is { } bindings) bindings.Functions.CopyTo(functions, bindings.FunctionOffset);
            for (int index = 0; index < hir.Functions.Length; index++)
            {
                HirFunctionId id = new(index);
                var state = (self: this, id, function: hir.Functions[index]);
                functions[Function(id)] = trace is null ? new FunctionLowering(this, id, state.function).Run()
                    : trace.MeasureDetailed("compiler.lower.function", state,
                        static item => new FunctionLowering(item.self, item.id, item.function).Run(),
                        size: CompilationSize.Of,
                        attributes: new Dictionary<string, string>(HirTelemetry.Source(hir.Sources, hir.Nodes[state.function.Body.Value].Source))
                        { ["function.name"] = state.function.Name, ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            for (int index = 0; index < hir.Templates.Length; index++)
                functions[Locations.Templates[new(index)]] = Template(hir.Templates[index]);
            if (functions.Any(function => function is null))
                throw new InvalidOperationException("A persistent HIR module must retain its prior callable definitions.");
            Stage("compiler.lower.finalize_types", static self => self.FinalizeScopeTypes());
            NativeProgram program = new(constants.ToArray(), functions, session.Types.ToArray(),
                session.Layouts.ToArray(), Function(hir.Entry))
            {
                Projections = projections.ToArray(),
                Statics = session.Statics.ToArray(),
                Allocations = allocations.ToArray(),
                OptimizationLevel = options.OptimizationLevel,
                PreserveLocals = hir.IsSession || options.PreserveLocals,
                SessionRoots = hir.IsSession ? hir.Exports.Select(export =>
                    Locations.Locations[new(hir.Entry, export.Location)]).Distinct().ToArray() : null,
                SessionDispatches = hir.SessionDispatches.IsDefaultOrEmpty ? null :
                    hir.SessionDispatches.Select(dispatch => new SessionDispatchTable(dispatch.Slot,
                        dispatch.Targets.ToDictionary(target => Callable(target.Identity), target => Function(target.Target)))).ToArray()
            };
            return new(program, Locations.Layout);
        }

        private void PrepareFieldBindings()
        {
            fieldBindings = HirFieldBindings.Find(hir);
            trace?.Metric("fields.frame_bindings", fieldBindings.Count);
            trace?.Metric("fields.lexical_addresses", hir.Scopes.Sum(scope =>
                scope.Fields.Count(field => !fieldBindings.Contains(field))));
        }

        private void Stage(string name, Action<Lowering> action)
        {
            if (trace is null) action(this);
            else trace.Run(name, this, action);
        }

        private void RecordLayouts()
        {
            trace?.Metric("native.types", session.Types.Count);
            trace?.Metric("native.layouts", session.Layouts.Count);
        }

        private void FinalizeScopeTypes()
        {
            foreach (int type in session.StructuralTypes)
                session.Types[type] = session.Types[type] with { Layouts = null };
            FinalizeHistoricalTypes();
            foreach (var (type, contracts) in session.ScopeContracts)
                session.Types[type] = session.Types[type] with
                {
                    Layouts = session.NamespaceLayouts.Where(layout => contracts.Any(contract => LayoutAccepts(contract, layout)))
                    .Order().ToArray()
                };
        }

        private void SeedPathOrigins()
        {
            if (!hir.IsSession) return;
            foreach (HirNode node in hir.Nodes)
            {
                if (node.Kind != HirKind.Literal || hir.Literals[node.Data] is not { Kind: HirLiteralKind.Path } literal)
                    continue;
                Constant(new(NativeKind.Path, Text: literal.Text)
                {
                    OriginId = node.Source.Source.Value,
                    OriginOffset = node.Source.Start
                });
            }
        }

        private void ReserveCallables()
        {
            for (int i = 0; i < hir.Functions.Length; i++)
            {
                HirFunctionId id = new(i);
                if (Locations.Functions.ContainsKey(id)) continue;
                int slot = Locations.NextCallable++;
                Locations.Functions.Add(id, slot); Locations.Callables.Add(slot, HirCallableId.From(id));
            }
            for (int i = 0; i < hir.Templates.Length; i++)
            {
                HirTemplateId id = new(i);
                if (Locations.Templates.ContainsKey(id)) continue;
                int slot = Locations.NextCallable++;
                Locations.Templates.Add(id, slot); Locations.Callables.Add(slot, HirCallableId.From(id));
            }
        }

        private NativeFunctionCode Template(HirTemplate template)
        {
            var captures = template.Captures.IsDefault ? [] : template.Captures;
            var slots = captures.Select(capture => new NativeSlot(LocationType(capture.Target), NativeSlotRole.Capture,
                Name: hir.Locations[capture.Target.Value].Name)).Concat(template.Parameters.Select(parameter =>
                new NativeSlot(LocationType(parameter), NativeSlotRole.Parameter, Name: hir.Locations[parameter.Value].Name))).ToList();
            int value = slots.Count;
            int type = Type(TypeRef.Null);
            slots.Add(new(type));
            return new(template.Name, slots.ToArray(),
                [new(NativeOp.Constant, value, Constant(new(NativeKind.Null)), Type: type), new(NativeOp.Return, A: value)],
                new(template.Parameters.Select(LocationType).ToArray(), type, CaptureContracts(captures)),
                template.Parameters.Length, captures.Length, IsTemplate: true);
        }

        internal int Function(HirFunctionId id) => Locations.Functions[id];
        internal int Callable(HirCallableId id) => id.IsTemplate
            ? Locations.Templates[new(id.Value)] : Function(new(id.Value));
        internal int TemplateFunction(HirTemplateId id) => Locations.Templates[id];
        internal bool IsCaptured(HirLocationId location) => captured.Contains(location) || roots.Contains(location)
            || SharedImport(location);
        internal bool NeedsFieldBinding(HirLocationId location) =>
            hir.Locations[location.Value].Kind != HirLocationKind.Field || fieldBindings.Contains(location);
        internal bool IsRoot(HirLocationId location) => roots.Contains(location);
        internal bool SharedImport(HirLocationId location) => hir.Locations[location.Value] is
        { Owner: var owner, Kind: HirLocationKind.Import, TemplateOwner: null } && owner == hir.Entry
            && !hir.UnresolvedImports.Contains(location);
        internal IEnumerable<HirLocationId> FunctionLocations(HirFunctionId owner) =>
            functionLocations.GetValueOrDefault(owner, []);
        internal int Slot(HirFunctionId function, HirLocationId location) => Locations.Locations[new(function, location)];
        internal void BindSlot(HirFunctionId function, HirLocationId location, int slot) =>
            Locations.Locations[new(function, location)] = slot;
        internal bool TrySlot(HirFunctionId function, HirLocationId location, out int slot) =>
            Locations.Locations.TryGetValue(new(function, location), out slot);
        internal SourceSpan? Source(HirSourceRange source) => source.Source.Value < 0 ? null :
            new(hir.Sources[source.Source.Value].Name, source.Start, source.Length);
        internal TypeRef Runtime(HirTypeId type) => historical is null ? hir.Types[type.Value].RuntimeType : HistoricalRuntime(type);
        internal int LocationType(HirLocationId location)
        {
            if (fieldStorage.TryGetValue(location, out int physical)) return physical;
            if (session.StorageTypes.TryGetValue(location, out physical)) return physical;
            var stored = hir.Locations[location.Value];
            physical = Type(stored.StorageType, stored.AdmitsNull);
            session.StorageTypes.Add(location, physical);
            return physical;
        }
        private void BindFieldContracts()
        {
            for (int constructionIndex = 0; constructionIndex < hir.Scopes.Length; constructionIndex++)
            {
                var construction = hir.Scopes[constructionIndex];
                var fields = session.Layouts[ScopeLayout(constructionIndex)].Fields;
                for (int i = 0; i < construction.Fields.Length; i++)
                {
                    fieldStorage[construction.Fields[i]] = fields[i].Type;
                    session.StorageTypes[construction.Fields[i]] = fields[i].Type;
                }
            }
            Dictionary<HirLocationId, List<HirLocationId>> sources = [];
            foreach (var closure in hir.Closures)
                foreach (var capture in closure.Captures)
                    if (capture.Mode == HirCaptureMode.Location) Record(capture);
            foreach (var function in hir.Functions)
                foreach (var capture in function.Captures)
                    if (capture.Mode == HirCaptureMode.Location) Record(capture);
            BindSessionCaptures(Record);
            HashSet<HirLocationId> visiting = [];
            foreach (var target in sources.Keys) Resolve(target);

            void Record(HirCapture capture)
            {
                if (capture.SourceLocation == capture.Target) return;
                if (!sources.TryGetValue(capture.Target, out var values)) sources.Add(capture.Target, values = []);
                if (!values.Contains(capture.SourceLocation)) values.Add(capture.SourceLocation);
            }
            int Resolve(HirLocationId location)
            {
                if (fieldStorage.TryGetValue(location, out int known)) return known;
                if (session.StorageTypes.TryGetValue(location, out known)) return known;
                if (!sources.TryGetValue(location, out var candidates) || !visiting.Add(location))
                {
                    var stored = hir.Locations[location.Value];
                    return Type(stored.StorageType, stored.AdmitsNull);
                }
                int type = Resolve(candidates[0]);
                foreach (var candidate in candidates.Skip(1))
                    if (Resolve(candidate) != type)
                        throw new InvalidOperationException("A shared capture requires distinct fixed storage ABIs.");
                visiting.Remove(location); fieldStorage[location] = type;
                session.StorageTypes[location] = type; return type;
            }
        }
        private void BindSessionCaptures(Action<HirCapture> record)
        {
            if (!hir.IsSession) return;
            foreach (var closure in hir.TemplateClosures)
                for (int i = 0; i < closure.Captures.Length; i++)
                {
                    var capture = closure.Captures[i];
                    if (capture.Mode != HirCaptureMode.Location) continue;
                    var location = hir.Locations[capture.SourceLocation.Value];
                    if (location.Owner == hir.Entry && location.TemplateOwner is null)
                        session.RootCaptures[(closure.Template, i)] = capture.SourceLocation;
                }
            foreach (var function in hir.Functions)
            {
                if (function.Template is not { } template) continue;
                for (int i = 0; i < function.Captures.Length; i++)
                    if (function.Captures[i].Mode == HirCaptureMode.Location
                        && session.RootCaptures.TryGetValue((template, i), out var source))
                        record(function.Captures[i] with { SourceLocation = source });
            }
        }

        internal IEnumerable<int> CallResultTypes(HirCall call)
        {
            if (HistoricalFunction(call.NativeName, out int historicalTarget))
            { yield return HistoricalResult(historicalTarget); yield break; }
            if (call.Kind == HirCallKind.Path || call.Kind == HirCallKind.Native && call.SessionSlot < 0)
                yield break;
            if (call.Kind == HirCallKind.Function && call.SessionSlot < 0)
                yield return Type(hir.Functions[call.Function.Value].Result);
            if (!call.Targets.IsDefaultOrEmpty)
                foreach (var target in call.Targets)
                    yield return Type(hir.Functions[target.Target.Value].Result);
            if (call.SessionSlot >= 0 && !hir.SessionDispatches.IsDefaultOrEmpty)
                foreach (var dispatch in hir.SessionDispatches.Where(dispatch => dispatch.Slot == call.SessionSlot))
                    foreach (var target in dispatch.Targets)
                        yield return Type(hir.Functions[target.Target.Value].Result);
        }

        internal NativeCaptureContract[] CaptureContracts(ImmutableArray<HirCapture> captures) =>
            captures.Select(capture => new NativeCaptureContract(LocationType(capture.Target),
                capture.Mode == HirCaptureMode.Location ? NativeCaptureMode.Location : NativeCaptureMode.Value)).ToArray();
        internal int Constant(NativeConstant literal)
        {
            if (constantIndices.TryGetValue(literal, out int index)) return index;
            index = constants.Count; constants.Add(literal); constantIndices.Add(literal, index); return index;
        }
        internal void Allocation(int function, int instruction, int layout) =>
            allocations.Add(new(instruction, layout, Function: function));
        internal int Site(int construction)
        {
            var scope = hir.Scopes[construction];
            var identity = scope.Site ?? throw new InvalidOperationException("A finalized constructor requires an allocation-site identity.");
            if (Locations.AllocationSites.TryGetValue(identity, out int site)) return site;
            site = Locations.NextAllocation++;
            Locations.AllocationSites.Add(identity, site);
            return site;
        }
        internal int Static(int construction)
        {
            int site = Site(construction);
            int layout = ScopeLayout(construction);
            if (session.StaticDeclarations.TryGetValue(site, out int id))
            {
                if (session.Statics[id].Layout != layout)
                    throw new InvalidOperationException("A named declaration changed its fixed layout.");
                return id;
            }
            id = session.Statics.Count;
            session.StaticDeclarations.Add(site, id); session.Statics.Add(new(layout, site)); return id;
        }
        private static HashSet<HirLocationId> Captured(HirModule hir)
        {
            HashSet<HirLocationId> result = [];
            foreach (var closure in hir.Closures)
                foreach (var capture in closure.Captures)
                    if (capture.Mode == HirCaptureMode.Location) result.Add(capture.SourceLocation);
            foreach (var closure in hir.TemplateClosures)
                foreach (var capture in closure.Captures)
                    if (capture.Mode == HirCaptureMode.Location) result.Add(capture.SourceLocation);
            return result;
        }

        private sealed class ConstantComparer : IEqualityComparer<NativeConstant>
        {
            public bool Equals(NativeConstant? left, NativeConstant? right) => ReferenceEquals(left, right)
                || left is not null && right is not null && left.Kind == right.Kind && left.Text == right.Text
                && BitConverter.DoubleToInt64Bits(left.Number) == BitConverter.DoubleToInt64Bits(right.Number)
                && left.Boolean == right.Boolean && left.Type == right.Type && left.ConnectionKind == right.ConnectionKind
                && left.Path == right.Path && left.OriginId == right.OriginId && left.OriginOffset == right.OriginOffset;
            public int GetHashCode(NativeConstant value) => HashCode.Combine(value.Kind, value.Text,
                BitConverter.DoubleToInt64Bits(value.Number), value.Boolean, value.Type, value.ConnectionKind,
                value.Path, HashCode.Combine(value.OriginId, value.OriginOffset));
        }
    }
}
