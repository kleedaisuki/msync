using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed record RuntimeProfileSample(string Name, Dictionary<string, string> Attributes, Dictionary<string, ulong> Metrics);

public sealed class RuntimeProfile(bool instructionSites = false)
{
    private readonly ConcurrentDictionary<Module, ImageCounts> images = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<Module, ImageIdentity> identities = new();
    private int nextImage;

    internal Frame Enter(Module image, int functionId, RuntimeFrameTrace? trace, bool borrowed, bool split, int rawArguments)
    {
        ImageCounts counts = Image(image);
        FunctionCounts function = counts.GetFunction(functionId);
        Interlocked.Increment(ref function.Calls);
        if (borrowed) Interlocked.Increment(ref function.Borrowed);
        if (split) Interlocked.Increment(ref function.Split);
        if (rawArguments != 0) Interlocked.Add(ref function.RawArguments, rawArguments);
        return new(this, function, trace);
    }

    internal Frame DirectSourceFrame(Module image, int target) => new(this, Image(image).GetFunction(target), null);
    internal bool ObservesInstructionSites => InstructionSites;

    internal void NativeWorker(Module image, int target,
        NativeWorkerPlan plan,
        ReadOnlySpan<ulong> operations, bool[]? physicalOperations, long nanoseconds, int yields)
    {
        ImageCounts counts = Image(image);
        FunctionCounts worker = counts.GetFunction(target);
        Interlocked.Increment(ref worker.NativeWorkerEntries);
        Interlocked.Increment(ref worker.JitEntries);
        Interlocked.Add(ref worker.JitExecuteNs, nanoseconds);
        Interlocked.Add(ref worker.JitYields, yields);
        var logical = plan.LogicalInstructions;
        if (logical.IsDefault) Interlocked.Increment(ref worker.NativeWorkerUnmappedEntries);
        long total = 0, stores = 0;
        for (int i = 0; i < operations.Length; i++)
        {
            long hits = (long)operations[i];
            if (hits == 0) continue;
            if (physicalOperations is null || physicalOperations[i]) total += hits;
            if (plan.Instructions[i].Op is NativeWorkerOp.StoreNumber or NativeWorkerOp.StoreIndex) stores += hits;
            if (logical.IsDefault) continue;
            foreach (var visit in logical[i])
            {
                var instruction = image.Functions[visit.Function].Instructions[visit.Pc];
                var function = counts.GetFunction(visit.Function);
                Interlocked.Add(ref function.Opcodes[(byte)instruction.Op], hits);
                if (instruction is { Op: OpCode.NativeCall, Text: { } name })
                    Interlocked.Add(ref counts.Natives.GetOrAdd(name, static _ => new()).Calls, hits);
            }
        }
        Interlocked.Add(ref worker.NativeWorkerOperations, total);
        if (stores != 0) Interlocked.Add(ref worker.NativeWorkerStores, stores);
    }

    internal void NativeWorkerPrefixExit(Module image, int target) =>
        Interlocked.Increment(ref Image(image).GetFunction(target).NativeWorkerPrefixExits);

    internal void NativeWorkerGuardMiss(Module image, int target) =>
        Interlocked.Increment(ref Image(image).GetFunction(target).NativeWorkerGuardMisses);

    internal void Predicate(Module image, int target, ReadOnlySpan<byte> path, bool taken)
    {
        var function = Image(image).GetFunction(target);
        if (!taken)
        {
            Interlocked.Increment(ref function.PredicateMisses);
            return;
        }
        Interlocked.Increment(ref function.PredicateCalls);
        Frame logical = new(this, function, null);
        var code = image.Functions[target].Instructions;
        foreach (int pc in path) logical.Visit(pc, code[pc].Op);
    }

    internal void LiteralScope(Module image, int functionId)
    {
        var function = Image(image).GetFunction(functionId);
        Interlocked.Increment(ref function.LiteralFactories);
        var code = image.Functions[functionId].Instructions;
        Frame logical = new(this, function, null);
        for (int pc = 0; pc < code.Count; pc++)
            logical.Visit(pc, code[pc].Op);
    }

