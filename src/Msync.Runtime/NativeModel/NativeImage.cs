using Msync.Core;
using Msync.Core.Native;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

internal sealed class NativeImage
{
    private readonly NativeType[] types;
    private readonly NativeLayout[] layouts;
    private readonly NativeEnvironmentLayout?[] environmentLayouts;
    private readonly Action<RuntimeObservation>? observe;
    private readonly FrameAllocation[] frames;
    private readonly NativeValue[] constants;
    private readonly TypeRef?[] sourceTypes;
    private readonly ConditionalWeakTable<NativeRuntimeContext, NativeValue[]> contextualConstants = new();
    private readonly int[] layoutLinkOrder;
    private readonly ConcurrentDictionary<NativeLayout, int> linkedLayouts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(NativeLayout Layout, NativeOffset Offset), int> frameLayouts = [];
    private readonly Dictionary<NativeLayout, NativeFrameRegion[]> frameRegions = new(ReferenceEqualityComparer.Instance);

    internal NativeImage(NativeProgram program, NativeRuntimeContext context,
        Dictionary<(int OriginId, int OriginOffset), NativeValue>? retainedLiterals = null)
    {
        Program = program;
        observe = context.Observe;
        EnvironmentLayoutResolver = EnvironmentLayout;
        Connections = program.Backend?.Connections.Select(context.Connections.Get).ToArray() ?? [];
        layoutLinkOrder = program.Types.Where(type => (type.Kind & TypeKind.Scope) != 0)
            .SelectMany(type => type.Layouts ?? []).Concat(Enumerable.Range(0, program.Layouts.Count)).Distinct().ToArray();
        types = program.Types.Select(StorageType).ToArray();
        sourceTypes = new TypeRef?[program.Types.Count];
        for (int index = 0; index < sourceTypes.Length; index++) sourceTypes[index] = BuildSourceType(index);
        layouts = program.Layouts.Select(layout => new NativeLayout(layout.Fields.Select(
            field => new NativeField(field.Name, types[field.Type])))).ToArray();
        environmentLayouts = new NativeEnvironmentLayout?[layouts.Length];
        ILookup<int, NativeAllocationPlan> framePlans = program.Allocations
            .Where(plan => plan.Placement == NativeAllocationPlacement.Frame).ToLookup(plan => plan.Function);
        HashSet<int>? incoming = null;
        foreach (NativeFunctionCode code in program.Functions)
            foreach (NativeCallStorage call in code.Storage!.Calls)
                if ((code.Storage.Instructions[call.Instruction] & NativeInstructionStorage.OwnArguments) != 0)
                    (incoming ??= []).Add(code.Instructions[call.Instruction].B);
        frames = program.Functions.Select((function, number) => new FrameAllocation(program, number,
            new NativeLayout(function.Slots.Select((slot, index) =>
                new NativeField(slot.Name ?? $"%{index}", types[slot.Type]))), types, framePlans[number].ToArray(), Layout,
                incoming?.Contains(number) == true)).ToArray();
        foreach (FrameAllocation frame in frames)
        {
            if (ReferenceEquals(frame.Layout, frame.Registers)) continue;
            List<NativeFrameRegion> regions = [new(default, frame.Registers),
                .. frame.Regions.Select(region => new NativeFrameRegion(region.Offset, layouts[region.Layout]))];
            if (frame.AppendedWindow is { } window) regions.Add(window);
            frameRegions.Add(frame.Layout, regions.ToArray());
            foreach (NativeFrameAllocation region in frame.Regions)
                frameLayouts.Add((frame.Layout, region.Offset), region.Layout);
        }
        NativeRuntimeContext origin = context.CaptureDirectory();
        constants = program.Constants.Select(value => BindLiteral(value, origin, retainedLiterals)).ToArray();
        for (int index = 0; index < layouts.Length; index++) linkedLayouts.TryAdd(layouts[index], index);
    }

    internal NativeConnections.NativeEndpoint[] Connections { get; }
    internal NativeProgram Program { get; }
    internal Func<NativeAddress, NativeEnvironmentLayout> EnvironmentLayoutResolver { get; }
    internal NativeType Type(int index) => types[index];
    internal NativeLayout Layout(int index) => layouts[index];
    internal NativeEnvironmentLayout EnvironmentLayout(NativeAddress address)
    {
        if (address.Allocation.Layout.EnvironmentSchema is { } schema) return schema;
        int index = LayoutIdentity(address);
        if (Volatile.Read(ref environmentLayouts[index]) is { } existing) return existing;
        var layout = new NativeEnvironmentLayout(Program.Layouts[index].Fields.Select(field =>
        {
            int pointee = Program.Types[field.Type].Pointee;
            return new NativeCaptureLayout(Type(pointee >= 0 ? pointee : field.Type), pointee >= 0);
        }));
        var published = Interlocked.CompareExchange(ref environmentLayouts[index], layout, null);
        if (published is not null) return published;
        observe?.Invoke(new("function-environment-schema", "linked",
            Detail: $"layout={index}; captures={layout.Captures.Count}; locations={layout.Captures.Count(capture => capture.Location)}; source=producer-storage"));
        return layout;
    }
    internal FrameAllocation Frame(int function) => frames[function];
    internal bool TryRegionLayout(NativeAddress address, out NativeLayout layout)
    {
        if (frameLayouts.TryGetValue((address.Allocation.Layout, address.Offset), out int identity))
        {
            layout = layouts[identity];
            return true;
        }
        layout = null!;
        return false;
    }

