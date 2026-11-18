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
            ["call_physical_transfers"] = (ulong)counters.PhysicalArgumentTransfers,
            ["call_physical_arguments"] = (ulong)counters.PhysicalArgumentCount,
            ["call_physical_omitted_arguments"] = (ulong)counters.OmittedArguments,
            ["call_windows_adopted"] = (ulong)counters.AdoptedWindows,
            ["call_windows_zero_stage"] = (ulong)counters.ZeroStageWindows,
            ["call_window_arguments"] = (ulong)counters.WindowArguments,
            ["call_window_staged_arguments"] = (ulong)counters.WindowStagedArguments,
            ["call_window_owner_misses"] = (ulong)counters.WindowOwnerMisses,
            ["call_window_pending_misses"] = (ulong)counters.WindowPendingMisses,
            ["call_window_capacity_misses"] = (ulong)counters.WindowCapacityMisses,
            ["call_physical_pending_fallbacks"] = (ulong)counters.PhysicalArgumentPending,
            ["call_physical_owner_fallbacks"] = (ulong)counters.PhysicalArgumentOwner,
            ["call_argument_snapshots"] = (ulong)counters.ArgumentSnapshots,
            ["call_argument_snapshot_allocations"] = 0,
            ["call_argument_snapshot_bytes"] = (ulong)counters.ArgumentSnapshotBytes,
            ["call_argument_snapshot_reference_slots"] = (ulong)counters.ArgumentSnapshotReferences,
            ["frame_binding_slots"] = (ulong)counters.FrameBindings,
            ["frame_native_binding_slots"] = (ulong)counters.FrameNativeBindings,
            ["vm_scope_copies_forwarded"] = (ulong)counters.ForwardedScopeCopies,
            ["native_indirect_loads"] = (ulong)counters.NativeIndirectLoads,
            ["native_indirect_stores"] = (ulong)counters.NativeIndirectStores,
            ["indirect_pending"] = (ulong)counters.IndirectPending,
            ["indirect_non_native_owner"] = (ulong)counters.IndirectOwner,
            ["indirect_unlinked_layout"] = (ulong)counters.IndirectLayout,
            ["indirect_uninitialized"] = (ulong)counters.IndirectUninitialized,
            ["indirect_boundary_carrier"] = (ulong)counters.IndirectCarrier,
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
            ["native_builtin_storage_allocations"] = (ulong)counters.BuiltinStorageAllocations,
            ["native_path_joins"] = (ulong)counters.NativePathJoins,
            ["native_text_comparisons"] = (ulong)counters.NativeTextComparisons,
            ["native_text_objects"] = (ulong)counters.NativeTextObjects,
            ["native_text_bytes"] = (ulong)counters.NativeTextBytes,
            ["native_builtin_storage_elements"] = (ulong)counters.BuiltinStorageElements,
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
            ["stack_frame_native_roots"] = (ulong)counters.StackFrameNativeRoots,
            ["stack_segment_allocations"] = (ulong)counters.StackSegmentAllocations,
            ["stack_segment_reentries"] = (ulong)counters.StackSegmentReentries,
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
            ["jit_guest_reference_load_sites"] = (ulong)counters.TierGuestReferenceLoadSites,
            ["jit_guest_reference_store_sites"] = (ulong)counters.TierGuestReferenceStoreSites,
            ["jit_value_materialization_sites"] = (ulong)counters.TierValueMaterializationSites,
            ["jit_integer64_constants"] = (ulong)counters.TierInteger64Constants,
            ["jit_guest_null_constants"] = (ulong)counters.TierGuestNullConstants,
            ["jit_guest_function_composes"] = (ulong)counters.TierGuestFunctionComposes,
            ["jit_value_materialization_exits_with_pending"] = (ulong)counters.TierValueMaterializationPending,
            ["jit_value_materialization_exits_without_pending"] = (ulong)counters.TierValueMaterializationOther,
            ["jit_guest_reference_stores"] = (ulong)counters.TierGuestReferenceStores,
            ["jit_guest_reference_store_pending_exits"] = (ulong)counters.TierGuestReferenceStorePending,
            ["jit_guest_reference_store_storage_exits"] = (ulong)counters.TierGuestReferenceStoreStorage,
            ["jit_guest_reference_loads"] = (ulong)counters.TierGuestReferenceLoads,
            ["jit_guest_reference_load_pending_fallbacks"] = (ulong)counters.TierGuestReferenceLoadPending,
            ["jit_guest_reference_load_non_native_base_fallbacks"] = (ulong)counters.TierGuestReferenceLoadNonNative,
            ["jit_guest_reference_load_storage_fallbacks"] = (ulong)counters.TierGuestReferenceLoadStorage,
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
            ["jit_guest_carrier_adaptations"] = (ulong)counters.TierGuestCarrierAdaptations,
            ["jit_guest_refinement_copies"] = (ulong)counters.TierGuestRefinementCopies,
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
            ["jit_frame_call_sites"] = (ulong)counters.TierFrameCallSites,
            ["jit_fixed_native_argument_sites"] = (ulong)counters.TierFrameArgumentSites,
            ["jit_frame_calls_continued"] = (ulong)counters.TierFrameCalls,
            ["jit_frame_load_calls_continued"] = (ulong)counters.TierFrameLoadCalls,
            ["jit_frame_callable_copies_elided"] = (ulong)counters.TierFrameCallableCopiesElided,
            ["jit_frame_call_activation_exits"] = (ulong)counters.TierFrameCallExits,
            ["jit_fixed_guest_single_frame_returns"] = (ulong)counters.FixedGuestReturns,
            ["jit_fixed_guest_driver_handoffs"] = (ulong)counters.FixedGuestHandoffs,
            ["jit_fixed_native_calls"] = (ulong)counters.FixedTierCalls,
            ["jit_fixed_native_entries"] = (ulong)counters.FixedTierEntries,
            ["jit_fixed_native_code_lease_reuses"] = (ulong)counters.FixedTierCodeReuses,
            ["jit_fixed_native_code_leases"] = (ulong)counters.FixedTierCodeLeases,
            ["jit_fixed_native_frame_resumes"] = (ulong)counters.FixedTierResumes,
            ["jit_fixed_native_scalar_returns"] = (ulong)counters.FixedTierScalarReturns,
            ["jit_fixed_native_workless_returns"] = (ulong)counters.FixedTierWorklessReturns,
            ["jit_fixed_native_raw_returns"] = (ulong)counters.FixedRawTierReturns,
            ["jit_fixed_native_unattached_returns"] = (ulong)counters.FixedUnattachedReturns,
            ["jit_fixed_native_reservations"] = (ulong)counters.FixedUnattachedReservations,
            ["jit_fixed_native_emitted_frames"] = (ulong)counters.FixedEmittedFrames,
            ["jit_fixed_native_repeated_admissions"] = (ulong)counters.FixedRepeatedAdmissions,
            ["jit_fixed_native_repeated_attachments"] = (ulong)counters.FixedRepeatedAttachments,
            ["jit_fixed_native_inline_returns"] = (ulong)counters.FixedNativeReturns,
            ["jit_fixed_native_frame_attachments"] = (ulong)counters.FixedUnattachedAttachments,
            ["jit_fixed_native_reservation_misses"] = (ulong)counters.FixedUnattachedReservationMisses,
            ["jit_fixed_native_raw_managed_exits"] = (ulong)counters.FixedRawTierManagedExits,
            ["jit_fixed_native_raw_other_exits"] = (ulong)counters.FixedRawTierOtherExits,
            ["jit_location_captures_deferrable"] = (ulong)counters.TierDeferrableLocationCaptures,
            ["jit_fixed_native_capture_bindings_deferred"] = (ulong)counters.FixedCaptureBindingsDeferred,
            ["jit_fixed_native_capture_bindings_materialized"] = (ulong)counters.FixedCaptureBindingsMaterialized,
            ["jit_fixed_native_capture_bindings_avoided"] = (ulong)counters.FixedCaptureBindingsAvoided,
            ["jit_fixed_native_work_bridge_entries"] = (ulong)counters.FixedTierWorkBridgeEntries,
            ["jit_fixed_native_work_call_entries"] = (ulong)counters.FixedTierWorkCallEntries,
            ["jit_fixed_native_work_resume_entries"] = (ulong)counters.FixedTierWorkResumeEntries,
            ["jit_fixed_native_argument_placements"] = (ulong)counters.FixedTierArgumentPlacements,
            ["jit_fixed_native_arguments_placed"] = (ulong)counters.FixedTierArgumentsPlaced,
            ["jit_fixed_native_argument_reference_words"] = (ulong)counters.FixedTierArgumentReferenceWords,
            ["jit_fixed_native_argument_frame_exits"] = (ulong)counters.FixedTierArgumentFrameExits,
            ["jit_fixed_native_argument_pending_exits"] = (ulong)counters.FixedTierArgumentPendingExits,
            ["jit_fixed_native_argument_host_reference_exits"] = (ulong)counters.FixedTierArgumentHostReferenceExits,
            ["jit_machine_guest_calls"] = (ulong)counters.TierMachineCalls,
            ["jit_machine_pointer_call_sites"] = (ulong)counters.TierMachinePointerCallSites,
            ["jit_machine_pointer_calls"] = (ulong)counters.TierMachinePointerCalls,
            ["jit_machine_address_result_sites"] = (ulong)counters.TierMachineAddressResultSites,
            ["jit_machine_address_results"] = (ulong)counters.TierMachineAddressResults,
            ["jit_machine_argument_window_sites"] = (ulong)counters.TierMachineArgumentWindowSites,
            ["jit_machine_argument_window_calls"] = (ulong)counters.TierMachineArgumentWindowCalls,
            ["jit_machine_closure_calls"] = (ulong)counters.TierMachineClosureCalls,
            ["jit_machine_field_calls"] = (ulong)counters.TierMachineFieldCalls,
            ["jit_machine_field_argument_calls"] = (ulong)counters.TierMachineFieldArgumentCalls,
            ["jit_machine_composed_calls"] = (ulong)counters.TierMachineComposedCalls,
            ["native_range_window_kernels"] = (ulong)counters.RangeWindowKernels,
            ["native_range_window_fusions"] = (ulong)counters.RangeWindowFusions,
            ["native_range_plan_nodes"] = (ulong)counters.RangePlanNodes,
            ["native_range_plan_edges"] = (ulong)counters.RangePlanEdges,
            ["native_range_cursor_opens"] = (ulong)counters.RangePlanOpens,
            ["native_range_cursor_bytes"] = (ulong)counters.RangePlanBytes,
            ["native_range_ordered_nodes"] = (ulong)counters.OrderedRangePlans,
            ["native_range_callback_requests"] = (ulong)counters.RangeCallbackRequests,
            ["native_range_callback_resumes"] = (ulong)counters.RangeCallbackResumes,
            ["native_range_cursor_yields"] = (ulong)counters.RangePlanSteps,
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
                if (sites[pc] != 0 || counters.BridgeSites![pc].Entries != 0)
                    CollectNativeSite(image, function, pc, sites[pc], counters.BridgeSites![pc], attributes, samples);
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

    private static void CollectNativeSite(NativeProfileImage image, int function, int pc, long hits, NativeBridgeSite bridge,
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
        if ((bridge.Entries != 0 || bridge.Executions != 0) && image.Program.Backend is { } backend)
        {
            NativeBackendInstruction linked = backend.Functions[function].Instructions[pc];
            attributes["backend.opcode"] = linked.Op.ToString();
            attributes["backend.handler"] = linked.Handler.ToString();
        }
        Dictionary<string, ulong> metrics = new() { ["hits"] = (ulong)hits };
        if (bridge.Entries != 0) metrics["bridge.entries"] = (ulong)bridge.Entries;
        if (bridge.Executions != 0) metrics["bridge.executions"] = (ulong)bridge.Executions;
        if (bridge.GuardEntries != 0) metrics["bridge.guard_entries"] = (ulong)bridge.GuardEntries;
        samples.Add(new("vm.instruction", attributes, metrics));
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
    internal struct NativeBridgeSite { internal long Entries, Executions, GuardEntries; }

    internal sealed class NativeFunctionCounters
    {
        internal NativeFunctionCounters(int instructions, bool sites)
        {
            if (!sites) return;
            Sites = new long[instructions];
            BridgeSites = new NativeBridgeSite[instructions];
            Edges = new();
            Targets = new();
        }
        internal readonly long[] Opcodes = new long[Enum.GetValues<NativeOp>().Max(static op => (int)op) + 1];
        internal readonly long[]? Sites;
        internal readonly NativeBridgeSite[]? BridgeSites;
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
        internal long StackFrames, StackFrameBytes, StackFrameRoots, StackFrameNativeRoots, StackSegmentAllocations;
        internal long StackSegmentReentries;
        internal long BorrowedArguments, CopiedArguments, ArgumentSnapshots, FrameStorageReuses, HeapEscapingLocations;
        internal long PhysicalArgumentTransfers, PhysicalArgumentCount, PhysicalArgumentPending, PhysicalArgumentOwner;
        internal long OmittedArguments;
        internal long AdoptedWindows, ZeroStageWindows, WindowArguments, WindowStagedArguments, WindowOwnerMisses, WindowPendingMisses, WindowCapacityMisses;
        internal long TailArguments, TailArgumentBuffers, TailArgumentBufferReuses;
        internal long ArgumentSnapshotBytes, ArgumentSnapshotReferences, FrameBindings, FrameNativeBindings;
        internal long ForwardedScopeCopies;
        internal long NativeIndirectLoads, NativeIndirectStores, IndirectPending, IndirectOwner;
        internal long IndirectLayout, IndirectUninitialized, IndirectCarrier;
        internal long BuiltinStorageAllocations, BuiltinStorageElements, NativeTextObjects, NativeTextBytes, NativePathJoins, NativeTextComparisons;
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
        internal long TierGuestReferenceLoadSites, TierGuestReferenceLoads, TierGuestReferenceLoadPending,
            TierGuestReferenceLoadNonNative, TierGuestReferenceLoadStorage;
        internal long TierGuestReferenceStoreSites, TierGuestReferenceStores, TierGuestReferenceStorePending, TierGuestReferenceStoreStorage;
        internal long TierValueMaterializationSites, TierInteger64Constants, TierGuestNullConstants, TierGuestFunctionComposes;
        internal long TierValueMaterializationPending, TierValueMaterializationOther;
        internal long TierGuestPending, TierGuestNonNative, TierGuestUninitialized;
        internal long TierGuestLayoutSites, TierGuestLayoutChecks, TierGuestLayoutPending, TierGuestLayoutNonNative, TierGuestLayoutUnknown;
        internal long TierGuestLayoutKeptWithBridge;
        internal long TierGuestLayoutBranchSites, TierGuestLayoutBranches;
        internal long TierGuestCopySites, TierGuestCopies, TierGuestCopiesKeptWithBridge;
        internal long TierGuestRefinementCopies, TierGuestCarrierAdaptations;
        internal long TierGuestViewSites, TierGuestViews, TierGuestViewFallbacks, TierGuestViewsKeptWithBridge;
        internal long TierGuestCopyPending, TierGuestCopyUnready, TierGuestCopyHost;
        internal long GuestLinkedLayoutChecks;
        internal long TierMachineCallSites, TierMachineCalls, TierMachineClosureCalls, TierMachineFieldCalls, TierMachineComposedCalls;
        internal long TierFrameCallSites, TierFrameCalls, TierFrameLoadCalls, TierFrameCallableCopiesElided, TierFrameCallExits;
        internal long TierFrameArgumentSites;
        internal long FixedGuestReturns, FixedGuestHandoffs;
        internal long FixedTierEntries, FixedTierCalls, FixedTierResumes, FixedTierScalarReturns;
        internal long FixedTierWorklessReturns, FixedTierWorkBridgeEntries, FixedTierWorkCallEntries, FixedTierWorkResumeEntries;
        internal long FixedRawTierReturns, FixedRawTierManagedExits, FixedRawTierOtherExits;
        internal long FixedUnattachedReturns, FixedUnattachedReservations, FixedUnattachedAttachments, FixedUnattachedReservationMisses, FixedNativeReturns;
        internal long FixedEmittedFrames, FixedRepeatedAdmissions, FixedRepeatedAttachments;
        internal long TierDeferrableLocationCaptures, FixedCaptureBindingsDeferred, FixedCaptureBindingsMaterialized, FixedCaptureBindingsAvoided;
        internal long FixedTierArgumentPlacements, FixedTierArgumentsPlaced, FixedTierArgumentReferenceWords;
        internal long FixedTierArgumentFrameExits, FixedTierArgumentPendingExits, FixedTierArgumentHostReferenceExits;
        internal long FixedTierCodeLeases, FixedTierCodeReuses;
        internal long TierMachinePointerCallSites, TierMachinePointerCalls;
        internal long TierMachineAddressResultSites, TierMachineAddressResults;
        internal long TierMachineArgumentWindowSites, TierMachineArgumentWindowCalls;
        internal long NativeCaptureBytes, NativeCaptureLocations, NativeCaptureValues;
        internal long RangeCallbackRequests, RangeCallbackResumes, OrderedRangePlans;
        internal long RangeWindowFusions, RangeWindowKernels;
        internal long RangePlanNodes, RangePlanEdges, RangePlanOpens, RangePlanBytes, RangePlanSteps;
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
        internal void RangeCallback(bool resumed)
        {
            if (counters is null) return;
            if (resumed) Interlocked.Increment(ref counters.RangeCallbackResumes);
            else Interlocked.Increment(ref counters.RangeCallbackRequests);
        }

        internal void RangeWindowFusion()
        {
            if (counters is not null) Interlocked.Increment(ref counters.RangeWindowFusions);
        }
        internal void RangePlanNode(int edges, bool ordered = false)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.RangePlanNodes);
            Interlocked.Add(ref counters.RangePlanEdges, edges);
            if (ordered) Interlocked.Increment(ref counters.OrderedRangePlans);
        }
        internal void RangePlanOpen(int bytes, bool windowKernel = false)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.RangePlanOpens);
            Interlocked.Add(ref counters.RangePlanBytes, bytes);
            if (windowKernel) Interlocked.Increment(ref counters.RangeWindowKernels);
        }
        internal void RangePlanStep(long count = 1)
        {
            if (counters is not null) Interlocked.Add(ref counters.RangePlanSteps, count);
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
            Interlocked.Add(ref counters.TierGuestReferenceLoadSites, statistics.GuestReferenceLoadSites);
            Interlocked.Add(ref counters.TierGuestReferenceStoreSites, statistics.GuestReferenceStoreSites);
            Interlocked.Add(ref counters.TierValueMaterializationSites, statistics.ValueMaterializationSites);
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
            Interlocked.Add(ref counters.TierFrameCallSites, statistics.FrameCallSites);
            Interlocked.Add(ref counters.TierDeferrableLocationCaptures, statistics.DeferredLocationCaptures);
            Interlocked.Add(ref counters.TierFrameArgumentSites, statistics.FrameArgumentSites);
            Interlocked.Add(ref counters.TierMachinePointerCallSites, statistics.MachinePointerCallSites);
            Interlocked.Add(ref counters.TierMachineAddressResultSites, statistics.MachineAddressResultSites);
            Interlocked.Add(ref counters.TierMachineArgumentWindowSites, statistics.MachineArgumentWindowSites);
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

        internal void GuestReferenceLoad(long hits) => Interlocked.Add(ref counters!.TierGuestReferenceLoads, hits);
        internal void GuestReferenceStore(long hits) => Interlocked.Add(ref counters!.TierGuestReferenceStores, hits);
        internal void ValueMaterialized(NativeFastOperation operation, long hits)
        {
            if (counters is null) return;
            switch (operation)
            {
                case NativeFastOperation.Integer64Constant: Interlocked.Add(ref counters.TierInteger64Constants, hits); break;
                case NativeFastOperation.GuestNullConstant: Interlocked.Add(ref counters.TierGuestNullConstants, hits); break;
                case NativeFastOperation.GuestComposeFunction:
                    Interlocked.Add(ref counters.TierGuestFunctionComposes, hits);
                    Interlocked.Add(ref counters.FunctionCompositionsReady, hits);
                    break;
            }
        }

        internal void ValueMaterializationExit(bool pending)
        {
            if (counters is null) return;
            Interlocked.Increment(ref (pending ? ref counters.TierValueMaterializationPending : ref counters.TierValueMaterializationOther));
        }
        internal void GuestReferenceStoreExit(bool pending) =>
            Interlocked.Increment(ref (pending ? ref counters!.TierGuestReferenceStorePending : ref counters!.TierGuestReferenceStoreStorage));
        internal void GuestReferenceLoadFallback(bool pending, bool nativeBase)
        {
            if (pending) Interlocked.Increment(ref counters!.TierGuestReferenceLoadPending);
            else if (!nativeBase) Interlocked.Increment(ref counters!.TierGuestReferenceLoadNonNative);
            else Interlocked.Increment(ref counters!.TierGuestReferenceLoadStorage);
        }

        internal void GuestCopy(long hits, bool refinement, bool adapted = false)
        {
            Interlocked.Add(ref counters!.TierGuestCopies, hits);
            if (refinement) Interlocked.Add(ref counters!.TierGuestRefinementCopies, hits);
            if (adapted) Interlocked.Add(ref counters!.TierGuestCarrierAdaptations, hits);
        }
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
            else if (!NativeGuestObjectTable.IsObjectEdge(edge)) Interlocked.Increment(ref counters!.TierGuestLayoutNonNative);
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
        internal void TierFrameCall(bool fused, bool elided)
        {
            Interlocked.Increment(ref counters!.TierFrameCalls);
            if (fused) Interlocked.Increment(ref counters.TierFrameLoadCalls);
            if (elided) Interlocked.Increment(ref counters.TierFrameCallableCopiesElided);
        }
        internal void TierFrameCallExit() => Interlocked.Increment(ref counters!.TierFrameCallExits);
        internal void FixedGuestReturn()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedGuestReturns);
        }
        internal void FixedTierCall()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedTierCalls);
        }
        internal void FixedTierEntry()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedTierEntries);
        }
        internal void FixedTierCodeEntry(bool reused)
        {
            if (counters is null) return;
            Interlocked.Increment(ref (reused ? ref counters.FixedTierCodeReuses : ref counters.FixedTierCodeLeases));
        }
        internal void FixedTierResume()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedTierResumes);
        }
        internal void FixedTierScalarReturn()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedTierScalarReturns);
        }
        internal void FixedTierWorklessReturn()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedTierWorklessReturns);
        }
        internal void FixedRawTierExit(bool returned, bool managed)
        {
            if (counters is null) return;
            if (returned) Interlocked.Increment(ref counters.FixedRawTierReturns);
            else if (managed) Interlocked.Increment(ref counters.FixedRawTierManagedExits);
            else Interlocked.Increment(ref counters.FixedRawTierOtherExits);
        }
        internal void FixedUnattachedReturn()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedUnattachedReturns);
        }
        internal void FixedNativeReturn()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedNativeReturns);
        }

        internal void FixedUnattachedAttach()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedUnattachedAttachments);
        }
        internal void FixedUnattachedReservationMiss()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedUnattachedReservationMisses);
        }
        internal void FixedUnattachedReservation()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedUnattachedReservations);
        }
        internal void FixedEmittedFrame()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedEmittedFrames);
        }
        internal void FixedRepeatedAdmissions(int count)
        {
            if (counters is not null) Interlocked.Add(ref counters.FixedRepeatedAdmissions, count);
        }
        internal void FixedRepeatedAttachment()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedRepeatedAttachments);
        }
        internal void FixedTierCaptureBindings(int deferred = 0, int materialized = 0, int avoided = 0)
        {
            if (counters is null) return;
            if (deferred != 0) Interlocked.Add(ref counters.FixedCaptureBindingsDeferred, deferred);
            if (materialized != 0) Interlocked.Add(ref counters.FixedCaptureBindingsMaterialized, materialized);
            if (avoided != 0) Interlocked.Add(ref counters.FixedCaptureBindingsAvoided, avoided);
        }
        internal void FixedTierWorkBoundary(NativeInterpreter.FixedWorkBoundary boundary)
        {
            if (counters is null) return;
            switch (boundary)
            {
                case NativeInterpreter.FixedWorkBoundary.Bridge: Interlocked.Increment(ref counters.FixedTierWorkBridgeEntries); break;
                case NativeInterpreter.FixedWorkBoundary.Call: Interlocked.Increment(ref counters.FixedTierWorkCallEntries); break;
                case NativeInterpreter.FixedWorkBoundary.Resume: Interlocked.Increment(ref counters.FixedTierWorkResumeEntries); break;
            }
        }
        internal void FixedTierArgumentPlacement(int count, int referenceWords)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.FixedTierArgumentPlacements);
            Interlocked.Add(ref counters.FixedTierArgumentsPlaced, count);
            if (referenceWords != 0) Interlocked.Add(ref counters.FixedTierArgumentReferenceWords, referenceWords);
        }
        internal void FixedTierArgumentExit(NativeArgumentPlacement reason)
        {
            if (counters is null) return;
            switch (reason)
            {
                case NativeArgumentPlacement.Frame: Interlocked.Increment(ref counters.FixedTierArgumentFrameExits); break;
                case NativeArgumentPlacement.Pending: Interlocked.Increment(ref counters.FixedTierArgumentPendingExits); break;
                case NativeArgumentPlacement.HostReference: Interlocked.Increment(ref counters.FixedTierArgumentHostReferenceExits); break;
            }
        }
        internal void FixedGuestHandoff()
        {
            if (counters is not null) Interlocked.Increment(ref counters.FixedGuestHandoffs);
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
        internal void BridgeEntry(int pc, bool guard)
        {
            if (counters?.BridgeSites is not { } sites) return;
            Interlocked.Increment(ref sites[pc].Entries);
            if (guard) Interlocked.Increment(ref sites[pc].GuardEntries);
        }
        internal void BridgeInstruction(int pc)
        {
            if (counters?.BridgeSites is { } sites) Interlocked.Increment(ref sites[pc].Executions);
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
        internal void MachineCalls(long count, bool captured, bool pointer, bool addressResult, bool argumentWindow)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.TierMachineCalls, count);
            if (captured) Interlocked.Add(ref counters.TierMachineClosureCalls, count);
            if (pointer) Interlocked.Add(ref counters.TierMachinePointerCalls, count);
            if (addressResult) Interlocked.Add(ref counters.TierMachineAddressResults, count);
            if (argumentWindow) Interlocked.Add(ref counters.TierMachineArgumentWindowCalls, count);
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
        internal void StackFrame(int bytes, int roots, int nativeBytes, bool nativeReferences, bool segmentReentry)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.StackFrames);
            Interlocked.Add(ref counters.StackFrameBytes, bytes);
            Interlocked.Add(ref counters.StackFrameRoots, roots);
            if (nativeReferences) Interlocked.Add(ref counters.StackFrameNativeRoots, roots);
            if (segmentReentry) Interlocked.Increment(ref counters.StackSegmentReentries);
            if (nativeBytes == 0) return;
            Interlocked.Increment(ref counters.StackSegmentAllocations);
            Interlocked.Add(ref counters.StackSegmentNativeBytes, nativeBytes);
        }
        internal void CallStorage(int borrowed, int copied, bool snapshot, NativeOffset extent, bool reused)
        {
            if (counters is null) return;
            Interlocked.Add(ref counters.BorrowedArguments, borrowed);
            Interlocked.Add(ref counters.CopiedArguments, copied);
            if (snapshot)
            {
                Interlocked.Increment(ref counters.ArgumentSnapshots);
                Interlocked.Add(ref counters.ArgumentSnapshotBytes, extent.Bytes);
                Interlocked.Add(ref counters.ArgumentSnapshotReferences, extent.References);
            }
            if (reused) Interlocked.Increment(ref counters.FrameStorageReuses);
        }
        internal void PhysicalArguments(int count, int omitted)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.PhysicalArgumentTransfers);
            Interlocked.Add(ref counters.PhysicalArgumentCount, count);
            if (omitted != 0) Interlocked.Add(ref counters.OmittedArguments, omitted);
        }
        internal void ArgumentWindow(NativeWindowDisposition disposition, int arguments, int staged)
        {
            if (counters is null) return;
            switch (disposition)
            {
                case NativeWindowDisposition.Adopted:
                    Interlocked.Increment(ref counters.AdoptedWindows);
                    Interlocked.Add(ref counters.WindowArguments, arguments);
                    if (staged == 0) Interlocked.Increment(ref counters.ZeroStageWindows);
                    else Interlocked.Add(ref counters.WindowStagedArguments, staged);
                    break;
                case NativeWindowDisposition.Owner: Interlocked.Increment(ref counters.WindowOwnerMisses); break;
                case NativeWindowDisposition.Pending: Interlocked.Increment(ref counters.WindowPendingMisses); break;
                case NativeWindowDisposition.Capacity: Interlocked.Increment(ref counters.WindowCapacityMisses); break;
            }
        }
        internal void PhysicalArgumentsFallback(bool pending, int omitted)
        {
            if (counters is null) return;
            if (pending) Interlocked.Increment(ref counters.PhysicalArgumentPending);
            else Interlocked.Increment(ref counters.PhysicalArgumentOwner);
            if (omitted != 0) Interlocked.Add(ref counters.OmittedArguments, omitted);
        }
        internal void FrameBindings(int count, bool native)
        {
            if (counters is null || count == 0) return;
            Interlocked.Add(ref counters.FrameBindings, count);
            if (native) Interlocked.Add(ref counters.FrameNativeBindings, count);
        }
        internal void ForwardedScopeCopies(long count)
        {
            if (counters is not null && count != 0) Interlocked.Add(ref counters.ForwardedScopeCopies, count);
        }
        internal void IndirectAccess(int outcome, bool store)
        {
            if (counters is null) return;
            switch (outcome)
            {
                case 0:
                    if (store) Interlocked.Increment(ref counters.NativeIndirectStores);
                    else Interlocked.Increment(ref counters.NativeIndirectLoads);
                    break;
                case 1: Interlocked.Increment(ref counters.IndirectPending); break;
                case 2: Interlocked.Increment(ref counters.IndirectOwner); break;
                case 3: Interlocked.Increment(ref counters.IndirectLayout); break;
                case 4: Interlocked.Increment(ref counters.IndirectUninitialized); break;
                case 5: Interlocked.Increment(ref counters.IndirectCarrier); break;
            }
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
        internal void NativeTextOperation(bool join)
        {
            if (counters is null) return;
            if (join) Interlocked.Increment(ref counters.NativePathJoins);
            else Interlocked.Increment(ref counters.NativeTextComparisons);
        }
        internal void NativeText(int bytes)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.NativeTextObjects);
            Interlocked.Add(ref counters.NativeTextBytes, bytes);
        }

        internal void BuiltinStorage(int count)
        {
            if (counters is null) return;
            Interlocked.Increment(ref counters.BuiltinStorageAllocations);
            Interlocked.Add(ref counters.BuiltinStorageElements, count);
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