    internal void FoldedLiteralRow(Module image, int functionId)
    {
        var function = Image(image).GetFunction(functionId);
        Interlocked.Increment(ref function.FoldedLiteralRows);
        foreach (var instruction in image.Functions[functionId].Instructions)
            Interlocked.Increment(ref function.Opcodes[(byte)instruction.Op]);
    }

    internal void Native(Module image, string name, bool fast = false)
    {
        NativeCounts counts = Image(image).Natives.GetOrAdd(name, static _ => new());
        Interlocked.Increment(ref counts.Calls);
        if (fast) Interlocked.Increment(ref counts.Fast);
    }

    private ImageCounts Image(Module image) => images.GetOrAdd(image, static (module, profile) =>
        new(module, profile.ImageId(module), profile.InstructionSites), this);

    internal int ImageId(Module image) =>
        identities.GetValue(image, _ => new(Interlocked.Increment(ref nextImage))).Id;

    private bool InstructionSites { get; } = instructionSites;

    // Drain after guest execution joins. Weak image IDs survive across drains
    // without retaining replaced session images.
    public IReadOnlyList<RuntimeProfileSample> Drain()
    {
        List<RuntimeProfileSample> samples = [];
        foreach (ImageCounts image in images.Values.OrderBy(image => image.Id))
        {
            for (int functionId = 0; functionId < image.Functions.Length; functionId++)
                if (image.Functions[functionId] is { } function) CollectFunction(image, functionId, function, samples);
            foreach (var (name, count) in image.Natives.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var attributes = image.Attributes();
                attributes["native.name"] = name;
                samples.Add(new("vm.native", attributes, new()
                {
                    ["calls"] = (ulong)count.Calls,
                    ["fast_calls"] = (ulong)count.Fast
                }));
            }
        }
        images.Clear();
        return samples;
    }

