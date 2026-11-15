using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeExecutionInstruction
{
    internal readonly NativeOp Op;
    internal readonly int Result;
    internal readonly int A;
    internal readonly int B;
    internal readonly int C;
    internal readonly int Span;
    internal readonly long Immediate;
    internal readonly nint Handler;

    internal NativeExecutionInstruction(NativeInstruction source, int span = 1, nint handler = 0) =>
        (Op, Result, A, B, C, Span, Immediate, Handler) = (source.Op, source.Result, source.A, source.B, source.C,
            span, source.Immediate, handler == 0 ? NativeInterpreter.ExecutionHandlerAddress(source.Op) : handler);

    internal NativeExecutionInstruction(NativeBackendInstruction source) =>
        (Op, Result, A, B, C, Span, Immediate, Handler) = (source.Op, source.Result, source.A, source.B, source.C,
            source.Span, source.Immediate, NativeInterpreter.BackendHandlerAddress(source.Handler, source.Op));

    internal NativeExecutionInstruction(in NativeExecutionInstruction source, long immediate)
    {
        this = source;
        Immediate = immediate;
    }
}

internal sealed class NativeExecutionImage(NativeImage image, NativeTierOptions options) : IDisposable
{
    private readonly NativeExecutionFunction?[] functions = new NativeExecutionFunction?[image.Program.Functions.Count];
    internal NativeImage Image => image;
    internal NativeExecutionFunction Function(int index)
    {
        NativeExecutionFunction? existing = Volatile.Read(ref functions[index]);
        if (existing is not null) return existing;
        NativeExecutionFunction created = new(image, index, options);
        NativeExecutionFunction? winner = Interlocked.CompareExchange(ref functions[index], created, null);
        if (winner is null) return created;
        created.Dispose();
        return winner;
    }

    public void Dispose()
    {
        for (int index = 0; index < functions.Length; index++)
            Interlocked.Exchange(ref functions[index], null)?.Dispose();
    }
}

internal sealed partial class NativeExecutionFunction : IDisposable
{
    private readonly object gate = new();
    private readonly NativeImage image;
    private readonly NativeTierOptions options;
    private int calls;
    private int backedges;
    private int invalidations;
    private bool unavailable;
    private NativeCompiledFunction? compiled;
    private readonly NativeExecutionInstruction[] instructions;
    internal NativeExecutionOperand[] Operands { get; }
    private NativeSiteProfile?[]? profiles;
    private IReadOnlyList<bool>? rawWritable;
    private readonly int[] operandSources = [];
    private readonly NativeNumberForm[]? numberForms;
    private readonly NativeBackendFunction? backend;
    internal NativeNumberForm NumberForm(int pc) => backend is { } body ? body.Instructions[pc].Numbers : numberForms![pc];
    internal int ConnectionSlot(int pc) => backend!.Instructions[pc].Connection;

