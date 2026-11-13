using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Msync.Core.Native;

namespace Msync.Runtime;

public sealed partial class RuntimeProfile
{
    private readonly ConcurrentDictionary<NativeProgram, NativeProfileImage> nativeImages = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<NativeProgram, ImageIdentity> nativeIdentities = new();

    internal NativeFrameCounters EnterNative(NativeProgram program, int function)
    {
        NativeProfileImage image = nativeImages.GetOrAdd(program, static (code, owner) => new(code,
            owner.nativeIdentities.GetValue(code, _ => new(Interlocked.Increment(ref owner.nextImage))).Id,
            owner.InstructionSites), this);
        NativeFunctionCounters counters = image.Functions[function];
        Interlocked.Increment(ref counters.Calls);
        return new(image, function, counters);
    }

    private void CollectNative(List<RuntimeProfileSample> samples)
    {
        foreach (NativeProfileImage image in nativeImages.Values.OrderBy(value => value.Identity))
        {
            for (int function = 0; function < image.Functions.Length; function++)
                CollectNativeFunction(image, function, samples);
            foreach (var (name, counter) in image.Natives.OrderBy(value => value.Key, StringComparer.Ordinal))
                samples.Add(new("vm.native", new(image.Attributes()) { ["native.name"] = name },
                    new() { ["calls"] = (ulong)counter.Calls }));
        }
        nativeImages.Clear();
    }

    private static void CollectNativeFunction(NativeProfileImage image, int function, List<RuntimeProfileSample> samples)
    {
        NativeFunctionCounters counters = image.Functions[function];
        if (counters.Calls == 0) return;
        Dictionary<string, string> attributes = image.FunctionAttributes(function);
        ulong total = 0;
        for (int operation = 0; operation < counters.Opcodes.Length; operation++)
        {
            long count = counters.Opcodes[operation];
            if (count == 0) continue;
            total += (ulong)count;
            samples.Add(new("vm.opcode", new(attributes) { ["opcode"] = ((NativeOp)operation).ToString() },
                new() { ["hits"] = (ulong)count }));
        }
        samples.Add(new("vm.function", attributes, new()
        {
            ["calls"] = (ulong)counters.Calls,
            ["instructions"] = total,
            ["dispatch_pauses"] = (ulong)counters.Pauses,
            ["dispatch_continuations"] = (ulong)counters.Continuations,
            ["logical_allocations"] = (ulong)counters.Allocations,
            ["logical_elements"] = (ulong)counters.Elements,
            ["logical_storage_bytes"] = (ulong)counters.StorageBytes,
            ["frame_placed_allocations"] = (ulong)counters.FrameAllocations,
            ["copies"] = (ulong)counters.Copies,
            ["moves"] = (ulong)counters.Moves,
            ["drops"] = (ulong)counters.Drops,
            ["jit_compilations"] = (ulong)counters.TierCompilations,
            ["jit_compile_ns"] = (ulong)counters.TierCompileNs,
            ["jit_code_bytes"] = (ulong)counters.TierCodeBytes,
            ["jit_entries"] = (ulong)counters.TierEntries,
            ["jit_operations"] = (ulong)counters.TierOperations,
            ["jit_direct_operations"] = (ulong)counters.TierDirectOperations,
            ["jit_guard_misses"] = (ulong)counters.TierGuardMisses,
            ["jit_deoptimizations"] = (ulong)counters.TierDeoptimizations,
            ["jit_suspensions"] = (ulong)counters.TierSuspensions,
            ["jit_recompilations"] = (ulong)counters.TierRecompilations,
            ["linked_entries"] = (ulong)counters.LinkedEntries,
            ["linked_operations"] = (ulong)counters.LinkedOperations,
            ["jit_raw_operations"] = (ulong)counters.TierRawOperations
        }));
        if (counters.Sites is { } sites)
            for (int pc = 0; pc < sites.Length; pc++)
                if (sites[pc] != 0) CollectNativeSite(image, function, pc, sites[pc], attributes, samples);
        if (counters.Edges is { } edges)
            foreach (var (edge, counter) in edges.OrderBy(value => value.Key.From).ThenBy(value => value.Key.To))
                samples.Add(new("vm.edge", new(attributes)
                {
                    ["instruction.pc"] = Number(edge.From),
                    ["target.pc"] = Number(edge.To)
                }, new() { ["hits"] = (ulong)counter.Calls }));
        if (counters.Targets is { } targets)
            foreach (var (target, counter) in targets.OrderBy(value => value.Key.Pc).ThenBy(value => value.Key.Function))
                samples.Add(new("vm.call", new(attributes)
                {
                    ["instruction.pc"] = Number(target.Pc),
                    ["target.image.id"] = Number(image.Identity),
                    ["target.function.id"] = Number(target.Function),
                    ["target.function.name"] = image.Program.Functions[target.Function].Name
                }, new() { ["hits"] = (ulong)counter.Calls }));
    }