    private static void CollectFunction(ImageCounts image, int functionId, FunctionCounts function, List<RuntimeProfileSample> samples)
    {
        var attributes = image.FunctionAttributes(functionId);
        ulong total = 0;
        for (int index = 0; index < function.Opcodes.Length; index++)
        {
            long count = function.Opcodes[index];
            if (count == 0) continue;
            total += (ulong)count;
            samples.Add(new("vm.opcode", new(attributes) { ["opcode"] = ((OpCode)index).ToString() },
                new() { ["hits"] = (ulong)count }));
        }
        samples.Add(new("vm.function", attributes, new()
        {
            ["calls"] = (ulong)function.Calls,
            ["dispatch_pauses"] = (ulong)function.DispatchPauses,
            ["dispatch_continuations"] = (ulong)function.DispatchContinuations,
            ["direct_source_calls"] = (ulong)function.DirectSourceCalls,
            ["guarded_scope_crossings"] = (ulong)function.GuardedScopeCrossings,
            ["guarded_scope_crossing_misses"] = (ulong)function.GuardedScopeCrossingMisses,
            ["guarded_scope_regions"] = (ulong)function.GuardedScopeRegions,
            ["guarded_scope_misses"] = (ulong)function.GuardedScopeMisses,
            ["predicate_calls"] = (ulong)function.PredicateCalls,
            ["predicate_guard_misses"] = (ulong)function.PredicateMisses,
            ["instructions"] = total,
            ["number_boxes"] = (ulong)function.Boxes,
            ["raw_scope_storage_loads"] = (ulong)function.RawScopeStorageLoads,
            ["static_scope_publications"] = (ulong)function.StaticScopePublications,
            ["static_scope_materializations"] = (ulong)function.StaticScopeMaterializations,
            ["raw_local_writes"] = (ulong)function.RawLocalWrites,
            ["number_sidecar_arrays"] = (ulong)function.NumberSidecarArrays,
            ["number_sidecar_slots"] = (ulong)function.NumberSidecarSlots,
            ["managed_word_edges"] = (ulong)function.ManagedWordEdges,
            ["managed_word_local_reads"] = (ulong)function.ManagedWordLocalReads,
            ["managed_word_local_writes"] = (ulong)function.ManagedWordLocalWrites,
            ["managed_word_index_operands"] = (ulong)function.ManagedWordIndexOperands,
            ["numeric_local_commits"] = (ulong)function.NumericLocalCommits,
            ["numeric_loop_entries"] = (ulong)function.NumericLoopEntries,
            ["numeric_loop_operations"] = (ulong)function.NumericOperations,
            ["jit_entries"] = (ulong)function.JitEntries,
            ["jit_tail_entries"] = (ulong)function.JitTailEntries,
            ["jit_tail_rejections"] = (ulong)function.JitTailRejections,
            ["native_worker_entries"] = (ulong)function.NativeWorkerEntries,
            ["native_worker_operations"] = (ulong)function.NativeWorkerOperations,
            ["native_worker_stores"] = (ulong)function.NativeWorkerStores,
            ["native_worker_prefix_exits"] = (ulong)function.NativeWorkerPrefixExits,
            ["native_worker_guard_misses"] = (ulong)function.NativeWorkerGuardMisses,
            ["native_worker_unmapped_entries"] = (ulong)function.NativeWorkerUnmappedEntries,
            ["literal_factories"] = (ulong)function.LiteralFactories,
            ["folded_literal_rows"] = (ulong)function.FoldedLiteralRows,
            ["number_folds"] = (ulong)function.NumberFolds,
            ["number_fold_rows"] = (ulong)function.NumberFoldRows,
            ["number_fold_native_chunks"] = (ulong)function.NumberFoldNativeChunks,
            ["number_fold_misses"] = (ulong)function.NumberFoldMisses,
            ["jit_compilations"] = (ulong)function.JitCompilations,
            ["jit_yields"] = (ulong)function.JitYields,
            ["jit_compile_ns"] = (ulong)function.JitCompileNs,
            ["jit_execute_ns"] = (ulong)function.JitExecuteNs,
            ["jit_code_bytes"] = (ulong)function.JitCodeBytes,
            ["borrowed_frames"] = (ulong)function.Borrowed,
            ["split_frames"] = (ulong)function.Split,
            ["raw_arguments"] = (ulong)function.RawArguments,
            ["detached_frames"] = (ulong)function.Detached
        }));
        if (function.Sites is { } sites)
            for (int pc = 0; pc < sites.Length; pc++)
                if (sites[pc].Hits != 0) CollectSite(image, functionId, pc, sites[pc], attributes, samples);
        if (function.Targets is { } targets)
            foreach (var (target, count) in targets.OrderBy(pair => pair.Key.Pc).ThenBy(pair => pair.Key.Image)
                .ThenBy(pair => pair.Key.Function))
            {
                samples.Add(new("vm.call", new(attributes)
                {
                    ["instruction.pc"] = Number(target.Pc),
                    ["target.image.id"] = Number(target.Image),
                    ["target.function.id"] = Number(target.Function),
                    ["target.function.name"] = target.Name
                }, new() { ["hits"] = (ulong)count.Hits }));
            }
    }

    private static void CollectSite(ImageCounts image, int functionId, int pc, SiteCounts site,
        Dictionary<string, string> function, List<RuntimeProfileSample> samples)
    {
        Instruction instruction = image.Module.Functions[functionId].Instructions[pc];
        Dictionary<string, string> attributes = new(function)
        {
            ["instruction.pc"] = Number(pc),
            ["opcode"] = instruction.Op.ToString()
        };
        if (instruction.Span is { } span)
        {
            attributes["source.file"] = span.Source;
            attributes["source.offset"] = Number(span.Start);
            attributes["source.length"] = Number(span.Length);
        }
        if (instruction.Op == OpCode.NativeCall && instruction.Text is { } name) attributes["native.name"] = name;
        samples.Add(new("vm.instruction", attributes, new() { ["hits"] = (ulong)site.Hits }));
        if (site.Sequential != 0) samples.Add(Edge(attributes, pc + 1, site.Sequential));
        if (site.Target != 0)
            samples.Add(Edge(attributes, instruction.Op == OpCode.IteratorNext ? instruction.B : instruction.A, site.Target));
    }