    internal NativeExecutionFunction(NativeImage image, int index, NativeTierOptions options, bool lowering = false, CompilationTrace? trace = null)
    {
        this.image = image;
        this.options = options;
        Index = index;
        Definition = image.Program.Functions[index];
        Metadata = Definition.Instructions as NativeInstruction[] ?? Definition.Instructions.ToArray();
        if (!lowering)
        {
            backend = image.Program.Backend!.Functions[index];
            IsPureSynchronous = backend.PureSynchronous;
            IsContextFree = backend.ContextFree;
            HasObservableBinary = backend.ObservableBinary;
            instructions = backend.Instructions.Select(code => new NativeExecutionInstruction(code)).ToArray();
            Operands = backend.Instructions.Select(code => NativeExecutionOperand.Link(image, Definition, Metadata[code.Operand]) with
            {
                Type = code.OperandKinds == ulong.MaxValue ? null : NativeType.FromKinds(code.OperandKinds),
                NeedsLayoutCheck = code.NeedsLayoutCheck
            }).ToArray();
            rawWritable = backend.RawWritable;
            LinkCalls();
            return;
        }
        operandSources = Enumerable.Range(0, Metadata.Length).ToArray();
        bool pureSynchronous = true;
        bool contextFree = true;
        bool observableBinary = false;
        foreach (NativeInstruction instruction in Metadata)
        {
            if (instruction.Op == NativeOp.Binary && !observableBinary)
            {
                if (image.Program.Types[Definition.Slots[instruction.A].Type].Kind != TypeKind.Number
                    || image.Program.Types[Definition.Slots[instruction.B].Type].Kind != TypeKind.Number)
                    observableBinary = true;
            }
            if (!pureSynchronous && !contextFree) continue;
            if (instruction.Op is NativeOp.Call or NativeOp.CallNative or NativeOp.Transfer
                or NativeOp.MakeLazyRange or NativeOp.DeferRange or NativeOp.OpenIterator
                or NativeOp.NextIterator or NativeOp.CloseIterator or NativeOp.ProcessCall
                or NativeOp.SelectPath or NativeOp.Barrier or NativeOp.Acquire
                or NativeOp.ExecutePlan or NativeOp.PlanResult)
            { pureSynchronous = false; contextFree = false; continue; }
            if (contextFree && (instruction.Op == NativeOp.Constant && image.Program.Constants[instruction.A].Kind == NativeKind.Path
                || instruction.Op == NativeOp.Binary && (NativeBinary)instruction.Immediate is NativeBinary.PathJoin or NativeBinary.Qualify))
                contextFree = false;
        }
        IsPureSynchronous = pureSynchronous;
        IsContextFree = contextFree;
        HasObservableBinary = observableBinary;
        Operands = Metadata.Select(instruction => NativeExecutionOperand.Link(image, Definition, instruction)).ToArray();
        instructions = Lower();
        numberForms = trace is null ? NativeNumberLowering.Analyze(image.Program, Definition, instructions)
            : trace.MeasureDetailed("backend.number_analysis", 0, _ => NativeNumberLowering.Analyze(image.Program, Definition, instructions, trace));
    }

    internal NativeBackendFunction ExportBackend() => backend ?? new(instructions.Select((code, pc) =>
        new NativeBackendInstruction(code.Op, code.Result, code.A, code.B, code.C, code.Span, code.Immediate,
            code.Op == NativeOp.Binary && code.Handler == NativeInterpreter.ExecutionHandlerAddress(NativeOp.Binary, number: true)
                && NumberForm(pc).HasFlag(NativeNumberForm.UnsignedA | NativeNumberForm.UnsignedB)
                ? NativeBackendHandler.UnsignedBinary : NativeInterpreter.BackendHandlerKind(code.Handler, code.Op), operandSources[pc],
            Operands[pc].Type?.Kinds ?? ulong.MaxValue, Operands[pc].NeedsLayoutCheck, NumberForm(pc))).ToArray(),
        RawWritable, IsPureSynchronous, IsContextFree, HasObservableBinary);

