using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Msync.Core.Native;
using Msync.Runtime.Native;

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
            ["call_arguments_borrowed"] = (ulong)counters.BorrowedArguments,
            ["call_arguments_copied"] = (ulong)counters.CopiedArguments,
            ["call_argument_snapshots"] = (ulong)counters.ArgumentSnapshots,
            ["call_argument_snapshot_allocations"] = (ulong)counters.ArgumentSnapshotAllocations,
            ["acquire_descendant_futures"] = (ulong)counters.AcquireDescendantFutures,
            ["acquire_descendant_processes"] = (ulong)counters.AcquireDescendantProcesses,
            ["work_forwarded_futures"] = (ulong)counters.WorkForwardedFutures,
            ["work_forwarded_processes"] = (ulong)counters.WorkForwardedProcesses,
            ["function_components_ready"] = (ulong)counters.FunctionComponentsReady,
            ["function_components_pending"] = (ulong)counters.FunctionComponentsPending,
            ["function_compositions_ready"] = (ulong)counters.FunctionCompositionsReady,
            ["function_compositions_pending"] = (ulong)counters.FunctionCompositionsPending,
            ["machine_ready_operations"] = (ulong)counters.MachineReady,
            ["machine_pending_operations"] = (ulong)counters.MachinePending,
            ["machine_binary_operations"] = (ulong)counters.MachineBinary,
            ["machine_compare_operations"] = (ulong)counters.MachineCompare,
            ["machine_cast_operations"] = (ulong)counters.MachineCast,
            ["machine_intrinsic_operations"] = (ulong)counters.MachineIntrinsic,
            ["machine_unary_operations"] = (ulong)counters.MachineUnary,
            ["machine_wide_operations"] = (ulong)counters.MachineWide,
            ["machine_retained_dependencies"] = (ulong)counters.MachineDependencies,
            ["select_ready_conditions"] = (ulong)counters.SelectReadyConditions,
            ["select_pending_conditions"] = (ulong)counters.SelectPendingConditions,
            ["select_retained_candidates"] = (ulong)counters.SelectRetainedCandidates,
            ["tier_ready_materializations"] = (ulong)counters.TierReadyMaterializations,
            ["tier_context_pending"] = (ulong)counters.TierContextPending,
            ["tier_context_unrelated_pending"] = (ulong)counters.TierContextUnrelatedPending,
            ["direct_return_slots"] = (ulong)counters.DirectReturnSlots,
            ["direct_return_scalars"] = (ulong)counters.DirectReturnScalars,
            ["compiled_reads"] = (ulong)counters.CompiledReads,
            ["compiled_copies"] = (ulong)counters.CompiledCopies,
            ["refinement_copies"] = (ulong)counters.RefinementCopies,
            ["refinement_fallbacks"] = (ulong)counters.RefinementFallbacks,
            ["tier_copy_fallbacks"] = (ulong)counters.TierCopyFallbacks,
            ["tier_copy_zero_roots"] = (ulong)counters.TierCopyZeroRoots,
            ["tier_copy_one_root"] = (ulong)counters.TierCopyOneRoot,
            ["tier_copy_two_roots"] = (ulong)counters.TierCopyTwoRoots,
            ["tier_copy_bulk_roots"] = (ulong)counters.TierCopyBulkRoots,
            ["tier_workspace_leases"] = (ulong)counters.TierWorkspaceLeases,
            ["tier_workspace_reuses"] = (ulong)counters.TierWorkspaceReuses,
            ["activation_descriptor_allocations"] = (ulong)counters.ActivationDescriptorAllocations,
            ["activation_descriptor_reuses"] = (ulong)counters.ActivationDescriptorReuses,
            ["stack_frames"] = (ulong)counters.StackFrames,
            ["stack_frame_bytes"] = (ulong)counters.StackFrameBytes,
            ["stack_frame_roots"] = (ulong)counters.StackFrameRoots,
            ["stack_segment_allocations"] = (ulong)counters.StackSegmentAllocations,
            ["frame_storage_reuses"] = (ulong)counters.FrameStorageReuses,
            ["guest_stack_limited_calls"] = (ulong)counters.GuestStackLimitedCalls,
            ["guest_loop_handoffs"] = (ulong)counters.GuestLoopHandoffs,
            ["guest_direct_calls"] = (ulong)counters.GuestDirectCalls,
            ["guest_leaf_calls"] = (ulong)counters.GuestLeafCalls,
            ["guest_call_pushes"] = (ulong)counters.GuestCallPushes,
            ["guest_call_resumes"] = (ulong)counters.GuestCallResumes,
            ["guest_max_call_depth"] = (ulong)counters.GuestMaxCallDepth,
            ["heap_escaping_locations"] = (ulong)counters.HeapEscapingLocations,
            ["tail_arguments_copied"] = (ulong)counters.TailArguments,
            ["tail_argument_buffers"] = (ulong)counters.TailArgumentBuffers,
            ["tail_argument_buffer_reuses"] = (ulong)counters.TailArgumentBufferReuses,
            ["copies"] = (ulong)counters.Copies,
            ["moves"] = (ulong)counters.Moves,
            ["drops"] = (ulong)counters.Drops,
            ["jit_compilations"] = (ulong)counters.TierCompilations,
            ["jit_compile_ns"] = (ulong)counters.TierCompileNs,
            ["jit_code_bytes"] = (ulong)counters.TierCodeBytes,
            ["jit_entries"] = (ulong)counters.TierEntries,
            ["jit_operations"] = (ulong)counters.TierOperations,
            ["jit_direct_operations"] = (ulong)counters.TierDirectOperations,
            ["jit_readiness_exits"] = (ulong)counters.TierReadinessExits,
            ["jit_guard_misses"] = (ulong)counters.TierGuardMisses,
            ["jit_deoptimizations"] = (ulong)counters.TierDeoptimizations,
            ["jit_suspensions"] = (ulong)counters.TierSuspensions,
            ["jit_recompilations"] = (ulong)counters.TierRecompilations,
            ["linked_entries"] = (ulong)counters.LinkedEntries,
            ["linked_operations"] = (ulong)counters.LinkedOperations,
            ["jit_raw_operations"] = (ulong)counters.TierRawOperations,
            ["jit_register_regions"] = (ulong)counters.TierRegisterRegions,
            ["jit_register_values"] = (ulong)counters.TierRegisterValues,
            ["jit_u64_values"] = (ulong)counters.TierUnsignedValues,
            ["jit_initialization_checks_elided"] = (ulong)counters.TierInitializationChecksElided,
            ["jit_initialization_writes_elided"] = (ulong)counters.TierInitializationWritesElided,
            ["jit_u64_operations"] = (ulong)counters.TierUnsignedOperations,
            ["jit_register_spills"] = (ulong)counters.TierRegisterSpills,
            ["jit_eliminated_frame_stores"] = (ulong)counters.TierEliminatedStores,
            ["jit_selection_ns"] = (ulong)counters.TierSelectionNs,
            ["jit_register_allocation_ns"] = (ulong)counters.TierAllocationNs,
            ["jit_emission_ns"] = (ulong)counters.TierEmissionNs,
            ["jit_publication_ns"] = (ulong)counters.TierPublicationNs
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
        if (instruction.Op is NativeOp.MachineBinary or NativeOp.MachineCast or NativeOp.MachineUnary)
            attributes["machine.operation"] = ((Msync.Core.Llvm.LlvmOpcode)instruction.Immediate).ToString();
        else if (instruction.Op == NativeOp.MachineCompare)
            attributes["machine.operation"] = ((Msync.Core.Llvm.LlvmCmpPredicate)instruction.Immediate).ToString();
        else if (instruction.Op == NativeOp.MachineIntrinsic)
            attributes["machine.operation"] = ((NativeMachineIntrinsic)instruction.Immediate).ToString();
        if (instruction.Op == NativeOp.FunctionComponent) attributes["function.component"] = Number((int)instruction.Immediate);
        if (instruction.Op == NativeOp.ComposeFunction && instruction.Symbol is { } origin) attributes["function.context_origin"] = origin;
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
        internal readonly long[] Opcodes = new long[Enum.GetValues<NativeOp>().Max(static op => (int)op) + 1];
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
        internal long AcquireDescendantFutures, AcquireDescendantProcesses;
        internal long WorkForwardedFutures, WorkForwardedProcesses;
        internal long SelectReadyConditions, SelectPendingConditions, SelectRetainedCandidates;
        internal long FunctionComponentsReady, FunctionComponentsPending, FunctionCompositionsReady, FunctionCompositionsPending;
        internal long MachineReady, MachinePending, MachineBinary, MachineCompare, MachineCast;
        internal long MachineIntrinsic, MachineUnary, MachineWide, MachineDependencies;
        internal long GuestStackLimitedCalls, GuestLoopHandoffs;
        internal long TierReadyMaterializations;
        internal long TierContextPending, TierContextUnrelatedPending;
        internal long DirectReturnSlots, DirectReturnScalars;
        internal long TierWorkspaceLeases, TierWorkspaceReuses;
        internal long TierCopyFallbacks, TierCopyZeroRoots, TierCopyOneRoot, TierCopyTwoRoots, TierCopyBulkRoots;
        internal long ActivationDescriptorAllocations, ActivationDescriptorReuses;
        internal long StackFrames, StackFrameBytes, StackFrameRoots, StackSegmentAllocations;
        internal long BorrowedArguments, CopiedArguments, ArgumentSnapshots, FrameStorageReuses, HeapEscapingLocations;
        internal long TailArguments, TailArgumentBuffers, TailArgumentBufferReuses, ArgumentSnapshotAllocations;
        internal long CompiledCopies, CompiledReads;
        internal long RefinementCopies, RefinementFallbacks;
        internal long Copies;
        internal long Moves;
        internal long Drops;
        internal long TierUnsignedValues, TierUnsignedOperations, TierInitializationChecksElided;
        internal long TierInitializationWritesElided;
        internal long TierCompilations, TierCompileNs, TierCodeBytes, TierEntries, TierOperations;
        internal long TierReadinessExits, GuestDirectCalls, GuestLeafCalls, GuestCallPushes, GuestCallResumes, GuestMaxCallDepth;
        internal long TierDirectOperations, TierGuardMisses, TierDeoptimizations, TierSuspensions, TierRecompilations;
        internal long LinkedEntries, LinkedOperations;
        internal long TierRawOperations;
        internal long TierRegisterRegions, TierRegisterValues, TierRegisterSpills, TierEliminatedStores;
        internal long TierSelectionNs, TierAllocationNs, TierEmissionNs, TierPublicationNs;
    }

    internal readonly struct NativeFrameCounters(NativeProfileImage? image, int function, NativeFunctionCounters? counters)
    {
        internal bool Observed => counters is not null;
        internal void FunctionComponent(bool ready)
        {
            if (counters is null) return;
            if (ready) Interlocked.Increment(ref counters.FunctionComponentsReady);
            else Interlocked.Increment(ref counters.FunctionComponentsPending);
        }
        internal void FunctionCompose(bool ready)
        {
            if (counters is null) return;
            if (ready) Interlocked.Increment(ref counters.FunctionCompositionsReady);
            else Interlocked.Increment(ref counters.FunctionCompositionsPending);
        }
        internal void Machine(NativeOp operation, bool pending, bool wide, int dependencies)
        {
            if (counters is null) return;
            if (pending) Interlocked.Increment(ref counters.MachinePending);
            else Interlocked.Increment(ref counters.MachineReady);
            if (wide) Interlocked.Increment(ref counters.MachineWide);
            Interlocked.Add(ref counters.MachineDependencies, dependencies);
            switch (operation)
            {
                case NativeOp.MachineBinary: Interlocked.Increment(ref counters.MachineBinary); break;
                case NativeOp.MachineCompare: Interlocked.Increment(ref counters.MachineCompare); break;
                case NativeOp.MachineCast: Interlocked.Increment(ref counters.MachineCast); break;
                case NativeOp.MachineIntrinsic: Interlocked.Increment(ref counters.MachineIntrinsic); break;
                case NativeOp.MachineUnary: Interlocked.Increment(ref counters.MachineUnary); break;
            }
        }
        internal void Selection(bool pending, int retainedCandidates = 0)
        {
            if (counters is null) return;
            if (pending) Interlocked.Increment(ref counters.SelectPendingConditions);
            else Interlocked.Increment(ref counters.SelectReadyConditions);
            Interlocked.Add(ref counters.SelectRetainedCandidates, retainedCandidates);
        }
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
        internal void TierCodegen(NativeCodegenStatistics statistics)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.TierRegisterRegions, statistics.Regions);
            Interlocked.Add(ref counters.TierRegisterValues, statistics.Values);
            Interlocked.Add(ref counters.TierUnsignedValues, statistics.UnsignedValues);
            Interlocked.Add(ref counters.TierUnsignedOperations, statistics.UnsignedOperations);
            Interlocked.Add(ref counters.TierInitializationChecksElided, statistics.InitializationChecksElided);
            Interlocked.Add(ref counters.TierInitializationWritesElided, statistics.InitializationWritesElided);
            Interlocked.Add(ref counters.TierRegisterSpills, statistics.Spills);
            Interlocked.Add(ref counters.TierEliminatedStores, statistics.EliminatedStores);
            Interlocked.Add(ref counters.TierSelectionNs, statistics.SelectionNanoseconds);
            Interlocked.Add(ref counters.TierAllocationNs, statistics.AllocationNanoseconds);
            Interlocked.Add(ref counters.TierEmissionNs, statistics.EmissionNanoseconds);
            Interlocked.Add(ref counters.TierPublicationNs, statistics.PublicationNanoseconds);
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
        internal void GuestDirectCall(bool leaf)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.GuestDirectCalls);
            if (leaf) Interlocked.Increment(ref counters.GuestLeafCalls);
        }
        internal void GuestCall(int depth)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.GuestCallPushes);
            long previous = Volatile.Read(ref counters.GuestMaxCallDepth);
            while (depth > previous)
            {
                long observed = Interlocked.CompareExchange(ref counters.GuestMaxCallDepth, depth, previous);
                if (observed == previous) break;
                previous = observed;
            }
        }
        internal void GuestResume()
        {
            if (counters is not null) Interlocked.Increment(ref counters.GuestCallResumes);
        }
        internal void TierReadinessExit()
        {
            if (counters is not null) Interlocked.Increment(ref counters.TierReadinessExits);
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
        internal void TailArguments(int count, bool allocated)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.TailArguments, count);
            if (allocated) Interlocked.Increment(ref counters.TailArgumentBuffers);
            else Interlocked.Increment(ref counters.TailArgumentBufferReuses);
        }
        internal void AcquireDescendants(int futures, int processes)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.AcquireDescendantFutures, futures);
            Interlocked.Add(ref counters.AcquireDescendantProcesses, processes);
        }
        internal void ForwardWork(bool process)
        {
            if (counters is null) return;
            if (process) Interlocked.Increment(ref counters.WorkForwardedProcesses);
            else Interlocked.Increment(ref counters.WorkForwardedFutures);
        }
        internal void StackLimitedCall()
        {
            if (counters is not null) Interlocked.Increment(ref counters.GuestStackLimitedCalls);
        }
        internal void GuestLoopHandoff()
        {
            if (counters is not null) Interlocked.Increment(ref counters.GuestLoopHandoffs);
        }
        internal void MaterializedFutures(int count)
        {
            if (counters is not null) Interlocked.Add(ref counters.TierReadyMaterializations, count);
        }
        internal void TierPendingContext(bool global, bool relevant)
        {
            if (counters is null) return;
            if (relevant) Interlocked.Increment(ref counters.TierContextPending);
            else if (global) Interlocked.Increment(ref counters.TierContextUnrelatedPending);
        }
        internal void DirectReturn(bool scalar)
        {
            if (counters is null) return;
            if (scalar) Interlocked.Increment(ref counters.DirectReturnScalars);
            else Interlocked.Increment(ref counters.DirectReturnSlots);
        }
        internal void CompiledRead()
        {
            if (counters is not null) Interlocked.Increment(ref counters.CompiledReads);
        }
        internal void CompiledCopy()
        {
            if (counters is not null) Interlocked.Increment(ref counters.CompiledCopies);
        }
        internal void RefinementCopy(bool ready)
        {
            if (counters is null) return;
            if (ready) Interlocked.Increment(ref counters.RefinementCopies);
            else Interlocked.Increment(ref counters.RefinementFallbacks);
        }
        internal void TierCopy(int roots)
        {
            if (counters is null) return;
            switch (roots)
            {
                case -1: Interlocked.Increment(ref counters.TierCopyFallbacks); break;
                case 0: Interlocked.Increment(ref counters.TierCopyZeroRoots); break;
                case 1: Interlocked.Increment(ref counters.TierCopyOneRoot); break;
                case 2: Interlocked.Increment(ref counters.TierCopyTwoRoots); break;
                default: Interlocked.Increment(ref counters.TierCopyBulkRoots); break;
            }
        }
        internal void TierWorkspace(bool reused)
        {
            if (counters is null) return;
            if (reused) Interlocked.Increment(ref counters.TierWorkspaceReuses);
            else Interlocked.Increment(ref counters.TierWorkspaceLeases);
        }
        internal void CallActivation(bool reused)
        {
            if (counters is null) return;
            if (reused) Interlocked.Increment(ref counters.ActivationDescriptorReuses);
            else Interlocked.Increment(ref counters.ActivationDescriptorAllocations);
        }
        internal void StackFrame(int bytes, int roots, bool grew)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.StackFrames);
            Interlocked.Add(ref counters.StackFrameBytes, bytes);
            Interlocked.Add(ref counters.StackFrameRoots, roots);
            if (grew) Interlocked.Increment(ref counters.StackSegmentAllocations);
        }
        internal void CallStorage(int borrowed, int copied, bool snapshot, bool snapshotAllocated, bool reused)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.BorrowedArguments, borrowed);
            Interlocked.Add(ref counters.CopiedArguments, copied);
            if (snapshot) Interlocked.Increment(ref counters.ArgumentSnapshots);
            if (snapshotAllocated) Interlocked.Increment(ref counters.ArgumentSnapshotAllocations);
            if (reused) Interlocked.Increment(ref counters.FrameStorageReuses);
        }
        internal void EscapingLocation(NativeLayout layout)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.HeapEscapingLocations);
            Allocation(layout);
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
