using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeExecutionInstruction(NativeInstruction source, int span = 1, nint handler = 0)
{
    internal readonly NativeOp Op = source.Op;
    internal readonly int Result = source.Result;
    internal readonly int A = source.A;
    internal readonly int B = source.B;
    internal readonly int C = source.C;
    internal readonly int Span = span;
    internal readonly long Immediate = source.Immediate;
    internal readonly nint Handler = handler == 0 ? NativeInterpreter.ExecutionHandlerAddress(source.Op) : handler;
}

internal sealed class NativeExecutionImage(NativeImage image, NativeTierOptions options) : IDisposable
{
    private readonly ConcurrentDictionary<int, NativeExecutionFunction> functions = [];
    internal NativeExecutionFunction Function(int index) => functions.GetOrAdd(index,
        static (index, state) => new(state.image, index, state.options), (image, options));

    public void Dispose()
    {
        foreach (NativeExecutionFunction function in functions.Values) function.Dispose();
        functions.Clear();
    }
}

internal sealed class NativeExecutionFunction : IDisposable
{
    private readonly object gate = new();
    private readonly NativeImage image;
    private readonly NativeTierOptions options;
    private NativeEscapeAnalysis? provenance;
    private int calls;
    private int backedges;
    private int invalidations;
    private bool unavailable;
    private NativeCompiledFunction? compiled;
    private readonly NativeExecutionInstruction[] instructions;
    internal NativeExecutionOperand[] Operands { get; }
    private NativeSiteProfile?[]? profiles;
    private bool[]? rawWritable;
    internal NativeExecutionFunction(NativeImage image, int index, NativeTierOptions options)
    {
        this.image = image;
        this.options = options;
        Index = index;
        Definition = image.Program.Functions[index];
        Metadata = Definition.Instructions as NativeInstruction[] ?? Definition.Instructions.ToArray();
        Operands = Metadata.Select((instruction, pc) => NativeExecutionOperand.Link(image, instruction,
            instruction.Op is NativeOp.AddressOffset or NativeOp.ProjectAddress
                ? Provenance.Addresses.ProvenLayoutsAt(pc, instruction.A) : null)).ToArray();
        instructions = Lower();
    }

    internal int Index { get; }
    internal NativeFunctionCode Definition { get; }
    internal NativeInstruction[] Metadata { get; }
    internal NativeExecutionInstruction[] Instructions => instructions;
    private NativeEscapeAnalysis Provenance => provenance ??= new(image.Program, Index, image.Provenance);
    internal bool HasKind(int slot, NativeKind kind) => image.Program.Types[Definition.Slots[slot].Type] is { Pointee: < 0 } type
        && type.Kind == (kind == NativeKind.Number ? TypeKind.Number : TypeKind.Bool);
    private NativeExecutionInstruction[] Lower()
    {
        NativeExecutionInstruction[] code = Metadata.Select(instruction => new NativeExecutionInstruction(instruction)).ToArray();
        bool[] entry = new bool[code.Length];
        NativeControlFlow flow = new(Metadata);
        for (int pc = 0; pc < code.Length; pc++)
            foreach (int next in flow.Successors[pc]) if (next != pc + 1) entry[next] = true;
        for (int pc = 0; pc + 1 < code.Length; pc++)
        {
            NativeInstruction address = Metadata[pc], access = Metadata[pc + 1];
            if (address.Op != NativeOp.FrameAddress || entry[pc + 1] || !RawWritable[address.Result]
                || access.A != address.Result || !DeadAfter(address.Result, pc + 1)) continue;
            if (access.Op == NativeOp.Load)
                code[pc] = new(access with { Op = NativeOp.Copy, A = address.A }, 2);
            else if (access.Op == NativeOp.Store && access.B != address.Result)
                code[pc] = new(access with { Result = address.A, A = access.B }, 2, NativeInterpreter.FrameStoreHandler);
            else continue;
            Operands[pc] = Operands[pc + 1];
        }
        HashSet<int> rebound = Metadata.Where(instruction => instruction.Op == NativeOp.BindLocation).Select(instruction => instruction.A).ToHashSet();
        for (int pc = 0; pc < code.Length; pc++)
        {
            NativeExecutionInstruction instruction = code[pc];
            if (instruction.Op == NativeOp.Binary && instruction.Immediate is >= 0 and <= (long)NativeBinary.GreaterEqual
                && HasKind(instruction.A, NativeKind.Number) && HasKind(instruction.B, NativeKind.Number)
                && HasKind(instruction.Result, instruction.Immediate >= (long)NativeBinary.Equal ? NativeKind.Bool : NativeKind.Number)
                && FixedCell(instruction.A, rebound) && FixedCell(instruction.B, rebound) && FixedCell(instruction.Result, rebound))
                code[pc] = new(Metadata[pc], handler: NativeInterpreter.ExecutionHandlerAddress(NativeOp.Binary, number: true));
            if (code[pc].Op == NativeOp.Copy)
            {
                Operands[pc] = Operands[pc] with { Type = null };
                if (!FixedCell(code[pc].A, rebound) || !FixedCell(code[pc].Result, rebound)) continue;
                NativeType type = image.Type(Definition.Slots[code[pc].A].Type);
                if (type.Equals(image.Type(Definition.Slots[code[pc].Result].Type))) Operands[pc] = Operands[pc] with { Type = type };
            }
        }
        return code;
    }

    private bool FixedCell(int slot, HashSet<int> rebound) => !rebound.Contains(slot)
        && (slot >= Definition.CaptureCount || Definition.Signature.Captures[slot].Mode == NativeCaptureMode.Value)
        && !(image.Program.PreserveLocals && Index == image.Program.EntryPoint
            && (image.Program.SessionRoots?.Contains(slot) ?? Definition.Slots[slot].Role != NativeSlotRole.Temporary));