    internal int Index { get; }
    internal bool IsPureSynchronous { get; }
    internal bool IsContextFree { get; }
    internal bool HasObservableBinary { get; }
    internal bool IsUnavailable => Volatile.Read(ref unavailable);
    internal NativeFunctionCode Definition { get; }
    internal NativeInstruction[] Metadata { get; }
    internal NativeExecutionInstruction[] Instructions => instructions;
    private bool Escapes(int slot) => Definition.Slots[slot].Escapes
        || Definition.Storage!.Slots[slot].HasFlag(NativeSlotStorage.Escapes);
    internal bool HasKind(int slot, NativeKind kind) => image.Program.Types[Definition.Slots[slot].Type] is { Pointee: < 0 }
        && image.Type(Definition.Slots[slot].Type) is { IsNever: false, IsUnion: false } type && type.Kind == kind;
    internal bool CanWriteRaw(int slot) => slot >= 0 && RawWritable[slot]
        && image.Type(Definition.Slots[slot].Type).Representation.References == 0;
    internal bool IsPrivateTemporary(int slot) => Definition.Storage!.Slots[slot].HasFlag(NativeSlotStorage.PrivateArgument);
    internal bool InitializationUnobserved(int slot) =>
        Definition.Storage!.Slots[slot].HasFlag(NativeSlotStorage.UnobservedInitialization);
    internal bool IsUnion(int slot) => image.Type(Definition.Slots[slot].Type).IsUnion;
    internal bool AdmitsKind(int slot, NativeKind kind) => image.Type(Definition.Slots[slot].Type).Accepts(kind);
    private NativeExecutionInstruction[] Lower()
    {
        NativeExecutionInstruction[] code = Metadata.Select(instruction => new NativeExecutionInstruction(instruction)).ToArray();
        bool[] entry = new bool[code.Length];
        NativeControlFlow flow = new(Metadata);
        HashSet<int>[] liveAfter = NativeRegisterPlan.LiveAfter(Metadata, flow);
        for (int pc = 0; pc < code.Length; pc++)
            foreach (int next in flow.Successors[pc]) if (next != pc + 1) entry[next] = true;
        HashSet<int> exposed = Metadata.Where(instruction => instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation)
            .Select(instruction => instruction.A).ToHashSet();
        HashSet<int> rebound = Metadata.Where(instruction => instruction.Op == NativeOp.BindLocation)
            .Select(instruction => instruction.A).ToHashSet();
        bool CanElideTemp(int slot) => slot >= Definition.CaptureCount
            && Definition.Slots[slot].Role == NativeSlotRole.Temporary
            && !Definition.Slots[slot].Escapes
            && !exposed.Contains(slot)
            && FixedCell(slot, rebound);
        for (int pc = 0; pc + 1 < code.Length; pc++)
        {
            NativeInstruction address = Metadata[pc], access = Metadata[pc + 1];
            bool deadTemp = !liveAfter[pc + 1].Contains(address.Result)
                || NativeInstructionFacts.Definitions(access).Contains(address.Result);
            if (entry[pc + 1] || !deadTemp) continue;
            if (address.Op == NativeOp.FrameAddress && CanElideTemp(address.Result) && access.A == address.Result)
            {
                if (access.Op == NativeOp.Load)
                    code[pc] = new(access with { Op = NativeOp.Copy, A = address.A }, 2);
                else if (access.Op == NativeOp.Store && access.B != address.Result)
                    code[pc] = new(access with { Result = address.A, A = access.B }, 2, NativeInterpreter.FrameStoreHandler);
                else continue;
                Operands[pc] = Operands[pc + 1];
                operandSources[pc] = operandSources[pc + 1];
                pc++;
            }
            else if (address.Op == NativeOp.AddressOffset && CanElideTemp(address.Result))
            {
                if (access.Op == NativeOp.Load && access.A == address.Result)
                    code[pc] = new(access with { A = address.A }, 2, NativeInterpreter.OffsetLoadHandler);
                else if (access.Op == NativeOp.Store && access.A == address.Result && access.B != address.Result)
                    code[pc] = new(access with { A = address.A }, 2, NativeInterpreter.OffsetStoreHandler);
                else if (access.Op == NativeOp.BindLocation && access.B == address.Result)
                    code[pc] = new(access with { B = address.A }, 2, NativeInterpreter.OffsetBindHandler);
                else continue;
                pc++;
            }
            else if (address.Op == NativeOp.ProjectAddress && CanElideTemp(address.Result))
            {
                if (access.Op == NativeOp.Load && access.A == address.Result)
                    code[pc] = new(access with { A = address.A }, 2, NativeInterpreter.ProjectLoadHandler);
                else if (access.Op == NativeOp.Store && access.A == address.Result && access.B != address.Result)
                    code[pc] = new(access with { A = address.A }, 2, NativeInterpreter.ProjectStoreHandler);
                else continue;
                pc++;
            }
        }
        int[] defCount = new int[Definition.Slots.Count];
        int[] singleDefPc = new int[Definition.Slots.Count];
        for (int pc = 0; pc < code.Length; pc += code[pc].Span)
        {
            int result = code[pc].Result;
            if (result < 0) continue;
            defCount[result]++;
            singleDefPc[result] = pc;
        }
        int[] blockStart = new int[code.Length];
        foreach (NativeControlFlow.Block block in flow.Blocks)
            for (int i = block.Start; i < block.End; i++) blockStart[i] = block.Start;
        bool[] unfusedExposed = new bool[Definition.Slots.Count];
        bool[] safeValueSlot = new bool[Definition.Slots.Count];
        for (int pc = 0; pc < code.Length; pc += code[pc].Span)
        {
            if (code[pc].Op is NativeOp.FrameAddress or NativeOp.BindLocation
                || (code[pc].Span == 1 && Metadata[pc].Op == NativeOp.FrameAddress))
                unfusedExposed[Metadata[pc].A] = true;
        }
        for (int slot = 0; slot < safeValueSlot.Length; slot++)
        {
            safeValueSlot[slot] = FixedCell(slot, rebound)
                && !unfusedExposed[slot]
                && !Definition.Slots[slot].Escapes
                && !Escapes(slot);
        }
        int[] slotVersion = new int[Definition.Slots.Count];
        (int Slot, int Version)[] copyOrigin = new (int, int)[Definition.Slots.Count];
        Array.Fill(copyOrigin, (-1, 0));
        Dictionary<int, int> blockProvenTypes = [];
        for (int pc = 0; pc < code.Length; pc += code[pc].Span)
        {
            if (entry[pc] || pc == blockStart[pc]) blockProvenTypes.Clear();
            NativeExecutionInstruction instruction = code[pc];
            int tailPc = pc + instruction.Span - 1;
            if (instruction.Result >= 0 && !liveAfter[tailPc].Contains(instruction.Result)
                && safeValueSlot[instruction.Result]
                && (instruction.Op == NativeOp.MakeFunction && (instruction.A < 0 || instruction.C == 0 || Metadata[pc].Inputs is null or { Count: 0 })
                    || instruction.Op == NativeOp.Constant && image.Program.Constants[instruction.A].Kind is NativeKind.Number or NativeKind.Bool or NativeKind.Null or NativeKind.String))
            {
                blockProvenTypes.Remove(instruction.Result);
                slotVersion[instruction.Result]++;
                code[pc] = new(Metadata[pc], instruction.Span, NativeInterpreter.NopHandler);
                continue;
            }
            if (instruction.Op == NativeOp.Narrow
                && ((blockProvenTypes.TryGetValue(instruction.A, out int provenType) && CoversNarrowType(image, provenType, Metadata[pc].Type))
                    || RedundantNarrow(pc, instruction, rebound, code, defCount, singleDefPc, blockStart[pc])))
            {
                code[pc] = new(Metadata[pc] with { Op = NativeOp.Copy }, code[pc].Span);
            }
            else if (instruction.Op == NativeOp.Narrow && Metadata[pc].Type >= 0)
            {
                int narrowedType = Metadata[pc].Type;
                if (safeValueSlot[instruction.A]) blockProvenTypes[instruction.A] = narrowedType;
                (int originSlot, int originVersion) = copyOrigin[instruction.A];
                if (originSlot >= 0 && safeValueSlot[originSlot] && slotVersion[originSlot] == originVersion)
                    blockProvenTypes[originSlot] = narrowedType;
                if (instruction.Result >= 0 && safeValueSlot[instruction.Result])
                    blockProvenTypes[instruction.Result] = narrowedType;
                if (instruction.Result >= 0 && instruction.Result != instruction.A)
                    slotVersion[instruction.Result]++;
            }
            else if (instruction.Op == NativeOp.Move && (!liveAfter[pc].Contains(instruction.A) || instruction.A == instruction.Result))
            {
                code[pc] = new(Metadata[pc] with { Op = NativeOp.Copy }, code[pc].Span);
            }
            if (code[pc].Op == NativeOp.Copy)
            {
                int src = code[pc].A;
                int dst = code[pc].Result;
                if (dst >= 0)
                {
                    if (dst != src) slotVersion[dst]++;
                    if (safeValueSlot[dst])
                    {
                        if (blockProvenTypes.TryGetValue(src, out int copiedType)) blockProvenTypes[dst] = copiedType;
                        else blockProvenTypes.Remove(dst);
                    }
                    else blockProvenTypes.Remove(dst);
                    if (safeValueSlot[src])
                    {
                        (int originSlot, int originVersion) = copyOrigin[src];
                        copyOrigin[dst] = originSlot >= 0 && safeValueSlot[originSlot] && slotVersion[originSlot] == originVersion
                            ? (originSlot, originVersion)
                            : (src, slotVersion[src]);
                    }
                    else copyOrigin[dst] = (-1, 0);
                }
                Operands[pc] = Operands[pc] with { Type = null };
                if (FixedCell(src, rebound) && FixedCell(dst, rebound))
                {
                    NativeType type = image.Type(Definition.Slots[src].Type);
                    if (type.Equals(image.Type(Definition.Slots[dst].Type))) Operands[pc] = Operands[pc] with { Type = type };
                }
            }
            else if (instruction.Op == NativeOp.Binary && instruction.Immediate is >= 0 and <= (long)NativeBinary.GreaterEqual
                && HasKind(instruction.A, NativeKind.Number) && HasKind(instruction.B, NativeKind.Number)
                && HasKind(instruction.Result, instruction.Immediate >= (long)NativeBinary.Equal ? NativeKind.Bool : NativeKind.Number)
                && FixedCell(instruction.A, rebound) && FixedCell(instruction.B, rebound) && FixedCell(instruction.Result, rebound))
            {
                if (instruction.Result >= 0)
                {
                    blockProvenTypes.Remove(instruction.Result);
                    slotVersion[instruction.Result]++;
                    copyOrigin[instruction.Result] = (-1, 0);
                }
                code[pc] = new(Metadata[pc], handler: NativeInterpreter.ExecutionHandlerAddress(NativeOp.Binary, number: true));
            }
            else if (code[pc].Op != NativeOp.Narrow && code[pc].Result >= 0)
            {
                blockProvenTypes.Remove(code[pc].Result);
                slotVersion[code[pc].Result]++;
                copyOrigin[code[pc].Result] = (-1, 0);
            }
        }
        for (int pc = 0; pc < code.Length; pc += code[pc].Span)
        {
            NativeExecutionInstruction copy = code[pc];
            if (copy.Op != NativeOp.Copy || copy.Handler != NativeInterpreter.ExecutionHandlerAddress(NativeOp.Copy)
                || Operands[pc].Type is null) continue;
            if (!Definition.Storage!.Slots[copy.A].HasFlag(NativeSlotStorage.PrivateArgument)) continue;
            code[pc] = new(Metadata[pc] with { Op = NativeOp.Copy, A = copy.A, Result = copy.Result },
                copy.Span, NativeInterpreter.CompiledCopyHandler);
        }
        return code;
    }