    private static void CollectNativeSite(NativeProfileImage image, int function, int pc, long hits,
        Dictionary<string, string> owner, List<RuntimeProfileSample> samples)
    {
        NativeInstruction instruction = image.Program.Functions[function].Instructions[pc];
        Dictionary<string, string> attributes = new(owner)
        {
            ["instruction.pc"] = Number(pc),
            ["opcode"] = instruction.Op.ToString()
        };
        if (instruction.Source is { } source)
        {
            attributes["source.file"] = source.Source;
            attributes["source.offset"] = Number(source.Start);
            attributes["source.length"] = Number(source.Length);
        }
        if (instruction.Op == NativeOp.CallNative && instruction.Symbol is { } symbol)
            attributes["native.name"] = symbol;
        samples.Add(new("vm.instruction", attributes, new() { ["hits"] = (ulong)hits }));
    }

    internal sealed class NativeProfileImage
    {
        internal NativeProfileImage(NativeProgram program, int identity, bool sites)
        {
            Program = program;
            Identity = identity;
            Functions = program.Functions.Select(function => new NativeFunctionCounters(function.Instructions.Count, sites)).ToArray();
        }
        internal NativeProgram Program { get; }
        internal int Identity { get; }
        internal NativeFunctionCounters[] Functions { get; }
        internal ConcurrentDictionary<string, NativeCounter> Natives { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Attributes() => new(StringComparer.Ordinal)
        {
            ["image.id"] = Number(Identity),
            ["image.stage"] = Program.Stage.ToString(),
            ["artifact.stage"] = Program.Stage.ToString(),
            ["optimization.level"] = Number(Program.OptimizationLevel),
            ["representation"] = NativeProgram.Representation
        };
        internal Dictionary<string, string> FunctionAttributes(int function)
        {
            Dictionary<string, string> attributes = Attributes();
            attributes["function.id"] = Number(function);
            attributes["function.name"] = Program.Functions[function].Name;
            return attributes;
        }
    }

    internal sealed class NativeCounter { internal long Calls; }

    internal sealed class NativeFunctionCounters
    {
        internal NativeFunctionCounters(int instructions, bool sites)
        {
            if (!sites) return;
            Sites = new long[instructions];
            Edges = new();
            Targets = new();
        }
        internal readonly long[] Opcodes = new long[Enum.GetValues<NativeOp>().Length];
        internal readonly long[]? Sites;
        internal readonly ConcurrentDictionary<(int From, int To), NativeCounter>? Edges;
        internal readonly ConcurrentDictionary<(int Pc, int Function), NativeCounter>? Targets;
        internal long Calls;
        internal long Pauses;
        internal long Continuations;
        internal long Allocations;
        internal long Elements;
        internal long StorageBytes;
        internal long FrameAllocations;
        internal long Copies;
        internal long Moves;
        internal long Drops;
        internal long TierCompilations, TierCompileNs, TierCodeBytes, TierEntries, TierOperations;
        internal long TierDirectOperations, TierGuardMisses, TierDeoptimizations, TierSuspensions, TierRecompilations;
        internal long LinkedEntries, LinkedOperations;
        internal long TierRawOperations;
    }