    private bool DeadAfter(int slot, int pc)
    {
        for (int next = pc + 1; next < Metadata.Length; next++)
        {
            NativeInstruction instruction = Metadata[next];
            if (instruction.Op == NativeOp.Drop && instruction.A == slot) return true;
            if (NativeInstructionFacts.ValueReads(instruction).Contains(slot)) return false;
            if (NativeInstructionFacts.Definitions(instruction).Contains(slot)) return true;
            if (instruction.Op is NativeOp.Branch or NativeOp.Jump or NativeOp.NextIterator) return false;
            if (instruction.Op is NativeOp.Return or NativeOp.Unreachable) return true;
        }
        return true;
    }

    internal NativeInstruction Diagnostic(int pc) => Metadata[pc + instructions[pc].Span - 1];
    internal void Visit(RuntimeProfile.NativeFrameCounters counters, int pc)
    {
        for (int index = pc; index < pc + instructions[pc].Span; index++)
        {
            if (index != pc) counters.Edge(index - 1, index);
            counters.Visit(index, Metadata[index].Op);
        }
    }
    internal NativeSiteProfile?[] Profiles
    {
        get
        {
            if (Volatile.Read(ref profiles) is { } ready) return ready;
            NativeSiteProfile?[] created = new NativeSiteProfile?[Metadata.Length];
            return Interlocked.CompareExchange(ref profiles, created, null) ?? created;
        }
    }
    internal bool[] RawWritable
    {
        get
        {
            if (Volatile.Read(ref rawWritable) is { } ready) return ready;
            bool[] proof = PrivateWrites(image.Program, Index, Provenance);
            return Interlocked.CompareExchange(ref rawWritable, proof, null) ?? proof;
        }
    }
    private static bool[] PrivateWrites(NativeProgram program, int index, NativeEscapeAnalysis escape)
    {
        NativeFunctionCode function = program.Functions[index];
        bool[] result = new bool[function.Slots.Count];
        if (escape.EscapingLocations.Count != 0) return result;
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            if (instruction.Op == NativeOp.EnvironmentAddress
                || instruction.Op == NativeOp.BindLocation && escape.Addresses.At(pc, instruction.B).MayAliasCurrentFrame) return result;
            if (instruction.Op is NativeOp.AddressOffset or NativeOp.ProjectAddress or NativeOp.IndexAddress
                or NativeOp.CopyMemory or NativeOp.ClearMemory or NativeOp.RangeFromMemory)
            {
                if (escape.Addresses.At(pc, instruction.A).MayAliasCurrentFrame) return result;
                if (instruction.Op == NativeOp.CopyMemory && escape.Addresses.At(pc, instruction.B).MayAliasCurrentFrame) return result;
            }
        }
        HashSet<int> exposed = function.Instructions.Where(instruction => instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation)
            .Select(instruction => instruction.A).ToHashSet();
        for (int slot = 0; slot < result.Length; slot++)
            result[slot] = function.Slots[slot].Role == NativeSlotRole.Temporary && !exposed.Contains(slot);
        return result;
    }
    internal bool NeedsProfiling => Volatile.Read(ref calls) >= Math.Max(1, options.CallThreshold / 2)
        || Volatile.Read(ref backedges) >= Math.Max(1, options.BackedgeThreshold / 2);
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
            counters.Compiled(code.Bytes, duration, invalidations != 0);
            profile.TierCompilation(code.Bytes, duration.Ticks * 100, invalidations != 0);
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
    private long compilations, compilationNanoseconds, codeBytes, entries, operations, directOperations;
    private long guardMisses, deoptimizations, suspensions, recompilations, cancellationPolls, unavailable;
    private long linkedEntries, linkedOperations;
    private long rawOperations;
    internal void LinkedEntry() => Interlocked.Increment(ref linkedEntries);
    internal void LinkedOperation() => Interlocked.Increment(ref linkedOperations);
    internal void Compiled(int bytes, TimeSpan duration, bool recompiled)
    {
        Interlocked.Increment(ref compilations);
        Interlocked.Add(ref compilationNanoseconds, duration.Ticks * 100);
        Interlocked.Add(ref codeBytes, bytes);
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
    internal void BridgeBatch(long count, long polls)
    {
        Interlocked.Add(ref operations, count);
        Interlocked.Add(ref cancellationPolls, polls);
    }
    internal void GuardMiss() { Interlocked.Increment(ref guardMisses); Interlocked.Increment(ref deoptimizations); }
    internal void Suspended() => Interlocked.Increment(ref suspensions);
    internal void Poll() => Interlocked.Increment(ref cancellationPolls);
    internal void Unavailable() => Interlocked.Increment(ref unavailable);
    internal NativeTierStatistics Snapshot() => new(Volatile.Read(ref compilations), Volatile.Read(ref compilationNanoseconds),
        Volatile.Read(ref codeBytes), Volatile.Read(ref entries), Volatile.Read(ref operations), Volatile.Read(ref directOperations),
        Volatile.Read(ref guardMisses), Volatile.Read(ref deoptimizations), Volatile.Read(ref suspensions),
        Volatile.Read(ref recompilations), Volatile.Read(ref cancellationPolls), Volatile.Read(ref unavailable))
    { LinkedEntries = Volatile.Read(ref linkedEntries), LinkedOperations = Volatile.Read(ref linkedOperations), RawOperations = Volatile.Read(ref rawOperations) };
}