    private static bool SameTypeShape(NativeProgram program, int left, int right)
    {
        if (left == right) return true;
        NativeCodeType a = program.Types[left], b = program.Types[right];
        if (a.Kind != b.Kind || a.Element != b.Element || a.Pointee != b.Pointee) return false;
        if (a.Layouts is null || b.Layouts is null) return a.Layouts is null && b.Layouts is null;
        return a.Layouts.Count == b.Layouts.Count && a.Layouts.SequenceEqual(b.Layouts);
    }

    private static bool CoversNarrowType(NativeImage image, int provenType, int targetType)
    {
        if (provenType < 0 || targetType < 0) return false;
        if (SameTypeShape(image.Program, provenType, targetType)) return true;
        NativeType sourceStorage = image.Type(provenType);
        NativeType targetStorage = image.Type(targetType);
        if ((sourceStorage.Kinds & ~targetStorage.Kinds) != 0) return false;
        if (!sourceStorage.Accepts(NativeKind.Address)) return true;
        IReadOnlyList<int>? admitted = image.Program.Types[targetType].Layouts;
        if (admitted is null) return true;
        if (admitted.Count == 0) return false;
        IReadOnlyList<int>? declared = image.Program.Types[provenType].Layouts;
        return declared is { Count: > 0 } && declared.All(admitted.Contains);
    }

