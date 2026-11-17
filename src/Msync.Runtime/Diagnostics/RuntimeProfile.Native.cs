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
        NativeFrameCounters counters = NativeCounters(program, function);
        counters.EnterCall();
        return counters;
    }

    internal NativeFrameCounters NativeCounters(NativeProgram program, int function)
    {
        NativeProfileImage image = nativeImages.GetOrAdd(program, static (code, owner) => new(code,
            owner.nativeIdentities.GetValue(code, _ => new(Interlocked.Increment(ref owner.nextImage))).Id,
            owner.InstructionSites), this);
        return new(image, function, image.Functions[function]);
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
        CollectNativeStorage(samples);
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
            ["jit_ssa_compiled"] = (ulong)counters.SsaCompiled,
            ["jit_ssa_code_bytes"] = (ulong)counters.SsaCodeBytes,
            ["jit_ssa_compile_ns"] = (ulong)counters.SsaCompileNs,
            ["jit_ssa_unavailable"] = (ulong)counters.SsaUnavailable,
            ["jit_ssa_entry_fallbacks"] = (ulong)counters.SsaEntryFallbacks,
            ["jit_ssa_calls"] = (ulong)counters.SsaCalls,
            ["jit_ssa_machine_call_sites"] = (ulong)counters.SsaMachineCallSites,
            ["jit_ssa_machine_calls"] = (ulong)counters.SsaMachineCalls,
            ["jit_ssa_register_call_sites"] = (ulong)counters.SsaRegisterCallSites,
            ["jit_ssa_register_calls"] = (ulong)counters.SsaRegisterCalls,
            ["jit_ssa_call_spill_slots"] = (ulong)counters.SsaCallSpillSlots,
            ["jit_ssa_frameless_calls"] = (ulong)counters.SsaFramelessCalls,
            ["jit_ssa_loop_polls"] = (ulong)counters.SsaLoopPolls,
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
            ["function_components_ready_address"] = (ulong)counters.FunctionComponentsReadyAddress,
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
            ["jit_scalar_selects"] = (ulong)counters.JitScalarSelects,
            ["native_heap_reused_objects"] = (ulong)counters.NativeHeapReused,
            ["native_heap_objects"] = (ulong)counters.NativeHeapObjects,
            ["native_heap_payload_and_flags"] = (ulong)counters.NativeHeapBytes,
            ["native_heap_callable_bytes"] = (ulong)counters.NativeCallableBytes,
            ["native_heap_edge_bytes"] = (ulong)counters.NativeEdgeBytes,
            ["native_heap_descriptors_created"] = (ulong)counters.NativeDescriptorsCreated,
            ["native_heap_descriptors_reused"] = (ulong)counters.NativeDescriptorsReused,
            ["native_heap_environment_views_created"] = (ulong)counters.NativeEnvironmentViewsCreated,
            ["native_heap_environment_views_reused"] = (ulong)counters.NativeEnvironmentViewsReused,
            ["native_heap_reserved_bytes"] = (ulong)counters.NativeHeapReserved,
            ["stack_segment_native_bytes"] = (ulong)counters.StackSegmentNativeBytes,
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
            ["jit_register_plan_eligibility_instructions"] = (ulong)counters.TierRegisterPlanEligibilityInstructions,
            ["jit_register_plan_eligibility_ns"] = (ulong)counters.TierRegisterPlanEligibilityNanoseconds,
            ["jit_register_plan_skipped_no_eligible"] = (ulong)counters.TierRegisterPlanSkippedNoEligible,
            ["jit_register_plan_cfg_builds"] = (ulong)counters.TierRegisterPlanControlFlowBuilds,
            ["jit_register_plan_cfg_ns"] = (ulong)counters.TierRegisterPlanControlFlowNanoseconds,
            ["jit_register_plan_liveness_visits"] = (ulong)counters.TierRegisterPlanLivenessVisits,
            ["jit_register_plan_liveness_requeues"] = (ulong)counters.TierRegisterPlanLivenessRequeues,
            ["jit_register_plan_liveness_ns"] = (ulong)counters.TierRegisterPlanLivenessNanoseconds,
            ["jit_register_plan_pending_iterations"] = (ulong)counters.TierRegisterPlanPendingIterations,
            ["jit_register_plan_pending_visits"] = (ulong)counters.TierRegisterPlanPendingVisits,
            ["jit_register_plan_pending_ns"] = (ulong)counters.TierRegisterPlanPendingNanoseconds,
            ["jit_register_plan_regions_ns"] = (ulong)counters.TierRegisterPlanRegionNanoseconds,
            ["jit_register_regions"] = (ulong)counters.TierRegisterRegions,
            ["jit_register_values"] = (ulong)counters.TierRegisterValues,
            ["jit_u64_values"] = (ulong)counters.TierUnsignedValues,
            ["jit_initialization_checks_elided"] = (ulong)counters.TierInitializationChecksElided,
            ["jit_initialization_writes_elided"] = (ulong)counters.TierInitializationWritesElided,
            ["jit_u64_operations"] = (ulong)counters.TierUnsignedOperations,
            ["jit_double_compare_sites"] = (ulong)counters.TierDoubleComparisonSites,
            ["jit_unary_math_sites"] = (ulong)counters.TierUnaryMathSites,
            ["jit_guest_load_sites"] = (ulong)counters.TierGuestLoadSites,
            ["jit_guest_store_sites"] = (ulong)counters.TierGuestStoreSites,
            ["jit_guest_layout_sites"] = (ulong)counters.TierGuestLayoutSites,
            ["jit_guest_layout_kept_with_bridge_sites"] = (ulong)counters.TierGuestLayoutKeptWithBridge,
            ["jit_guest_layout_checks"] = (ulong)counters.TierGuestLayoutChecks,
            ["jit_guest_layout_branch_sites"] = (ulong)counters.TierGuestLayoutBranchSites,
            ["jit_guest_layout_branches"] = (ulong)counters.TierGuestLayoutBranches,
            ["jit_guest_copy_sites"] = (ulong)counters.TierGuestCopySites,
            ["jit_guest_view_sites"] = (ulong)counters.TierGuestViewSites,
            ["jit_guest_views"] = (ulong)counters.TierGuestViews,
            ["jit_guest_view_kept_with_bridge_sites"] = (ulong)counters.TierGuestViewsKeptWithBridge,
            ["jit_guest_view_fallbacks"] = (ulong)counters.TierGuestViewFallbacks,
            ["jit_guest_copies"] = (ulong)counters.TierGuestCopies,
            ["jit_guest_copy_kept_with_bridge_sites"] = (ulong)counters.TierGuestCopiesKeptWithBridge,
            ["jit_guest_copy_pending_fallbacks"] = (ulong)counters.TierGuestCopyPending,
            ["jit_guest_copy_unready_fallbacks"] = (ulong)counters.TierGuestCopyUnready,
            ["jit_guest_copy_host_fallbacks"] = (ulong)counters.TierGuestCopyHost,
            ["guest_linked_layout_checks"] = (ulong)counters.GuestLinkedLayoutChecks,
            ["jit_guest_layout_pending_fallbacks"] = (ulong)counters.TierGuestLayoutPending,
            ["jit_guest_layout_non_native_fallbacks"] = (ulong)counters.TierGuestLayoutNonNative,
            ["jit_guest_layout_unknown_fallbacks"] = (ulong)counters.TierGuestLayoutUnknown,
            ["jit_guest_field_kept_with_bridge_sites"] = (ulong)counters.TierGuestAccessKeptWithBridge,
            ["jit_guest_loads"] = (ulong)counters.TierGuestLoads,
            ["jit_guest_stores"] = (ulong)counters.TierGuestStores,
            ["jit_guest_field_pending_fallbacks"] = (ulong)counters.TierGuestPending,
            ["jit_guest_field_non_native_fallbacks"] = (ulong)counters.TierGuestNonNative,
            ["jit_guest_field_uninitialized_fallbacks"] = (ulong)counters.TierGuestUninitialized,
            ["jit_machine_call_sites"] = (ulong)counters.TierMachineCallSites,
            ["jit_machine_guest_calls"] = (ulong)counters.TierMachineCalls,
            ["jit_machine_pointer_call_sites"] = (ulong)counters.TierMachinePointerCallSites,
            ["jit_machine_pointer_calls"] = (ulong)counters.TierMachinePointerCalls,
            ["jit_machine_closure_calls"] = (ulong)counters.TierMachineClosureCalls,
            ["jit_machine_field_calls"] = (ulong)counters.TierMachineFieldCalls,
            ["jit_machine_field_argument_calls"] = (ulong)counters.TierMachineFieldArgumentCalls,
            ["jit_machine_composed_calls"] = (ulong)counters.TierMachineComposedCalls,
            ["native_sequence_opens"] = (ulong)counters.SequenceOpens,
            ["native_sequence_steps"] = (ulong)counters.SequenceSteps,
            ["jit_sequence_steps"] = (ulong)counters.TierSequenceSteps,
            ["native_heap_capture_vector_bytes"] = (ulong)counters.NativeCaptureBytes,
            ["native_heap_capture_vector_locations"] = (ulong)counters.NativeCaptureLocations,
            ["native_heap_capture_vector_values"] = (ulong)counters.NativeCaptureValues,
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
        internal long JitScalarSelects, StackSegmentNativeBytes;
        internal long NativeHeapObjects, NativeHeapBytes, NativeHeapReserved, NativeHeapReused, NativeCallableBytes;
        internal long NativeEdgeBytes, NativeDescriptorsCreated, NativeDescriptorsReused;
        internal long NativeEnvironmentViewsCreated, NativeEnvironmentViewsReused;
        internal long SelectReadyConditions, SelectPendingConditions, SelectRetainedCandidates;
        internal long FunctionComponentsReady, FunctionComponentsPending, FunctionCompositionsReady, FunctionCompositionsPending;
        internal long FunctionComponentsReadyAddress;
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
        internal long TierDoubleComparisonSites;
        internal long TierUnaryMathSites;
        internal long TierGuestLoadSites, TierGuestStoreSites, TierGuestLoads, TierGuestStores;
        internal long TierGuestAccessKeptWithBridge;
        internal long TierGuestPending, TierGuestNonNative, TierGuestUninitialized;
        internal long TierGuestLayoutSites, TierGuestLayoutChecks, TierGuestLayoutPending, TierGuestLayoutNonNative, TierGuestLayoutUnknown;
        internal long TierGuestLayoutKeptWithBridge;
        internal long TierGuestLayoutBranchSites, TierGuestLayoutBranches;
        internal long TierGuestCopySites, TierGuestCopies, TierGuestCopiesKeptWithBridge;
        internal long TierGuestViewSites, TierGuestViews, TierGuestViewFallbacks, TierGuestViewsKeptWithBridge;
        internal long TierGuestCopyPending, TierGuestCopyUnready, TierGuestCopyHost;
        internal long GuestLinkedLayoutChecks;
        internal long TierMachineCallSites, TierMachineCalls, TierMachineClosureCalls, TierMachineFieldCalls, TierMachineComposedCalls;
        internal long TierMachinePointerCallSites, TierMachinePointerCalls;
        internal long NativeCaptureBytes, NativeCaptureLocations, NativeCaptureValues;
        internal long SequenceOpens, SequenceSteps, TierSequenceSteps;
        internal long TierMachineFieldArgumentCalls;
        internal long TierInitializationWritesElided;
        internal long TierCompilations, TierCompileNs, TierCodeBytes, TierEntries, TierOperations;
        internal long TierReadinessExits, GuestDirectCalls, GuestLeafCalls, GuestCallPushes, GuestCallResumes, GuestMaxCallDepth;
        internal long TierDirectOperations, TierGuardMisses, TierDeoptimizations, TierSuspensions, TierRecompilations;
        internal long LinkedEntries, LinkedOperations;
        internal long TierRawOperations;
        internal long TierRegisterRegions, TierRegisterValues, TierRegisterSpills, TierEliminatedStores;
        internal long TierSelectionNs, TierAllocationNs, TierEmissionNs, TierPublicationNs;
        internal long TierRegisterPlanEligibilityInstructions;
        internal long TierRegisterPlanEligibilityNanoseconds;
        internal long TierRegisterPlanSkippedNoEligible;
        internal long TierRegisterPlanControlFlowBuilds;
        internal long TierRegisterPlanControlFlowNanoseconds;
        internal long TierRegisterPlanLivenessVisits;
        internal long TierRegisterPlanLivenessRequeues;
        internal long TierRegisterPlanLivenessNanoseconds;
        internal long TierRegisterPlanPendingIterations;
        internal long TierRegisterPlanPendingVisits;
        internal long TierRegisterPlanPendingNanoseconds;
        internal long TierRegisterPlanRegionNanoseconds;
        internal long SsaCompiled, SsaCodeBytes, SsaCompileNs, SsaUnavailable, SsaEntryFallbacks, SsaCalls, SsaLoopPolls, SsaFramelessCalls;
        internal long SsaMachineCallSites, SsaMachineCalls;
        internal long SsaRegisterCallSites, SsaRegisterCalls, SsaCallSpillSlots;
    }

    internal readonly struct NativeFrameCounters(NativeProfileImage? image, int function, NativeFunctionCounters? counters)
    {
        internal bool Observed => counters is not null;
        internal bool NeedsSites => counters?.Sites is not null;
        internal void EnterCall()
        {
            if (counters is not null) Interlocked.Increment(ref counters.Calls);
        }
        internal void SsaFramelessCall()
        {
            if (counters is not null) Interlocked.Increment(ref counters.SsaFramelessCalls);
        }
        internal void SsaCompile(int bytes, long ns, int callSites, int registerCallSites, int callSpills)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.SsaCompileNs, ns);
            if (bytes == 0) Interlocked.Increment(ref counters.SsaUnavailable);
            else
            {
                Interlocked.Increment(ref counters.SsaCompiled);
                Interlocked.Add(ref counters.SsaCodeBytes, bytes);
                Interlocked.Add(ref counters.SsaMachineCallSites, callSites);
                Interlocked.Add(ref counters.SsaRegisterCallSites, registerCallSites);
                Interlocked.Add(ref counters.SsaCallSpillSlots, callSpills);
            }
        }
        internal void SsaEntryFallback()
        {
            if (counters is not null) Interlocked.Increment(ref counters.SsaEntryFallbacks);
        }
        internal void SsaRun(int polls, long stateCalls, long registerCalls)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.SsaCalls);
            Interlocked.Add(ref counters.SsaLoopPolls, polls);
            Interlocked.Add(ref counters.SsaMachineCalls, stateCalls + registerCalls);
            Interlocked.Add(ref counters.SsaRegisterCalls, registerCalls);
        }
        internal void FunctionComponent(bool ready, bool exactAddress = false)
        {
            if (counters is null) return;
            if (ready) Interlocked.Increment(ref counters.FunctionComponentsReady);
            else Interlocked.Increment(ref counters.FunctionComponentsPending);
            if (exactAddress) Interlocked.Increment(ref counters.FunctionComponentsReadyAddress);
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
        internal void SequenceOpen()
        {
            if (counters is not null) Interlocked.Increment(ref counters.SequenceOpens);
        }
        internal void SequenceSteps(long count, bool machine)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.SequenceSteps, count);
            if (machine) Interlocked.Add(ref counters.TierSequenceSteps, count);
        }
        internal void DirectSelect(long count)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.SelectReadyConditions, count);
            Interlocked.Add(ref counters.JitScalarSelects, count);
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
            if (statistics.RegisterPlanning is { } planning)
            {
                Interlocked.Add(ref counters.TierRegisterPlanEligibilityInstructions, planning.EligibilityInstructions);
                Interlocked.Add(ref counters.TierRegisterPlanEligibilityNanoseconds, planning.EligibilityNanoseconds);
                Interlocked.Add(ref counters.TierRegisterPlanSkippedNoEligible, planning.SkippedNoEligible);
                Interlocked.Add(ref counters.TierRegisterPlanControlFlowBuilds, planning.ControlFlowBuilds);
                Interlocked.Add(ref counters.TierRegisterPlanControlFlowNanoseconds, planning.ControlFlowNanoseconds);
                Interlocked.Add(ref counters.TierRegisterPlanLivenessVisits, planning.LivenessVisits);
                Interlocked.Add(ref counters.TierRegisterPlanLivenessRequeues, planning.LivenessRequeues);
                Interlocked.Add(ref counters.TierRegisterPlanLivenessNanoseconds, planning.LivenessNanoseconds);
                Interlocked.Add(ref counters.TierRegisterPlanPendingIterations, planning.PendingIterations);
                Interlocked.Add(ref counters.TierRegisterPlanPendingVisits, planning.PendingVisits);
                Interlocked.Add(ref counters.TierRegisterPlanPendingNanoseconds, planning.PendingNanoseconds);
                Interlocked.Add(ref counters.TierRegisterPlanRegionNanoseconds, planning.RegionNanoseconds);
            }
            Interlocked.Add(ref counters.TierRegisterRegions, statistics.Regions);
            Interlocked.Add(ref counters.TierRegisterValues, statistics.Values);
            Interlocked.Add(ref counters.TierUnsignedValues, statistics.UnsignedValues);
            Interlocked.Add(ref counters.TierUnsignedOperations, statistics.UnsignedOperations);
            Interlocked.Add(ref counters.TierDoubleComparisonSites, statistics.DoubleComparisonSites);
            Interlocked.Add(ref counters.TierUnaryMathSites, statistics.UnaryMathSites);
            Interlocked.Add(ref counters.TierGuestLoadSites, statistics.GuestLoadSites);
            Interlocked.Add(ref counters.TierGuestStoreSites, statistics.GuestStoreSites);
            Interlocked.Add(ref counters.TierGuestLayoutSites, statistics.GuestLayoutSites);
            Interlocked.Add(ref counters.TierGuestLayoutBranchSites, statistics.GuestLayoutBranchSites);
            Interlocked.Add(ref counters.TierGuestCopySites, statistics.GuestCopySites);
            Interlocked.Add(ref counters.TierGuestViewSites, statistics.GuestViewSites);
            Interlocked.Add(ref counters.TierGuestViewsKeptWithBridge, statistics.GuestViewsKeptWithBridge);
            Interlocked.Add(ref counters.TierGuestCopiesKeptWithBridge, statistics.GuestCopiesKeptWithBridge);
            Interlocked.Add(ref counters.TierGuestLayoutKeptWithBridge, statistics.GuestLayoutKeptWithBridge);
            Interlocked.Add(ref counters.TierGuestAccessKeptWithBridge, statistics.GuestAccessKeptWithBridge);
            Interlocked.Add(ref counters.TierMachineCallSites, statistics.MachineCallSites);
            Interlocked.Add(ref counters.TierMachinePointerCallSites, statistics.MachinePointerCallSites);
            Interlocked.Add(ref counters.TierInitializationChecksElided, statistics.InitializationChecksElided);
            Interlocked.Add(ref counters.TierInitializationWritesElided, statistics.InitializationWritesElided);
            Interlocked.Add(ref counters.TierRegisterSpills, statistics.Spills);
            Interlocked.Add(ref counters.TierEliminatedStores, statistics.EliminatedStores);
            Interlocked.Add(ref counters.TierSelectionNs, statistics.SelectionNanoseconds);
            Interlocked.Add(ref counters.TierAllocationNs, statistics.AllocationNanoseconds);
            Interlocked.Add(ref counters.TierEmissionNs, statistics.EmissionNanoseconds);
            Interlocked.Add(ref counters.TierPublicationNs, statistics.PublicationNanoseconds);
        }
        internal void GuestFieldAccess(bool load, long hits) =>
            Interlocked.Add(ref (load ? ref counters!.TierGuestLoads : ref counters!.TierGuestStores), hits);

        internal void GuestCopy(long hits) => Interlocked.Add(ref counters!.TierGuestCopies, hits);
        internal void GuestView(long hits) => Interlocked.Add(ref counters!.TierGuestViews, hits);
        internal void GuestViewFallback() => Interlocked.Increment(ref counters!.TierGuestViewFallbacks);
        internal void GuestCopyFallback(bool pending, bool ready)
        {
            if (pending) Interlocked.Increment(ref counters!.TierGuestCopyPending);
            else if (!ready) Interlocked.Increment(ref counters!.TierGuestCopyUnready);
            else Interlocked.Increment(ref counters!.TierGuestCopyHost);
        }
        internal void GuestLayoutCheck(long hits, bool branch)
        {
            Interlocked.Add(ref counters!.TierGuestLayoutChecks, hits);
            if (branch) Interlocked.Add(ref counters.TierGuestLayoutBranches, hits);
        }
        internal void GuestLinkedLayoutCheck()
        {
            if (counters is not null) Interlocked.Increment(ref counters.GuestLinkedLayoutChecks);
        }

        internal void GuestLayoutFallback(bool pending, nint edge)
        {
            if (pending) Interlocked.Increment(ref counters!.TierGuestLayoutPending);
            else if (edge == 0) Interlocked.Increment(ref counters!.TierGuestLayoutNonNative);
            else if (NativeGuestObjectTable.LayoutIdentity(edge) == NativeGuestObjectTable.UnknownLayout)
                Interlocked.Increment(ref counters!.TierGuestLayoutUnknown);
        }

        internal void GuestFieldFallback(bool pending, bool hasEdge)
        {
            if (pending) Interlocked.Increment(ref counters!.TierGuestPending);
            else if (!hasEdge) Interlocked.Increment(ref counters!.TierGuestNonNative);
            else Interlocked.Increment(ref counters!.TierGuestUninitialized);
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
        internal void BatchCall(int pc, int target, long count)
        {
            if (counters?.Targets is not { } targets) return;
            Interlocked.Add(ref targets.GetOrAdd((pc, target), static _ => new()).Calls, count);
        }
        internal void MachineCalls(long count, bool captured, bool pointer)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.TierMachineCalls, count);
            if (captured) Interlocked.Add(ref counters.TierMachineClosureCalls, count);
            if (pointer) Interlocked.Add(ref counters.TierMachinePointerCalls, count);
        }
        internal void MachineSourceCalls(long count, bool field, bool argument)
        {
            if (counters is null) return;
            if (field) Interlocked.Add(ref counters.TierMachineFieldCalls, count);
            else Interlocked.Add(ref counters.TierMachineComposedCalls, count);
            if (argument) Interlocked.Add(ref counters.TierMachineFieldArgumentCalls, count);
        }
        internal void NativeCallableCells(int bytes)
        {
            if (counters is not null) Interlocked.Add(ref counters.NativeCallableBytes, bytes);
        }
        internal void NativeGuestDescriptor(bool reused, int bytes)
        {
            if (counters is null) return;
            Interlocked.Increment(ref (reused ? ref counters.NativeDescriptorsReused : ref counters.NativeDescriptorsCreated));
            if (bytes != 0) Interlocked.Add(ref counters.NativeEdgeBytes, bytes);
        }
        internal void NativeGuestEnvironment(bool reused)
        {
            if (counters is null) return;
            Interlocked.Increment(ref (reused ? ref counters.NativeEnvironmentViewsReused : ref counters.NativeEnvironmentViewsCreated));
        }
        internal void NativeCaptureVector(int bytes, int locations, int values, long reserved)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.NativeCaptureBytes, bytes);
            Interlocked.Add(ref counters.NativeCaptureLocations, locations);
            Interlocked.Add(ref counters.NativeCaptureValues, values);
            if (reserved != 0) Interlocked.Add(ref counters.NativeHeapReserved, reserved);
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
        internal void StackFrame(int bytes, int roots, int nativeBytes)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.StackFrames);
            Interlocked.Add(ref counters.StackFrameBytes, bytes);
            Interlocked.Add(ref counters.StackFrameRoots, roots);
            if (nativeBytes == 0) return;
            Interlocked.Increment(ref counters.StackSegmentAllocations);
            Interlocked.Add(ref counters.StackSegmentNativeBytes, nativeBytes);
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
        internal void Native(string name, long count = 1)
        {
            if (image is null) return;
            Interlocked.Add(ref image.Natives.GetOrAdd(name, static _ => new()).Calls, count);
        }
        internal void Pause()
        {
            if (counters is not null) Interlocked.Increment(ref counters.Pauses);
        }
        internal void Continue()
        {
            if (counters is not null) Interlocked.Increment(ref counters.Continuations);
        }
        internal void NativeHeapAllocation(int bytes, long reserved, bool reused)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.NativeHeapObjects);
            Interlocked.Add(ref counters.NativeHeapBytes, (long)bytes * 2);
            if (reserved != 0) Interlocked.Add(ref counters.NativeHeapReserved, reserved);
            if (reused) Interlocked.Increment(ref counters.NativeHeapReused);
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