    internal readonly struct NativeFrameCounters(NativeProfileImage? image, int function, NativeFunctionCounters? counters)
    {
        internal bool Observed => counters is not null;
        internal void LinkedEntry()
        {
            if (counters is not null) Interlocked.Increment(ref counters.LinkedEntries);
        }
        internal void LinkedOperation()
        {
            if (counters is not null) Interlocked.Increment(ref counters.LinkedOperations);
        }
        internal void TierCompilation(int bytes, long nanoseconds, bool recompiled)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.TierCompilations);
            Interlocked.Add(ref counters.TierCompileNs, nanoseconds);
            Interlocked.Add(ref counters.TierCodeBytes, bytes);
            if (recompiled) Interlocked.Increment(ref counters.TierRecompilations);
        }
        internal void TierEntry()
        {
            if (counters is not null) Interlocked.Increment(ref counters.TierEntries);
        }
        internal void TierOperation(bool direct, int count = 1)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.TierOperations, count);
            if (direct) Interlocked.Add(ref counters.TierDirectOperations, count);
        }
        internal void TierDirectBatch(long count)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.TierOperations, count);
            Interlocked.Add(ref counters.TierDirectOperations, count);
            Interlocked.Add(ref counters.TierRawOperations, count);
        }
        internal void TierBridgeBatch(long count)
        {
            if (counters is not null) Interlocked.Add(ref counters.TierOperations, count);
        }
        internal void TierGuardMiss()
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.TierGuardMisses);
            Interlocked.Increment(ref counters.TierDeoptimizations);
        }
        internal void TierSuspension()
        {
            if (counters is not null) Interlocked.Increment(ref counters.TierSuspensions);
        }
        internal void Visit(int pc, NativeOp operation)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.Opcodes[(int)operation]);
            if (counters.Sites is { } sites) Interlocked.Increment(ref sites[pc]);
            if (operation == NativeOp.Copy) Interlocked.Increment(ref counters.Copies);
            else if (operation == NativeOp.Move) Interlocked.Increment(ref counters.Moves);
            else if (operation == NativeOp.Drop) Interlocked.Increment(ref counters.Drops);
        }
        internal void BatchVisit(int pc, NativeOp operation, long count)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.Opcodes[(int)operation], count);
            if (counters.Sites is { } sites) Interlocked.Add(ref sites[pc], count);
            if (operation == NativeOp.Copy) Interlocked.Add(ref counters.Copies, count);
            else if (operation == NativeOp.Move) Interlocked.Add(ref counters.Moves, count);
            else if (operation == NativeOp.Drop) Interlocked.Add(ref counters.Drops, count);
        }
        internal void BatchEdge(int from, int to, long count)
        {
            if (counters?.Edges is not { } edges) return;
            Interlocked.Add(ref edges.GetOrAdd((from, to), static _ => new()).Calls, count);
        }
        internal void Edge(int from, int to)
        {
            if (counters?.Edges is not { } edges) return;
            Interlocked.Increment(ref edges.GetOrAdd((from, to), static _ => new()).Calls);
        }
        internal void Call(int pc, int target)
        {
            if (counters?.Targets is not { } targets) return;
            Interlocked.Increment(ref targets.GetOrAdd((pc, target), static _ => new()).Calls);
        }
        internal void Native(string name)
        {
            if (image is null) return;
            Interlocked.Increment(ref image.Natives.GetOrAdd(name, static _ => new()).Calls);
        }
        internal void Pause()
        {
            if (counters is not null) Interlocked.Increment(ref counters.Pauses);
        }
        internal void Continue()
        {
            if (counters is not null) Interlocked.Increment(ref counters.Continuations);
        }
        internal void Allocation(NativeAllocation allocation, bool framePlaced = false)
            => Allocation(allocation.Layout, allocation.ElementCount, framePlaced);

        internal void Allocation(NativeLayout layout, int elementCount = 1, bool framePlaced = false)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.Allocations);
            Interlocked.Add(ref counters.Elements, elementCount);
            long bytes = checked((long)layout.RawSize * elementCount
                + (long)layout.ReferenceCount * elementCount * IntPtr.Size);
            Interlocked.Add(ref counters.StorageBytes, bytes);
            if (framePlaced) Interlocked.Increment(ref counters.FrameAllocations);
        }
        internal int Function => function;
    }
}