    private bool RedundantNarrow(int pc, in NativeExecutionInstruction instruction, HashSet<int> rebound,
        NativeExecutionInstruction[] code, int[] defCount, int[] singleDefPc, int blockStart)
    {
        if (!FixedCell(instruction.A, rebound)) return false;
        int slotTypeIndex = Definition.Slots[instruction.A].Type;
        int targetTypeIndex = Metadata[pc].Type;
        if (targetTypeIndex < 0) return false;
        if (SameTypeShape(image.Program, slotTypeIndex, targetTypeIndex)) return true;
        NativeType sourceStorage = image.Type(slotTypeIndex);
        NativeType targetStorage = image.Type(targetTypeIndex);
        if ((sourceStorage.Kinds & ~targetStorage.Kinds) != 0) return false;
        if (!sourceStorage.Accepts(NativeKind.Address)) return true;
        IReadOnlyList<int>? admitted = image.Program.Types[targetTypeIndex].Layouts;
        if (admitted is null) return true;
        if (admitted.Count == 0) return false;
        IReadOnlyList<int>? declared = image.Program.Types[slotTypeIndex].Layouts;
        if (declared is { Count: > 0 } && declared.All(admitted.Contains)) return true;
        if (Definition.Storage!.Instructions[pc].HasFlag(NativeInstructionStorage.NarrowLayout)) return true;
        int current = instruction.A;
        int scanPc = pc - 1;
        for (int depth = 0; depth < 8; depth++)
        {
            int defPc = -1;
            if (scanPc >= blockStart)
            {
                for (int cursor = blockStart; cursor <= scanPc; cursor += code[cursor].Span)
                {
                    if (code[cursor].Result == current || rebound.Contains(current))
                    {
                        if (code[cursor].Result == current) defPc = cursor;
                    }
                    else if (cursor != pc && Metadata[cursor].Op == NativeOp.FrameAddress && Metadata[cursor].A == current && code[cursor].Span == 1)
                    {
                        defPc = -1;
                        break;
                    }
                }
            }
            if (defPc < 0 && defCount[current] == 1 && !rebound.Contains(current) && current >= Definition.CaptureCount + Definition.ParameterCount)
                defPc = singleDefPc[current];
            if (defPc < 0) return false;
            NativeExecutionInstruction def = code[defPc];
            if (!(def.Op is NativeOp.Copy or NativeOp.Move or NativeOp.Narrow
                || def.Op == NativeOp.Store && def.Handler == NativeInterpreter.FrameStoreHandler)) return false;
            current = def.A;
            if (!FixedCell(current, rebound)) return false;
            int currentType = Definition.Slots[current].Type;
            if (SameTypeShape(image.Program, currentType, targetTypeIndex)) return true;
            IReadOnlyList<int>? sourceLayouts = image.Program.Types[currentType].Layouts;
            if (sourceLayouts is { Count: > 0 } && sourceLayouts.All(admitted.Contains)) return true;
            scanPc = defPc - 1;
        }
        return false;
    }