    private static RuntimeProfileSample Edge(Dictionary<string, string> attributes, int target, long hits) =>
        new("vm.edge", new(attributes) { ["target.pc"] = Number(target) }, new() { ["hits"] = (ulong)hits });

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record ImageIdentity(int Id);

    private sealed class ImageCounts(Module module, int id, bool instructionSites)
    {
        public Module Module { get; } = module;
        public int Id { get; } = id;
        public FunctionCounts?[] Functions { get; } = new FunctionCounts?[module.Functions.Count];
        public ConcurrentDictionary<string, NativeCounts> Natives { get; } = new(StringComparer.Ordinal);

        public FunctionCounts GetFunction(int functionId)
        {
            ref FunctionCounts? slot = ref Functions[functionId];
            var current = Volatile.Read(ref slot);
            if (current is not null) return current;
            var created = new FunctionCounts(instructionSites ? Module.Functions[functionId].Instructions.Count : null);
            return Interlocked.CompareExchange(ref slot, created, null) ?? created;
        }

        public Dictionary<string, string> Attributes() => new()
        {
            ["image.id"] = Number(Id),
            ["artifact.stage"] = Module.Stage.ToString(),
            ["optimization.level"] = Number(Module.OptimizationLevel)
        };

        public Dictionary<string, string> FunctionAttributes(int functionId)
        {
            var result = Attributes();
            result["function.id"] = Number(functionId);
            result["function.name"] = Module.Functions[functionId].Name;
            return result;
        }
    }

    internal sealed class FunctionCounts(int? sites)
    {
        public readonly long[] Opcodes = new long[byte.MaxValue + 1];
        public readonly SiteCounts[]? Sites = sites is { } count ? new SiteCounts[count] : null;
        public readonly ConcurrentDictionary<CallTarget, CallCount>? Targets = sites is null ? null : new();
        public long Calls;
        public long DispatchPauses;
        public long DispatchContinuations;
        public long PredicateCalls;
        public long DirectSourceCalls;
        public long GuardedScopeCrossings;
        public long GuardedScopeCrossingMisses;
        public long GuardedScopeRegions;
        public long GuardedScopeMisses;
        public long PredicateMisses;
        public long Boxes;
        public long RawLocalWrites;
        public long NumberSidecarArrays;
        public long NumberSidecarSlots;
        public long ManagedWordEdges;
        public long ManagedWordLocalReads;
        public long ManagedWordLocalWrites;
        public long ManagedWordIndexOperands;
        public long RawScopeStorageLoads;
        public long StaticScopePublications;
        public long StaticScopeMaterializations;
        public long NumericLocalCommits;
        public long NumericLoopEntries;
        public long NumericOperations;
        public long JitEntries;
        public long JitTailEntries;
        public long JitTailRejections;
        public long NativeWorkerEntries;
        public long NativeWorkerOperations;
        public long NativeWorkerStores;
        public long NativeWorkerPrefixExits;
        public long NativeWorkerGuardMisses;
        public long NativeWorkerUnmappedEntries;
        public long LiteralFactories;
        public long FoldedLiteralRows;
        public long NumberFolds;
        public long NumberFoldRows;
        public long NumberFoldNativeChunks;
        public long NumberFoldMisses;
        public long JitCompilations;
        public long JitYields;
        public long JitCompileNs;
        public long JitExecuteNs;
        public long JitCodeBytes;
        public long Borrowed;
        public long Split;
        public long RawArguments;
        public long Detached;
    }