    internal NativeAddress IndexAddress(NativeAddress address, int index, NativeLayout stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        NativeAddress result = address.OffsetBy(stride.Extent * index);
        CheckRegionBounds(address, result, stride, 1);
        return result;
    }

    internal void CheckRegionBounds(NativeAddress address, NativeLayout layout, int count) =>
        CheckRegionBounds(address, address, layout, count);

    private void CheckRegionBounds(NativeAddress origin, NativeAddress address, NativeLayout layout, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (frameRegions.TryGetValue(origin.Allocation.Layout, out NativeFrameRegion[]? regions))
        {
            NativeFrameRegion region = Region(origin.Offset, regions);
            NativeOffset end = address.Offset + layout.Extent * count;
            NativeOffset limit = region.Offset + region.Layout.Extent;
            if (address.Offset.Bytes < region.Offset.Bytes || address.Offset.References < region.Offset.References
                || end.Bytes > limit.Bytes || end.References > limit.References)
                throw new ArgumentOutOfRangeException(nameof(count), "Native region exceeds its logical allocation.");
        }
        NativeStorage.CheckRegionBounds(address, layout, count);
    }

    private static NativeFrameRegion Region(NativeOffset offset, IReadOnlyList<NativeFrameRegion> regions)
    {
        foreach (NativeFrameRegion region in regions)
            if (offset.Bytes >= region.Offset.Bytes && offset.Bytes < checked(region.Offset.Bytes + region.Layout.RawSize))
                return region;
        throw new ArgumentOutOfRangeException(nameof(offset), "Native address is outside its logical allocation.");
    }

    internal TypeRef? OptionalSourceType(int index) => sourceTypes[index];
    internal TypeRef SourceType(int index) => sourceTypes[index] ??
        throw new InvalidOperationException($"Native machine type #{index} has no source-language projection.");

    private TypeRef? BuildSourceType(int index)
    {
        NativeCodeType type = Program.Types[index];
        if (type.IsMachine) return null;
        return new(type.Kind, type.Element >= 0 ? BuildSourceType(type.Element) : null);
    }

    internal NativeCallableDescription? DescribeFunction(NativeFunction function)
    {
        if (function.NativeName is { } name)
            return NativeSignatures.TryGet(name, out Msync.Core.NativeSignature? signature)
                ? new(signature.Parameters, signature.Result, signature.Variadic) : null;
        NativeFunctionCode definition = Program.Functions[function.Function];
        if (definition.ReflectionSignature is { } reflected) return new(reflected.Parameters, reflected.Result);
        Msync.Core.Native.NativeSignature code = definition.Signature;
        if (code.Parameters.Any(type => OptionalReflectedType(type) is null) || OptionalReflectedType(code.Result) is null)
            return null;
        return new(code.Parameters.Select(ReflectedType).ToArray(), ReflectedType(code.Result));
    }

    private TypeRef ReflectedType(int type)
    {
        int pointee = Program.Types[type].Pointee;
        return SourceType(pointee >= 0 ? pointee : type);
    }
    private TypeRef? OptionalReflectedType(int type)
    {
        int pointee = Program.Types[type].Pointee;
        return OptionalSourceType(pointee >= 0 ? pointee : type);
    }

    internal NativeValue Convert(NativeValue value, int target, bool explicitConversion, NativeRuntimeContext origin)
    {
        TypeRef type = SourceType(target);
        if (value.Kind == NativeKind.Address && Type(target).Accepts(NativeKind.Address)
            && !AdmitsLayout(target, value.AsAddress()))
            throw new MsyncException($"Native allocation layout #{value.AsAddress().Allocation.LayoutId} is outside the closed namespace contract {type} (type #{target}).");
        if (explicitConversion) return NativeBuiltins.ConvertValue(value, type, origin);
        if (type.Accepts(value.Type)) return value;
        if (type.WithoutNull.Kind == TypeKind.Path && value.Kind == NativeKind.String)
            return NativeValue.FromPath(origin.AsPath(value));
        if (type.WithoutNull.Kind == TypeKind.String && value.Kind == NativeKind.Path)
            return NativeValue.FromString(value.AsPath().NativeText);
        throw new MsyncException($"Cannot implicitly convert {value.Type} to {type}.");
    }