    private bool FixedCell(int slot, HashSet<int> rebound) => !rebound.Contains(slot)
        && (slot >= Definition.CaptureCount || Definition.Signature.Captures[slot].Mode == NativeCaptureMode.Value)
        && !(image.Program.PreserveLocals && Index == image.Program.EntryPoint
            && (image.Program.SessionRoots?.Contains(slot) ?? Definition.Slots[slot].Role != NativeSlotRole.Temporary));

    internal NativeInstruction Diagnostic(int pc) => Metadata[pc + instructions[pc].Span - 1];
    internal void Visit(RuntimeProfile.NativeFrameCounters counters, int pc)
    {
        for (int index = pc; index < pc + instructions[pc].Span; index++)
        {
            if (index != pc) counters.Edge(index - 1, index);
            counters.Visit(index, Metadata[index].Op);
        }
    }
    internal NativeSiteProfile?[]? ObservedProfiles => Volatile.Read(ref profiles);

    private NativeSiteProfile?[] Profiles
    {
        get
        {
            if (Volatile.Read(ref profiles) is { } ready) return ready;
            NativeSiteProfile?[] created = new NativeSiteProfile?[Metadata.Length];
            return Interlocked.CompareExchange(ref profiles, created, null) ?? created;
        }
    }
    internal IReadOnlyList<bool> RawWritable
    {
        get
        {
            if (Volatile.Read(ref rawWritable) is { } ready) return ready;
            bool[] proof = PrivateWrites(image.Program, Index, instructions);
            return Interlocked.CompareExchange(ref rawWritable, proof, null) ?? proof;
        }
    }
    private static bool TryCollectExposed(NativeFunctionCode function, NativeExecutionInstruction[] lowered, HashSet<int> exposed)
    {
        for (int pc = 0; pc < lowered.Length; pc += lowered[pc].Span)
        {
            NativeExecutionInstruction instruction = lowered[pc];
            if (instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation) exposed.Add(instruction.A);
            if (function.Storage!.Instructions[pc].HasFlag(NativeInstructionStorage.FrameAlias)) return false;
        }
        return true;
    }

