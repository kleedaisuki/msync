using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

/// <summary>Executable layout metadata derived exclusively from the portable place tables.</summary>
internal sealed class BoundScopePlaces
{
    private static readonly ConditionalWeakTable<Module, BoundScopePlaces> images = new();
    private readonly Module module;
    private readonly ScopeLayout[] layouts;
    private readonly Projection[] fields;
    private readonly int[]?[] domains;
    private readonly ParameterAdmission[]?[] admissions;
    private CodeStorageWitness?[]? storage;

    internal readonly record struct ParameterAdmission(int Position, int CodeType);

    private BoundScopePlaces(Module module)
    {
        this.module = module;
        layouts = new ScopeLayout[module.ScopeLayouts.Count];
        for (int layout = 0; layout < layouts.Length; layout++)
            layouts[layout] = new ScopeLayout(module.ScopeLayouts[layout].Fields.Select(field => field.Name), layout);
        fields = new Projection[module.FieldProjections.Count];
        for (int field = 0; field < fields.Length; field++)
            fields[field] = new(module.FieldProjections[field]);
        domains = new int[]?[module.CodeTypes.Count];
        for (int type = 0; type < domains.Length; type++)
            if (module.CodeTypes[type].ScopeLayouts is { Count: > 0 } cases)
                domains[type] = cases as int[] ?? cases.ToArray();
        admissions = new ParameterAdmission[]?[module.Functions.Count];
        for (int function = 0; function < admissions.Length; function++)
        {
            var parameters = module.Functions[function].CodeSignature?.Parameters;
            if (parameters is null) continue;
            List<ParameterAdmission>? required = null;
            for (int i = 0; i < parameters.Count; i++)
                if (domains[parameters[i]] is not null)
                    (required ??= []).Add(new(i, parameters[i]));
            admissions[function] = required?.ToArray();
        }
    }

    internal static BoundScopePlaces For(Module module) => images.GetValue(module, static image => new(image));

    internal ScopeLayout Layout(int index) => layouts[index];

    internal ScopeLayout StaticLayout(int index) => Layout(module.StaticScopes[index].Layout);

    internal int Field(ScopeValue owner, int projection) => fields[projection].Ordinal(owner);

    internal bool TryField(ScopeValue owner, int projection, out int field) => TryField(owner.Layout, projection, out field);

    internal bool TryField(ScopeLayout layout, int projection, out int field) => fields[projection].TryOrdinal(layout, out field);

    internal int WriteField(ScopeValue owner, int projection, Value? value)
    {
        var place = fields[projection];
        int field = place.Ordinal(owner);
        if (!FitsStore(place, owner, field, value))
            throw new MsyncException($"The value does not fit the storage of Scope member '{place.Name}'.");
        return field;
    }

    internal bool TryWriteField(ScopeValue owner, int projection, Value? value, out int field)
    {
        var place = fields[projection];
        return place.TryOrdinal(owner.Layout, out field) && FitsStore(place, owner, field, value);
    }

    private bool FitsStore(Projection place, ScopeValue owner, int field, Value? value)
    {
        if (!place.Dynamic) return true;
        int layout = owner.Layout.CodeLayoutId;
        return layout < 0
            || Accepts(module.ScopeLayouts[layout].Fields[field].StorageType, value?.Type ?? TypeRef.Number,
                (value as ScopeValue)?.Layout.CodeLayoutId ?? -1);
    }

    internal void Admit(ScopeValue owner, int type)
    {
        if (!IsAdmitted(owner, type)) throw new MsyncException("The selected Scope does not have an admitted layout.");
    }

    internal bool IsAdmitted(ScopeValue owner, int type) => IsAdmitted(owner.Layout, type);

    internal bool IsAdmitted(ScopeLayout layout, int type) =>
        ContainsLayout(domains[type]!, layout.CodeLayoutId);

    internal bool FitsValue(int id, Value value) => Accepts(id, value.Type, (value as ScopeValue)?.Layout.CodeLayoutId ?? -1);
    internal ReadOnlySpan<ParameterAdmission> Parameters(int function) => admissions[function];

    internal void AdmitArgument(ParameterAdmission parameter, Value? value, SourceSpan? span)
    {
        if (value is null ? Accepts(parameter.CodeType, TypeRef.Number, -1) : FitsValue(parameter.CodeType, value))
            return;
        throw new MsyncException($"Argument {parameter.Position + 1} does not fit the selected Scope layout.", span);
    }

    internal CodeStorageWitness Storage(int id)
    {
        var witnesses = storage ??= new CodeStorageWitness?[module.CodeTypes.Count];
        if (witnesses[id] is { } known) return known;
        var type = module.CodeTypes[id];
        return witnesses[id] = new(type.Kind, type.Element < 0 ? null : Storage(type.Element), type.ScopeLayouts);
    }

    private bool Accepts(int id, TypeRef actual, int layout)
    {
        var type = module.CodeTypes[id];
        if ((actual.Kind & type.Kind) != actual.Kind) return false;
        if ((actual.Kind & TypeKind.Scope) != 0 && domains[id] is { } cases
            && !ContainsLayout(cases, layout)) return false;
        // A Range element is admitted when selected. Checking a field store
        // must not run its producer to discover element layouts.
        return type.Element < 0 || actual.Element is null || AcceptsKind(type.Element, actual.Element);
    }

    private bool AcceptsKind(int id, TypeRef actual)
    {
        var type = module.CodeTypes[id];
        return (actual.Kind & type.Kind) == actual.Kind
            && (type.Element < 0 || actual.Element is null || AcceptsKind(type.Element, actual.Element));
    }

    private static bool ContainsLayout(int[] cases, int layout) =>
        cases.Length == 1 ? cases[0] == layout : Array.BinarySearch(cases, layout) >= 0;

    private readonly struct Projection
    {
        private readonly string name;
        private readonly int uniform;
        private readonly CodeFieldCase[] alternatives;
        internal string Name => name;
        internal bool Dynamic => alternatives.Length == 0;

        internal Projection(CodeFieldProjection projection)
        {
            name = projection.Name;
            alternatives = projection.Cases as CodeFieldCase[] ?? projection.Cases.ToArray();
            uniform = alternatives.Length == 0 ? -1 : alternatives[0].Ordinal;
            foreach (var candidate in alternatives)
            {
                if (candidate.Ordinal == uniform) continue;
                uniform = -1;
                break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Ordinal(ScopeValue owner)
        {
            if (uniform >= 0) return uniform;
            if (TryOrdinal(owner.Layout, out int field)) return field;
            if (Dynamic) throw new MsyncException($"Scope has no member '{name}'");
            throw new InvalidOperationException("An admitted Scope layout has no executable field case.");
        }

        internal bool TryOrdinal(ScopeLayout owner, out int field)
        {
            if (Dynamic) return owner.TryGetOffset(name, out field);
            int layout = owner.CodeLayoutId;
            foreach (var candidate in alternatives)
                if (candidate.Layout == layout) { field = candidate.Ordinal; return true; }
            field = -1;
            return false;
        }
    }
}
