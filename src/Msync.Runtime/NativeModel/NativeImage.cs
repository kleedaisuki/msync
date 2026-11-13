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
    private readonly FrameAllocation[] frames;
    private readonly NativeValue[] constants;
    private readonly ConditionalWeakTable<NativeRuntimeContext, NativeValue[]> contextualConstants = new();
    private readonly int[] layoutLinkOrder;
    private readonly ConcurrentDictionary<NativeLayout, int> linkedLayouts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(NativeLayout Layout, NativeOffset Offset), int> frameLayouts = [];
    private readonly Dictionary<NativeLayout, NativeFrameRegion[]> frameRegions = new(ReferenceEqualityComparer.Instance);

    internal NativeImage(NativeProgram program, NativeRuntimeContext context,
        Dictionary<(int OriginId, int OriginOffset), NativeValue>? retainedLiterals = null)
    {
        Program = program;
        Provenance = new(program);
        layoutLinkOrder = program.Types.Where(type => (type.Kind & TypeKind.Scope) != 0)
            .SelectMany(type => type.Layouts ?? []).Concat(Enumerable.Range(0, program.Layouts.Count)).Distinct().ToArray();
        types = program.Types.Select(StorageType).ToArray();
        layouts = program.Layouts.Select(layout => new NativeLayout(layout.Fields.Select(
            field => new NativeField(field.Name, types[field.Type])))).ToArray();
        ILookup<int, NativeAllocationPlan> framePlans = program.Allocations
            .Where(plan => plan.Placement == NativeAllocationPlacement.Frame).ToLookup(plan => plan.Function);
        frames = program.Functions.Select((function, number) => new FrameAllocation(program, number,
            new NativeLayout(function.Slots.Select((slot, index) =>
                new NativeField(slot.Name ?? $"%{index}", types[slot.Type]))), types, framePlans[number].ToArray(), Layout, Provenance)).ToArray();
        foreach (FrameAllocation frame in frames)
        {
            if (ReferenceEquals(frame.Layout, frame.Registers)) continue;
            frameRegions.Add(frame.Layout, [new(default, frame.Registers),
                .. frame.Regions.Select(region => new NativeFrameRegion(region.Offset, layouts[region.Layout]))]);
            foreach (NativeFrameAllocation region in frame.Regions)
                frameLayouts.Add((frame.Layout, region.Offset), region.Layout);
        }
        NativeRuntimeContext origin = context.CaptureDirectory();
        constants = program.Constants.Select(value => BindLiteral(value, origin, retainedLiterals)).ToArray();
        for (int index = 0; index < layouts.Length; index++) linkedLayouts.TryAdd(layouts[index], index);
    }

    internal NativeProgram Program { get; }
    internal NativeAddressAnalysis.ProgramFacts Provenance { get; }
    internal NativeType Type(int index) => types[index];
    internal NativeLayout Layout(int index) => layouts[index];
    internal FrameAllocation Frame(int function) => frames[function];
    internal NativeFrameAllocation? FrameAllocation(int function, int instruction) => frames[function].At(instruction);
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

    internal TypeRef SourceType(int index)
    {
        NativeCodeType type = Program.Types[index];
        return new(type.Kind, type.Element >= 0 ? SourceType(type.Element) : null);
    }

    internal NativeCallableDescription? DescribeFunction(NativeFunction function)
    {
        if (function.NativeName is { } name)
            return NativeSignatures.TryGet(name, out Msync.Core.NativeSignature? signature)
                ? new(signature.Parameters, signature.Result, signature.Variadic) : null;
        NativeFunctionCode definition = Program.Functions[function.Function];
        if (definition.ReflectionSignature is { } reflected) return new(reflected.Parameters, reflected.Result);
        Msync.Core.Native.NativeSignature code = definition.Signature;
        return new(code.Parameters.Select(ReflectedType).ToArray(), ReflectedType(code.Result));
    }

    private TypeRef ReflectedType(int type)
    {
        int pointee = Program.Types[type].Pointee;
        return SourceType(pointee >= 0 ? pointee : type);
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

    internal NativeValue Narrow(NativeValue value, NativeType storage, int type, SourceSpan? source, string function, int pc) => storage.Accepts(value.Kind)
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
        NativeKind.String => NativeValue.FromString(literal.Text ?? ""),
        NativeKind.Path => NativeValue.FromPath(literal.Path ?? context.Capture(new NativePathValue(literal.Text ?? ""))),
        NativeKind.Connection => NativeValue.FromConnection(new(literal.ConnectionKind, literal.Text ?? "")),
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

    internal bool AdmitsLayout(int type, NativeAddress address)
    {
        IReadOnlyList<int>? admitted = Program.Types[type].Layouts;
        return admitted is null || admitted.Contains(LayoutIdentity(address));
    }

    internal bool HasLayout(int layout, NativeAddress address) => LayoutIdentity(address) == layout;

    internal int LayoutIdentity(NativeAddress address)
    {
        NativeAllocation allocation = address.Allocation;
        if (allocation.LayoutId >= 0) return allocation.LayoutId;
        if (frameLayouts.TryGetValue((allocation.Layout, address.Offset), out int region)) return region;
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

    private static NativeType StorageType(NativeCodeType type) => type.Pointee >= 0
        ? NativeType.Of(NativeKind.Address) : NativeType.FromTypeRef(new(type.Kind));
}