    private static bool[] PrivateWrites(NativeProgram program, int index, NativeExecutionInstruction[] lowered)
    {
        NativeFunctionCode function = program.Functions[index];
        bool[] result = new bool[function.Slots.Count];
        HashSet<int> exposed = [];
        if (!TryCollectExposed(function, lowered, exposed)) return result;
        for (int slot = 0; slot < result.Length; slot++)
            result[slot] = slot >= function.CaptureCount && function.Slots[slot].Role == NativeSlotRole.Temporary
                && !function.Slots[slot].Escapes && !function.Storage!.Slots[slot].HasFlag(NativeSlotStorage.Escapes)
                && !exposed.Contains(slot) && program.Types[function.Slots[slot].Type].Pointee < 0
                && !(index == program.EntryPoint && program.PreserveLocals && program.SessionRoots?.Contains(slot) == true);
        return result;
    }
    internal bool NeedsProfiling => HasObservableBinary
        && !Volatile.Read(ref unavailable)
        && Volatile.Read(ref compiled) is null
        && options.Policy is NativeJitPolicy.Auto or NativeJitPolicy.Force
        && (Volatile.Read(ref calls) >= Math.Max(1, options.CallThreshold / 2)
            || Volatile.Read(ref backedges) >= Math.Max(1, options.BackedgeThreshold / 2));
    internal void Observe(int pc, NativeSiteShape shape)
    {
        NativeSiteProfile? profile = Volatile.Read(ref Profiles[pc]);
        if (profile is null)
        {
            NativeSiteProfile created = new();
            profile = Interlocked.CompareExchange(ref Profiles[pc], created, null) ?? created;
        }
        profile.Observe(shape);
    }
    internal void Enter() => Interlocked.Increment(ref calls);
    internal void Backedge() => Interlocked.Increment(ref backedges);

    internal NativeCompiledFunction? TryCompile(NativeTierCounters counters, RuntimeProfile.NativeFrameCounters profile)
    {
        if (options.Policy is NativeJitPolicy.Off or NativeJitPolicy.Linked || unavailable) return null;
        NativeCompiledFunction? ready = Volatile.Read(ref compiled);
        if (ready is not null) return ready;
        int threshold = invalidations == 0 ? options.CallThreshold : options.RecompileThreshold;
        if (calls < threshold && backedges < options.BackedgeThreshold) return null;
        lock (gate)
        {
            if (compiled is not null || unavailable) return compiled;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            NativeCompiledFunction? code = NativeCompiledFunction.Compile(image, this, options.Policy);
            if (code is null) { unavailable = true; counters.Unavailable(); return null; }
            compiled = code;
            TimeSpan duration = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            counters.Compiled(code.Bytes, duration, invalidations != 0, code.Statistics);
            profile.TierCompilation(code.Bytes, duration.Ticks * 100, invalidations != 0);
            profile.TierCodegen(code.Statistics);
            return code;
        }
    }

    internal void Invalidate(NativeCompiledFunction version)
    {
        lock (gate)
        {
            if (!ReferenceEquals(compiled, version)) return;
            compiled = null;
            calls = 0;
            backedges = 0;
            invalidations++;
            version.Dispose();
        }
    }

    public void Dispose() { lock (gate) { unavailable = true; compiled?.Dispose(); compiled = null; } }
}