    internal NativeValue Narrow(NativeValue value, NativeType storage, int type, SourceSpan? source, string function, int pc) => storage.Accepts(value)
        && (value.Kind != NativeKind.Address || AdmitsLayout(type, value.AsAddress()))
        ? value : throw new MsyncException($"Expected {SourceType(type)}, received {value.Type}{(value.Kind == NativeKind.Address ? $" (allocation layout #{value.AsAddress().Allocation.LayoutId})" : "")}. Native Narrow in function '{function}', instruction {pc}, type #{type}.", source);

    internal NativeValue Constant(int index, NativeRuntimeContext? invocation = null)
    {
        if (invocation is null || Program.Constants[index].Kind != NativeKind.Path) return constants[index];
        return contextualConstants.GetValue(invocation, origin => Program.Constants.Select((literal, slot) =>
            literal.Kind == NativeKind.Path ? Literal(literal, origin) : constants[slot]).ToArray())[index];
    }

    private static NativeValue BindLiteral(NativeConstant literal, NativeRuntimeContext context,
        Dictionary<(int OriginId, int OriginOffset), NativeValue>? retained)
    {
        if (literal.Kind != NativeKind.Path || literal.OriginId < 0 || retained is null)
            return Literal(literal, context);
        var identity = (literal.OriginId, literal.OriginOffset);
        lock (retained)
        {
            if (retained.TryGetValue(identity, out NativeValue previous)) return previous;
            NativeValue value = Literal(literal, context);
            retained.Add(identity, value);
            return value;
        }
    }

    internal static NativeValue Literal(NativeConstant literal, NativeRuntimeContext context) => literal.Kind switch
    {
        NativeKind.Null => NativeValue.Null,
        NativeKind.Bool => NativeValue.FromBool(literal.Boolean),
        NativeKind.Number => NativeValue.FromNumber(literal.Number),
        NativeKind.MachineInteger or NativeKind.MachineFloat => literal.ToMachineValue(),
        NativeKind.String => NativeValue.FromString(literal.Text ?? ""),
        NativeKind.Path => NativeValue.FromPath(literal.Path ?? context.Capture(new NativePathValue(literal.Text ?? ""))),
        NativeKind.Connection => NativeValue.FromConnectionResource(context.Connections.Get(new(literal.ConnectionKind, literal.Text ?? ""))),
        NativeKind.Type => NativeValue.FromType(literal.Type ?? throw new InvalidOperationException("Missing Type literal.")),
        _ => throw new InvalidOperationException($"{literal.Kind} is not a portable literal.")
    };

    internal NativeAddress AddressField(NativeAddress address, int layout, int field)
    {
        NativeFieldLayout member = layouts[layout][field];
        return address.OffsetBy(member.Offset, member.Type);
    }

    internal NativeAddress Project(NativeAddress address, int projection)
    {
        NativeProjection table = Program.Projections[projection];
        if (table.Cases.Count == 1)
        {
            NativeProjectionCase only = table.Cases[0];
            return AddressField(address, only.Layout, only.Field);
        }
        int layout = LayoutIdentity(address);
        for (int index = 0; index < table.Cases.Count; index++)
        {
            NativeProjectionCase selected = table.Cases[index];
            if (selected.Layout != layout) continue;
            NativeFieldLayout member = layouts[layout][selected.Field];
            return address.OffsetBy(member.Offset, member.Type);
        }
        throw new InvalidOperationException($"Allocation layout {layout} is outside its compiled address projection.");
    }

    internal bool AdmitsLayout(int type, NativeAddress address) =>
        Program.Types[type].Layouts is not { } admitted || admitted.Contains(LayoutIdentity(address.Allocation, address.Offset));

    internal bool AdmitsLayout(int type, NativeAllocation allocation, NativeOffset offset) =>
        Program.Types[type].Layouts is not { } admitted || admitted.Contains(LayoutIdentity(allocation, offset));

    internal bool HasLayout(int layout, NativeAddress address) => LayoutIdentity(address) == layout;

    internal int LayoutIdentity(NativeAddress address) => LayoutIdentity(address.Allocation, address.Offset);

    internal int LayoutIdentity(NativeAllocation allocation, NativeOffset offset)
    {
        if (allocation.LayoutId >= 0) return allocation.LayoutId;
        if (frameLayouts.TryGetValue((allocation.Layout, offset), out int region)) return region;
        if (linkedLayouts.TryGetValue(allocation.Layout, out int identity)) return identity;
        foreach (int index in layoutLinkOrder)
        {
            if (!SameLayout(layouts[index], allocation.Layout)) continue;
            linkedLayouts.TryAdd(allocation.Layout, index);
            return index;
        }
        throw new InvalidOperationException("A native call returned a layout absent from its compiled ABI.");
    }

    private static bool SameLayout(NativeLayout left, NativeLayout right)
    {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
            if (left[index].Name != right[index].Name || !left[index].Type.Equals(right[index].Type)) return false;
        return true;
    }

    private static NativeType StorageType(NativeCodeType type) => type.PhysicalType;
}