    internal struct SiteCounts
    {
        public long Hits;
        public long Sequential;
        public long Target;
    }

    internal readonly record struct CallTarget(int Pc, int Image, int Function, string Name);

    internal sealed class CallCount
    {
        public long Hits;
    }

    internal sealed class NativeCounts
    {
        public long Calls;
        public long Fast;
    }

    internal interface IFrame
    {
        void Visit(int pc, OpCode op);
        void Call(int pc, Module image, int target);
        void DispatchPaused(bool created);
        void Box();
        void RawLocalWrite();
        void NumberSidecarAllocated(int slots);
        void ManagedWordEdge();
        void ManagedWordLocal(bool write);
        void ManagedWordIndex(int count);
        void RawScopeStorageLoad();
        void StaticScopePublished();
        void StaticScopeMaterialized();
        void NumberFold(ReadOnlySpan<Instruction> body, int rows, int chunks, bool exhausted);
        void NumberFoldMiss();
        void GuardedScopeRegion(ReadOnlySpan<Instruction> original);
        void GuardedScopeMiss();
        void GuardedScopeCrossing(ReadOnlySpan<Instruction> prefix, ReadOnlySpan<Instruction> arm);
        void GuardedScopeCrossingMiss();
        void NumericLocalCommit();
        void Detach();
        void EnterNumericLoop();
        void NumericOperation();
        NativeLoopObservation NativeLoopObservation { get; }
        void JitCompiled(long nanoseconds, int bytes);
        void JitExecuted(long nanoseconds, int yields, bool tail);
        void JitTailRejected();
        void AddNativeInstruction(OpCode op, long hits);
        void AddNativeOperations(long hits);
        double Power(double left, double right, Module image, Instruction instruction);
        RuntimeTimelineScope? BeginInlineNative(Module image, Instruction instruction);
        ValueTask<T> Observe<T>(ValueTask<T> pending, int pc, Instruction instruction, string reason);
        ValueTask Observe(ValueTask pending, int pc, Instruction instruction, string reason);
        Task<T> Observe<T>(Task<T> pending, int pc, Instruction instruction, string reason);
    }

    internal readonly struct DisabledFrame : IFrame
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Visit(int pc, OpCode op) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Call(int pc, Module image, int target) { }
        public void DispatchPaused(bool created) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Box() { }
        public void RawLocalWrite() { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void NumberSidecarAllocated(int slots) { }
        public void ManagedWordEdge() { }
        public void ManagedWordLocal(bool write) { }
        public void ManagedWordIndex(int count) { }
        public void RawScopeStorageLoad() { }
        public void StaticScopePublished() { }
        public void StaticScopeMaterialized() { }
        public void NumberFold(ReadOnlySpan<Instruction> body, int rows, int chunks, bool exhausted) { }
        public void NumberFoldMiss() { }
        public void GuardedScopeRegion(ReadOnlySpan<Instruction> original) { }
        public void GuardedScopeMiss() { }
        public void GuardedScopeCrossing(ReadOnlySpan<Instruction> prefix, ReadOnlySpan<Instruction> arm) { }
        public void GuardedScopeCrossingMiss() { }
        public void NumericLocalCommit() { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Detach() { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnterNumericLoop() { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void NumericOperation() { }
        public NativeLoopObservation NativeLoopObservation => NativeLoopObservation.None;
        public void JitCompiled(long nanoseconds, int bytes) { }
        public void JitExecuted(long nanoseconds, int yields, bool tail) { }
        public void JitTailRejected() { }
        public void AddNativeInstruction(OpCode op, long hits) { }
        public void AddNativeOperations(long hits) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Power(double left, double right, Module image, Instruction instruction) => NativeRegistry.Power(left, right);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RuntimeTimelineScope? BeginInlineNative(Module image, Instruction instruction) => null;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask<T> Observe<T>(ValueTask<T> pending, int pc, Instruction instruction, string reason) => pending;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask Observe(ValueTask pending, int pc, Instruction instruction, string reason) => pending;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Task<T> Observe<T>(Task<T> pending, int pc, Instruction instruction, string reason) => pending;
    }

    internal struct Frame(RuntimeProfile? owner, FunctionCounts? counters, RuntimeFrameTrace? trace) : IFrame
    {
        public readonly void DispatchPaused(bool created)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.DispatchPauses);
            if (created) Interlocked.Increment(ref counters.DispatchContinuations);
        }

        private int previousBranch = -1;

        internal readonly void DirectSourceCompleted() => Interlocked.Increment(ref counters!.DirectSourceCalls);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Box()
        {
            if (counters is not null) Interlocked.Increment(ref counters.Boxes);
        }

        public readonly void GuardedScopeRegion(ReadOnlySpan<Instruction> original)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.GuardedScopeRegions);
            // Dispatch has already counted the canonical first PC.
            CountGuardedOpcodes(counters, original[1..]);
        }