internal sealed class NativeTierCounters
{
    private long readinessExits;
    internal void ReadinessExit() => Interlocked.Increment(ref readinessExits);
    private long compilations, compilationNanoseconds, codeBytes, entries, operations, directOperations;
    private long guardMisses, deoptimizations, suspensions, recompilations, cancellationPolls, unavailable;
    private long linkedEntries, linkedOperations;
    private long rawOperations, bridgeDirectOperations, bridgeFallbackOperations;
    private long registerRegions, registerValues, registerSpills, eliminatedStores;
    private long selectionNs, allocationNs, emissionNs, publicationNs;
    internal void LinkedEntry() => Interlocked.Increment(ref linkedEntries);
    internal void LinkedOperation() => Interlocked.Increment(ref linkedOperations);
    internal void Compiled(int bytes, TimeSpan duration, bool recompiled, NativeCodegenStatistics statistics)
    {
        Interlocked.Increment(ref compilations);
        Interlocked.Add(ref compilationNanoseconds, duration.Ticks * 100);
        Interlocked.Add(ref codeBytes, bytes);
        Interlocked.Add(ref registerRegions, statistics.Regions);
        Interlocked.Add(ref registerValues, statistics.Values);
        Interlocked.Add(ref registerSpills, statistics.Spills);
        Interlocked.Add(ref eliminatedStores, statistics.EliminatedStores);
        Interlocked.Add(ref selectionNs, statistics.SelectionNanoseconds);
        Interlocked.Add(ref allocationNs, statistics.AllocationNanoseconds);
        Interlocked.Add(ref emissionNs, statistics.EmissionNanoseconds);
        Interlocked.Add(ref publicationNs, statistics.PublicationNanoseconds);
        if (recompiled) Interlocked.Increment(ref recompilations);
    }
    internal void Entered() => Interlocked.Increment(ref entries);
    internal void Operation(bool direct, int count = 1)
    {
        Interlocked.Add(ref operations, count);
        if (direct) Interlocked.Add(ref directOperations, count);
    }
    internal void DirectBatch(long count)
    {
        Interlocked.Add(ref operations, count);
        Interlocked.Add(ref directOperations, count);
        Interlocked.Add(ref rawOperations, count);
    }
    internal void BridgeBatch(long count, long polls, long bridgeDirect = 0, long bridgeFallback = 0)
    {
        Interlocked.Add(ref operations, count);
        Interlocked.Add(ref cancellationPolls, polls);
        if (bridgeDirect != 0) Interlocked.Add(ref bridgeDirectOperations, bridgeDirect);
        if (bridgeFallback != 0) Interlocked.Add(ref bridgeFallbackOperations, bridgeFallback);
    }
    internal void GuardMiss() { Interlocked.Increment(ref guardMisses); Interlocked.Increment(ref deoptimizations); }
    internal void Suspended() => Interlocked.Increment(ref suspensions);
    internal void Poll() => Interlocked.Increment(ref cancellationPolls);
    internal void Unavailable() => Interlocked.Increment(ref unavailable);
    internal NativeTierStatistics Snapshot() => new(Volatile.Read(ref compilations), Volatile.Read(ref compilationNanoseconds),
        Volatile.Read(ref codeBytes), Volatile.Read(ref entries), Volatile.Read(ref operations), Volatile.Read(ref directOperations),
        Volatile.Read(ref guardMisses), Volatile.Read(ref deoptimizations), Volatile.Read(ref suspensions),
        Volatile.Read(ref recompilations), Volatile.Read(ref cancellationPolls), Volatile.Read(ref unavailable))
    {
        ReadinessExits = Volatile.Read(ref readinessExits), LinkedEntries = Volatile.Read(ref linkedEntries), LinkedOperations = Volatile.Read(ref linkedOperations), RawOperations = Volatile.Read(ref rawOperations),
        BridgeDirectOperations = Volatile.Read(ref bridgeDirectOperations), BridgeFallbackOperations = Volatile.Read(ref bridgeFallbackOperations),
        RegisterRegions = Volatile.Read(ref registerRegions), RegisterValues = Volatile.Read(ref registerValues),
        RegisterSpills = Volatile.Read(ref registerSpills), EliminatedFrameStores = Volatile.Read(ref eliminatedStores),
        SelectionNanoseconds = Volatile.Read(ref selectionNs), RegisterAllocationNanoseconds = Volatile.Read(ref allocationNs),
        EmissionNanoseconds = Volatile.Read(ref emissionNs), PublicationNanoseconds = Volatile.Read(ref publicationNs)
    };
}