        public readonly void GuardedScopeMiss()
        {
            if (counters is not null) Interlocked.Increment(ref counters.GuardedScopeMisses);
        }

        public readonly void GuardedScopeCrossing(ReadOnlySpan<Instruction> prefix, ReadOnlySpan<Instruction> arm)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.GuardedScopeCrossings);
            CountGuardedOpcodes(counters, prefix);
            CountGuardedOpcodes(counters, arm);
        }

        public readonly void GuardedScopeCrossingMiss()
        {
            if (counters is not null) Interlocked.Increment(ref counters.GuardedScopeCrossingMisses);
        }

        private static void CountGuardedOpcodes(FunctionCounts counters, ReadOnlySpan<Instruction> code)
        {
            foreach (ref readonly var instruction in code)
                Interlocked.Increment(ref counters.Opcodes[(byte)instruction.Op]);
        }

        public readonly void NumberFold(ReadOnlySpan<Instruction> body, int rows, int chunks, bool exhausted)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.NumberFolds);
            Interlocked.Add(ref counters.NumberFoldRows, rows);
            Interlocked.Add(ref counters.NumberFoldNativeChunks, chunks);
            // GetIterator has already been visited at the dispatch PC.
            Interlocked.Increment(ref counters.Opcodes[(byte)OpCode.InitializeLocal]);
            Interlocked.Add(ref counters.Opcodes[(byte)OpCode.IteratorNext], (long)rows + (exhausted ? 1 : 0));
            foreach (ref readonly var instruction in body)
                Interlocked.Add(ref counters.Opcodes[(byte)instruction.Op], rows);
        }

        public readonly void NumberFoldMiss()
        {
            if (counters is not null) Interlocked.Increment(ref counters.NumberFoldMisses);
        }

        public readonly void RawScopeStorageLoad()
        {
            if (counters is not null) Interlocked.Increment(ref counters.RawScopeStorageLoads);
        }

        public readonly void StaticScopePublished()
        {
            if (counters is not null) Interlocked.Increment(ref counters.StaticScopePublications);
        }

        public readonly void StaticScopeMaterialized()
        {
            if (counters is not null) Interlocked.Increment(ref counters.StaticScopeMaterializations);
        }

        public readonly void RawLocalWrite()
        {
            if (counters is not null) Interlocked.Increment(ref counters.RawLocalWrites);
        }

        public readonly void NumberSidecarAllocated(int slots)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.NumberSidecarArrays);
            Interlocked.Add(ref counters.NumberSidecarSlots, slots);
        }

        public readonly void ManagedWordEdge()
        {
            if (counters is not null) Interlocked.Increment(ref counters.ManagedWordEdges);
        }

        public readonly void ManagedWordLocal(bool write)
        {
            if (counters is null) return;
            if (write) Interlocked.Increment(ref counters.ManagedWordLocalWrites);
            else Interlocked.Increment(ref counters.ManagedWordLocalReads);
        }

        public readonly void ManagedWordIndex(int count)
        {
            if (counters is not null) Interlocked.Add(ref counters.ManagedWordIndexOperands, count);
        }

        public readonly void NumericLocalCommit()
        {
            if (counters is not null) Interlocked.Increment(ref counters.NumericLocalCommits);
        }

        public readonly void EnterNumericLoop()
        {
            if (counters is not null) Interlocked.Increment(ref counters.NumericLoopEntries);
        }

        public readonly void NumericOperation()
        {
            if (counters is not null) Interlocked.Increment(ref counters.NumericOperations);
        }

        public readonly NativeLoopObservation NativeLoopObservation => counters is null ? NativeLoopObservation.None
            : counters.Sites is null ? NativeLoopObservation.Counts : NativeLoopObservation.InstructionSites;

        public readonly void JitCompiled(long nanoseconds, int bytes)
        {
            Interlocked.Increment(ref counters!.JitCompilations);
            Interlocked.Add(ref counters.JitCompileNs, nanoseconds);
            Interlocked.Add(ref counters.JitCodeBytes, bytes);
        }

        public readonly void JitExecuted(long nanoseconds, int yields, bool tail)
        {
            Interlocked.Increment(ref counters!.JitEntries);
            if (tail) Interlocked.Increment(ref counters.JitTailEntries);
            Interlocked.Add(ref counters.JitExecuteNs, nanoseconds);
            Interlocked.Add(ref counters.JitYields, yields);
        }

        public readonly void JitTailRejected()
        {
            if (counters is not null) Interlocked.Increment(ref counters.JitTailRejections);
        }

        public readonly void AddNativeInstruction(OpCode op, long hits) =>
            Interlocked.Add(ref counters!.Opcodes[(byte)op], hits);

        public readonly void AddNativeOperations(long hits) =>
            Interlocked.Add(ref counters!.NumericOperations, hits);

        public readonly void Detach()
        {
            if (counters is not null) Interlocked.Increment(ref counters.Detached);
        }

        public readonly double Power(double left, double right, Module image, Instruction instruction)
        {
            owner?.Native(image, "math.pow", fast: true);
            return trace is null ? NativeRegistry.Power(left, right) : trace.Power(left, right, instruction.Span);
        }

        public readonly RuntimeTimelineScope? BeginInlineNative(Module image, Instruction instruction)
        {
            owner?.Native(image, instruction.Text!, fast: true);
            return trace?.BeginInlineNative(instruction);
        }

        public readonly ValueTask<T> Observe<T>(ValueTask<T> pending, int pc, Instruction instruction, string reason) =>
            trace is null ? pending : trace.Observe(pending, reason, pc, instruction);

        public readonly ValueTask Observe(ValueTask pending, int pc, Instruction instruction, string reason) =>
            trace is null ? pending : trace.Observe(pending, reason, pc, instruction);

        public readonly Task<T> Observe<T>(Task<T> pending, int pc, Instruction instruction, string reason) =>
            trace is null ? pending : trace.Observe(pending, reason, pc, instruction);

        public void Visit(int pc, OpCode op)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.Opcodes[(byte)op]);
            if (counters.Sites is not { } sites) return;
            Interlocked.Increment(ref sites[pc].Hits);
            if (previousBranch >= 0)
            {
                ref SiteCounts from = ref sites[previousBranch];
                if (pc == previousBranch + 1) Interlocked.Increment(ref from.Sequential);
                else Interlocked.Increment(ref from.Target);
            }
            previousBranch = op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext ? pc : -1;
        }

        public readonly void Call(int pc, Module image, int target)
        {
            if (counters?.Targets is not { } targets) return;
            var key = new CallTarget(pc, owner!.Image(image).Id, target, image.Functions[target].Name);
            Interlocked.Increment(ref targets.GetOrAdd(key, static _ => new()).Hits);
        }
    }
}
