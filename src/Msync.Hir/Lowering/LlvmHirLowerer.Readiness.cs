using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private readonly Dictionary<LlvmFunction, NativeUnary> liftedUnaryCalls = [];
        private readonly HashSet<LlvmArgument> futureParameters = [];
        private readonly HashSet<LlvmFunction> futureResults = [];
        private readonly HashSet<LlvmFieldCarrier> futureFields = [];
        private readonly Dictionary<LlvmArgument, LlvmFieldCarrier[]> futureCaptures = [];
        private readonly Dictionary<(NativeUnary Operation, LlvmFunctionType Signature), LlvmFunction> unaryBindings = [];
        private List<(LlvmInstruction Value, string Reason)>? readinessSources;
        internal readonly List<LlvmCallInstruction> controlDemands = [];

        internal T DeferredSource<T>(T value, string reason) where T : LlvmInstruction
        {
            (readinessSources ??= []).Add((value, reason));
            return value;
        }

        internal LlvmFunction DefineBinaryBinding(NativeBinary operation, LlvmFunctionType signature)
        {
            var target = LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage,
                "msync.value." + operation, module);
            runtimeCalls.Add(target, operation);
            binaryBindings.Add((operation, signature), target);
            return target;
        }

        private void SelectScalarCarriers()
        {
            ReadinessFlow? flow = null;
            if (readinessSources is not null)
            {
                SelectObservedTextConversions();
                flow = new ReadinessFlow(this);
                flow.Run();
                RecordFutureSignatures(flow);
                flow.RecordFutureStorage();
                SelectCompletionVectors(flow);
            }
            else
                trace?.Detail?.Metric("flow.ready_only", 1);

            if (flow is null && trace?.Detail is null)
            {
                foreach (var demand in controlDemands) EraseDemand(demand);
                return;
            }
            // Declarations are added on demand; only the originally emitted bodies are rewritten.
            int functionCount = module.Functions.Count;
            for (int index = 0; index < functionCount; ++index)
            {
                var function = module.Functions[index];
                if (function.IsDeclaration) continue;
                if (trace?.Detail is { } detail)
                    detail.Run("compiler.llvm.lower.readiness.function", (Lowering: this, Function: function, Flow: flow),
                        static state => state.Lowering.SelectScalars(state.Function, state.Flow),
                        attributes: ReadinessAttributes(function));
                else SelectScalars(function, flow);
            }
        }

        private void RecordFutureSignatures(ReadinessFlow flow)
        {
            foreach (var function in module.Functions)
            {
                if (function.IsDeclaration) continue;
                if (TrackedScalarCarrier(function.ReturnType) && flow.IsDeferred(function)) futureResults.Add(function);
                foreach (var argument in function.Arguments)
                    if (TrackedScalarCarrier(argument.Type) && flow.IsDeferred(argument)) futureParameters.Add(argument);
            }
            trace?.Detail?.Metric("flow.future_parameters", futureParameters.Count);
            trace?.Detail?.Metric("flow.future_results", futureResults.Count);

        }

        private bool TrackedScalarCarrier(LlvmType type) => type.IsDoubleTy() || type.IsIntegerTy(1)
            || type is LlvmStructType structure
            && (unionTypes.GetValueOrDefault(structure) & (TypeKind.Number | TypeKind.Bool)) != 0;

        private static Dictionary<string, string> ReadinessAttributes(LlvmFunction function)
        {
            var attributes = new Dictionary<string, string> { ["function.name"] = function.Name };
            if (function.BasicBlocks[0].FirstInstruction?.Source is { } source)
            {
                attributes["source.file"] = source.Source;
                attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return attributes;
        }

        private void SelectScalars(LlvmFunction function, ReadinessFlow? flow)
        {
            int ready = 0, lifted = 0, retainedDemands = 0, elidedDemands = 0;
            foreach (var block in function.BasicBlocks)
            for (LlvmInstruction? instruction = block.FirstInstruction; instruction is not null;)
            {
                var next = instruction.Next;
                if (ScalarOperation(instruction) is { } operation)
                {
                    if (flow?.IsDeferred(instruction) == true)
                    {
                        Lift(instruction, operation, flow);
                        lifted++;
                    }
                    else ready++;
                }
                if (instruction is LlvmCallInstruction { CalledFunction: { } target } demand
                    && valueOperations.TryGetValue(target, out var binding)
                    && binding.Operation == LlvmRuntimeValueOperation.DemandBool)
                {
                    if (flow?.IsDeferred(demand.GetArgument(0)) == true) retainedDemands++;
                    else { EraseDemand(demand); elidedDemands++; }
                }
                instruction = next;
            }
            trace?.Detail?.Metric("scalar.ready", ready);
            trace?.Detail?.Metric("scalar.lifted", lifted);
            trace?.Detail?.Metric("demand.retained", retainedDemands);
            trace?.Detail?.Metric("demand.elided", elidedDemands);
        }

        private static void EraseDemand(LlvmCallInstruction demand)
        {
            demand.ReplaceAllUsesWith(demand.GetArgument(0));
            demand.EraseFromParent();
        }

        private void Lift(LlvmInstruction instruction, ScalarEvaluation operation, ReadinessFlow flow)
        {
            var operands = operation.Unary is not null
                ? new[] { instruction.GetOperand(0)! } : [instruction.GetOperand(0)!, instruction.GetOperand(1)!];
            var signature = LlvmFunctionType.Get(instruction.Type, operands.Select(value => value.Type).ToArray());
            LlvmFunction target = operation.Unary is { } unary
                ? UnaryBinding(unary, signature)
                : LiftedBinaryBinding(operation.Binary, signature);
            var replacement = new LlvmCallInstruction(signature, target, operands)
                { LogicalType = instruction.LogicalType, Source = instruction.Source };
            for (int index = 0; index < operands.Length; ++index)
                replacement.GetOperandUse(index).LogicalType = instruction.GetOperandUse(index).LogicalType;
            flow.Replace(instruction, replacement);
            instruction.Parent!.InsertBefore(replacement, instruction);
            instruction.ReplaceAllUsesWith(replacement);
            instruction.EraseFromParent();
        }

        private LlvmFunction LiftedBinaryBinding(NativeBinary operation, LlvmFunctionType signature)
        {
            if (binaryBindings.TryGetValue((operation, signature), out var target)) return target;
            target = DefineBinaryBinding(operation, signature);
            // A lifted evaluation creates a fresh guest future when any operand is pending.
            // Its scheduler state is inaccessible to LLVM source loads and stores.
            target.MemoryEffectsAttribute = new(12);
            return target;
        }

        private LlvmFunction UnaryBinding(NativeUnary operation, LlvmFunctionType signature)
        {
            if (unaryBindings.TryGetValue((operation, signature), out var target)) return target;
            target = LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage,
                "msync.lifted." + operation, module);
            target.MemoryEffectsAttribute = new(12);
            unaryBindings.Add((operation, signature), target);
            liftedUnaryCalls.Add(target, operation);
            return target;
        }

        private readonly record struct ScalarEvaluation(NativeBinary Binary, NativeUnary? Unary = null);
        private static ScalarEvaluation? ScalarOperation(LlvmInstruction instruction) => instruction switch
        {
            LlvmUnaryOperator => new(default, NativeUnary.Negate),
            LlvmBinaryOperator binary => binary.Opcode switch
            {
                LlvmOpcode.FAdd => new(NativeBinary.Add),
                LlvmOpcode.FSub => new(NativeBinary.Subtract),
                LlvmOpcode.FMul => new(NativeBinary.Multiply),
                LlvmOpcode.FDiv => new(NativeBinary.Divide),
                LlvmOpcode.FRem => new(NativeBinary.Remainder),
                LlvmOpcode.Xor when binary.Type.IsIntegerTy(1) => new(default, NativeUnary.Not),
                _ => null
            },
            LlvmCmpInstruction comparison when comparison.Left.Type.IsDoubleTy() || comparison.Left.Type.IsIntegerTy(1)
                => comparison.Predicate switch
            {
                LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.ICmpEq => new(NativeBinary.Equal),
                LlvmCmpPredicate.FCmpUne or LlvmCmpPredicate.ICmpNe => new(NativeBinary.NotEqual),
                LlvmCmpPredicate.FCmpOgt => new(NativeBinary.Greater),
                LlvmCmpPredicate.FCmpOge => new(NativeBinary.GreaterEqual),
                LlvmCmpPredicate.FCmpOlt => new(NativeBinary.Less),
                LlvmCmpPredicate.FCmpOle => new(NativeBinary.LessEqual),
                _ => null
            },
            _ => null
        };

        // This is the closed producer graph before LLVM rewrites. Only a real submission,
        // retained session state or a deferred carrier reaching an operation seeds the worklist.
        private sealed class ReadinessFlow(Lowering owner)
        {
            private readonly Dictionary<LlvmValue, State> values = [];
            private readonly Dictionary<Storage, State> storage = [];
            private readonly Dictionary<LlvmArgument, List<LlvmValue>> captureSources = owner.CaptureSourceAddresses();
            private readonly Dictionary<LlvmArgument, Storage[]?> captureLanes = [];
            private readonly Dictionary<LlvmFunction, State>? controlWaits =
                owner.readinessSources is { } sources && sources.Any(source => source.Reason == "range_callback")
                    && sources.Any(source => source.Reason != "range_callback") ? [] : null;
            private readonly State exposedReads = new();
            private readonly State exposedWrites = new();
            private readonly State callbackParameters = new();
            private readonly State callbackResults = new();
            private readonly HashSet<LlvmFunction> callbacks = [];
            private readonly Queue<State> work = new();
            private readonly Dictionary<string, long>? sourceCounts = owner.trace?.Detail is null ? null : [];
            private int edges, propagated, sources, snapshots;
            internal List<LlvmCallInstruction>? ScalarSnapshots { get; private set; }

            [Flags]
            private enum Deferred : byte { None, Value = 1, Items = 2, Invocation = 4, Both = Value | Items }
            private enum Projection : byte { Identity, Value, Items, Elements, Yielded, Consumed, Invocation, Observed, Snapshot, Compared }
            private readonly record struct Edge(State Target, Projection Projection);

            private sealed class State
            {
                internal Deferred Deferred;
                internal List<Edge>? Uses;
            }

            // A field uses declaration identity (not numeric byte offset). A local never
            // aliases a field, and opaque capture pointers only join exposed storage.
            private readonly record struct Storage(LlvmValue? Local, int Layout = -1, int Field = -1);

            internal void Run()
            {
                if (owner.readinessSources is { } submissions)
                    foreach (var (value, reason) in submissions)
                    {
                        if (value is LlvmCallInstruction { CalledFunction: { } observed }
                            && owner.completedProcessText?.Contains(observed) == true) continue;
                        if (reason == "range_callback" && value is LlvmCallInstruction { CalledFunction: { } target } call
                            && owner.nativeCalls.TryGetValue(target, out string? name)
                            && ProjectedRangeCallback(call, NativeSignatures.Get(name)) is not null) continue;
                        bool unknownItems = reason == "range_callback" || reason == "open_call"
                            && (value.LogicalType < 0 || (owner.metadata.Types[value.LogicalType].Kind & TypeKind.Range) != 0);
                        Seed(Value(value), reason, unknownItems ? Deferred.Both : Deferred.Value);
                    }
                foreach (var function in owner.module.Functions)
                {
                    if (function.IsDeclaration) continue;
                    WireFunction(function);
                    foreach (var block in function.BasicBlocks)
                    foreach (var instruction in block.Instructions)
                        Wire(instruction);
                }
                ConnectCaptureLanes();

                while (work.TryDequeue(out State? state))
                    if (state.Uses is { } users)
                        foreach (Edge user in users)
                            if (Mark(user.Target, Project(state.Deferred, user.Projection))) propagated++;
                if (controlWaits is not null)
                    foreach (var selected in owner.rangeCallbackWork.Keys.ToArray())
                        if (owner.rangeCallbackWork[selected] == LlvmRangeCallbackWork.Ready
                            && controlWaits.TryGetValue(selected, out State? control)
                            && (control.Deferred & Deferred.Invocation) != 0)
                            owner.rangeCallbackWork[selected] = LlvmRangeCallbackWork.Cooperative;
                owner.trace?.Detail?.Metric("flow.edges", edges);
                owner.trace?.Detail?.Metric("flow.cells", storage.Count);
                owner.trace?.Detail?.Metric("flow.capture_lanes", captureLanes.Values.Sum(lanes => lanes?.Length ?? 0));
                owner.trace?.Detail?.Metric("flow.propagated", propagated);
                owner.trace?.Detail?.Metric("flow.sources", sources);
                owner.trace?.Detail?.Metric("flow.snapshot_item_edges", snapshots);
                if (sourceCounts is not null)
                    foreach (var (reason, count) in sourceCounts)
                        owner.trace!.Detail!.Metric("flow.source." + reason, count);
            }

            internal bool IsDeferred(LlvmValue value) => values.TryGetValue(value, out var state)
                && (state.Deferred & Deferred.Value) != 0;

            private static Deferred Project(Deferred value, Projection projection) => projection switch
            {
                Projection.Value => value & Deferred.Value,
                Projection.Items => value & (Deferred.Items | Deferred.Invocation),
                Projection.Snapshot => value & Deferred.Items,
                Projection.Invocation => value & Deferred.Invocation,
                Projection.Observed => (value & Deferred.Value) == 0 ? Deferred.None : Deferred.Invocation,
                Projection.Compared => (value & (Deferred.Items | Deferred.Invocation)) == 0 ? Deferred.None : Deferred.Invocation,
                Projection.Elements => value == Deferred.None ? Deferred.None : Deferred.Items,
                Projection.Yielded => (value & Deferred.Items) != 0 ? Deferred.Both : Deferred.None,
                Projection.Consumed => value == Deferred.None ? Deferred.None : Deferred.Both,
                _ => value
            };
            internal void Replace(LlvmValue previous, LlvmValue next)
            {
                if (values.Remove(previous, out State? state)) values.Add(next, state);
            }

            private void WireFunction(LlvmFunction function)
            {
                if (function.Users.Any(user => user is not LlvmCallInstruction call || !ReferenceEquals(call.CalledFunction, function)))
                    WireCallback(function);
            }

            private void WireCallback(LlvmFunction function)
            {
                if (!callbacks.Add(function)) return;
                int captures = owner.sourceFunctions[function].CaptureCount;
                for (int index = captures; index < function.Arguments.Count; ++index)
                    Flow(callbackParameters, Value(function.Arguments[index]));
                Flow(Value(function), callbackResults);
            }

            private void Wire(LlvmInstruction instruction)
            {
                switch (instruction)
                {
                    case LlvmStoreInstruction store:
                        Store(store.PointerOperand, store.ValueOperand);
                        break;
                    case LlvmLoadInstruction load:
                        if (load.PointerOperand is LlvmGlobalVariable global && owner.constants.ContainsKey(global)) break;
                        Read(load);
                        break;
                    case LlvmCallInstruction call:
                        WireCall(call);
                        break;
                    case LlvmReturnInstruction { ReturnValue: { } value }:
                        Flow(value, instruction.Parent!.Parent!);
                        break;
                    case LlvmUnaryOperator or LlvmBinaryOperator or LlvmCmpInstruction or LlvmPhiInstruction
                        or LlvmSelectInstruction or LlvmInsertValueInstruction or LlvmExtractValueInstruction
                        or LlvmGetElementPtrInstruction or LlvmCastInstruction or LlvmFreezeInstruction:
                        FlowOperands(instruction, instruction.Operands);
                        break;
                }
            }

            private void WireCall(LlvmCallInstruction call)
            {
                if (call.CalledFunction is not { } target) return;
                if (!target.IsDeclaration) { GuestCall(call, target, 0, 0); return; }

                if (owner.nativeCalls.TryGetValue(target, out string? name))
                {
                    WireNativeCall(call, NativeSignatures.Get(name));
                    return;
                }

                if (owner.callableCalls.TryGetValue(target, out var callable))
                {
                    CallableCall(call, callable);
                    return;
                }

                if (owner.iteratorCalls.TryGetValue(target, out var iterator))
                {
                    if (iterator == LlvmIteratorOperation.Next) Store(call.GetArgument(1), call.GetArgument(0));
                    else if (iterator != LlvmIteratorOperation.Close)
                        Flow(call.GetArgument(0), call, Projection.Yielded);
                    return;
                }

                if (owner.valueOperations.TryGetValue(target, out var operation))
                {
                    if (operation.Operation is LlvmRuntimeValueOperation.HasLayout or LlvmRuntimeValueOperation.SelectPath
                        or LlvmRuntimeValueOperation.ProjectAddress)
                        FlowOperands(call, call.Arguments);
                    else if (operation.Operation == LlvmRuntimeValueOperation.DemandBool && controlWaits is not null)
                        Flow(call.GetArgument(0), ControlWait(call.Parent!.Parent!), Projection.Observed);
                    return;
                }

                if (owner.runtimeConversions.TryGetValue(target, out var conversion))
                {
                    if (conversion.Operation is not (LlvmRuntimeConversionOperation.ObservedTextNumber
                        or LlvmRuntimeConversionOperation.ObservedTextBool))
                        FlowOperands(call, call.Arguments);
                }
                else if (owner.runtimeCalls.ContainsKey(target))
                {
                    int observed = owner.observedTextArguments.GetValueOrDefault(target);
                    if (observed == 0) FlowOperands(call, call.Arguments);
                    else
                        for (int index = 0; index < call.ArgumentCount; ++index)
                            if ((observed & (1 << index)) == 0) Flow(call.GetArgument(index), call, Projection.Identity);
                }
                else if (owner.constructors.TryGetValue(target, out var kind))
                {
                    FlowOperands(call, call.Arguments, kind == NativeKind.Range ? Projection.Elements : Projection.Identity);
                    if (kind == NativeKind.Range && call.ArgumentCount > 0
                        && (call.GetArgument(0).Type.IsDoubleTy() || call.GetArgument(0).Type.IsIntegerTy(1)))
                        (ScalarSnapshots ??= []).Add(call);
                }
            }

            private void WireNativeCall(LlvmCallInstruction call, Msync.Core.NativeSignature signature)
            {
                if (owner.emptyRangeConsumers?.Contains(call.CalledFunction!) == true) return;
                if (signature.ResultRule is NativeResultRule.RangeMerge or NativeResultRule.RangeMergeLeft
                    && WireSortedRange(call, signature.ResultRule == NativeResultRule.RangeMergeLeft)) return;
                if (signature.Name == "ranges.sort_with" && ExactRangeCallback(call.GetArgument(1)) is { } compare)
                {
                    var input = call.GetArgument(0);
                    Flow(input, call);
                    Flow(input, call, Projection.Compared);
                    WireComparison(call, compare, [input]);
                    return;
                }
                if (signature.Name is "ranges.materialize" or "ranges.pattern_prefix")
                {
                    // Collection completes traversal; the resulting snapshot
                    // copies its item datums without observing their payloads.
                    Flow(call.GetArgument(0), call, Projection.Snapshot);
                    snapshots++;
                    return;
                }
                if (ProjectedRangeCallback(call, signature) is { } selected)
                {
                    // Opening the input resolves its descriptor, not the next item.
                    // A filter consumes its predicate; a transform publishes its result.
                    int captures = owner.sourceFunctions[selected].CaptureCount;
                    Flow(call.GetArgument(0), selected.Arguments[captures], Projection.Yielded);
                    if (signature.Name == "ranges.transform")
                    {
                        Flow(selected, call, Projection.Elements);
                        Flow(call.GetArgument(0), call, Projection.Invocation);
                    }
                    else if (signature.Result.Kind == TypeKind.Bool)
                        Flow(selected, call, Projection.Observed);
                    else Flow(call.GetArgument(0), call, Projection.Items);
                    if (owner.RangeWork(selected) != LlvmRangeCallbackWork.Ready)
                        Seed(Value(call), "range_callback_invocation", Deferred.Invocation);
                    if (controlWaits is not null) Flow(ControlWait(selected), Value(call), Projection.Invocation);
                    return;
                }
                if (signature.Name is "ranges.sequence" or "ranges.closed")
                {
                    // The sequence can itself wait for bounds. Its cursor
                    // yields ready numbers after opening that sequence.
                    FlowOperands(call, call.Arguments, Projection.Value);
                    return;
                }
                if (signature.Name == "ranges.repeat")
                {
                    Flow(call.GetArgument(0), call, Projection.Elements);
                    return;
                }
                if (signature.Name is "ranges.take" or "ranges.drop" or "ranges.stride" or "ranges.step" or "ranges.enumerate_from")
                {
                    // Construction consumes the source descriptor and the
                    // count. Only already-lazy source items pass through.
                    Flow(call.GetArgument(0), call, Projection.Items);
                    return;
                }
                if ((signature.Behavior.GetValueOrDefault() & NativeBehavior.Invokes) != 0)
                {
                    if (!SelectedCallback(call, signature.ElementCallback))
                    {
                        foreach (LlvmValue argument in call.Arguments)
                            Flow(argument, callbackParameters, Projection.Consumed);
                        Flow(callbackResults, Value(call));
                    }
                }
                if (signature.IsStreamReduction) FlowOperands(call, call.Arguments, Projection.Consumed);
                else if (signature.LiftsScalarArguments
                    || (signature.Result.Kind & (TypeKind.Range | TypeKind.Scope | TypeKind.Function)) != 0)
                    FlowOperands(call, call.Arguments);
                if ((signature.Result.Kind & TypeKind.Range) != 0)
                    FlowOperands(call, call.Arguments, Projection.Elements);
            }

            private bool WireSortedRange(LlvmCallInstruction call, bool leftResult)
            {
                var compare = call.ArgumentCount == 3 ? ExactRangeCallback(call.GetArgument(2)) : null;
                if (call.ArgumentCount == 3 && compare is null) return false;
                var left = call.GetArgument(0);
                var right = call.GetArgument(1);
                // A comparison can delay the traversal, but it never produces an item.
                Flow(left, call);
                Flow(right, call, leftResult ? Projection.Value : Projection.Identity);
                Flow(left, call, Projection.Compared);
                Flow(right, call, Projection.Compared);
                if (compare is not null) WireComparison(call, compare, [left, right]);
                return true;
            }

            private void WireComparison(LlvmCallInstruction call, LlvmFunction compare, LlvmValue[] inputs)
            {
                int captures = owner.sourceFunctions[compare].CaptureCount;
                for (int parameter = captures; parameter < compare.Arguments.Count; ++parameter)
                    foreach (var input in inputs)
                        Flow(input, compare.Arguments[parameter], Projection.Yielded);
                Flow(compare, call, Projection.Observed);
                if (owner.RangeWork(compare) != LlvmRangeCallbackWork.Ready)
                    Seed(Value(call), "sorted_comparison_invocation", Deferred.Invocation);
                if (controlWaits is not null) Flow(ControlWait(compare), Value(call), Projection.Invocation);
            }

            private LlvmFunction? ExactRangeCallback(LlvmValue value)
            {
                if (value is not LlvmCallInstruction { CalledFunction: { } adapt }
                    || !owner.callableCalls.TryGetValue(adapt, out var binding)
                    || binding is not { Operation: LlvmCallableRuntimeOperation.Adapt, SessionSlot: < 0, Targets.Count: 1 })
                    return null;
                return binding.Targets.Values.First();
            }

            private LlvmFunction? ProjectedRangeCallback(LlvmCallInstruction call, Msync.Core.NativeSignature signature)
            {
                if (signature.Name is "ranges.transform" or "ranges.filter" or "ranges.sort_with" or
                    "ranges.all_of" or "ranges.any_of" or "ranges.any" or "ranges.none_of" or "ranges.none")
                    return ExactRangeCallback(call.GetArgument(1));
                return signature.ResultRule is NativeResultRule.RangeMerge or NativeResultRule.RangeMergeLeft && call.ArgumentCount == 3
                    ? ExactRangeCallback(call.GetArgument(2)) : null;
            }

            private bool SelectedCallback(LlvmCallInstruction call, NativeElementCallback? callback)
            {
                if (callback is null || call.GetArgument(callback.FunctionArgument) is not LlvmCallInstruction
                        { CalledFunction: { } target } adapt
                    || !owner.callableCalls.TryGetValue(target, out var binding)
                    || binding.Operation != LlvmCallableRuntimeOperation.Adapt
                    || binding.SessionSlot >= 0 || binding.Targets.Count == 0) return false;

                foreach (LlvmFunction selected in binding.Targets.Values)
                {
                    int captures = owner.sourceFunctions[selected].CaptureCount;
                    for (int index = captures; index < selected.Arguments.Count; ++index)
                        for (int input = 0; input < call.ArgumentCount; ++input)
                            if (input != callback.FunctionArgument)
                                Flow(call.GetArgument(input), selected.Arguments[index], Projection.Consumed);
                    Flow(selected, call);
                }
                // The callable may still be a native alternative; its input flow also
                // reaches the consuming operation, without tainting unrelated guests.
                Flow(adapt, call);
                return true;
            }

            private void CallableCall(LlvmCallInstruction call, LlvmCallableRuntimeBinding callable)
            {
                if (callable.Operation == LlvmCallableRuntimeOperation.Adapt)
                {
                    FlowOperands(call, call.Arguments);
                    foreach (LlvmFunction target in callable.Targets.Values)
                    {
                        WireCallback(target);
                        Flow(target, call);
                    }
                    return;
                }
                if (callable.SessionSlot >= 0 || callable.Targets.Count == 0)
                {
                    foreach (LlvmValue argument in call.Arguments) Flow(argument, callbackParameters);
                }
                foreach (LlvmFunction target in callable.Targets.Values)
                    GuestCall(call, target, 1, owner.sourceFunctions[target].CaptureCount);
            }

            private void GuestCall(LlvmCallInstruction call, LlvmFunction target, int firstInput, int firstParameter)
            {
                for (int index = firstParameter; index < target.Arguments.Count; ++index)
                    Flow(call.GetArgument(firstInput + index - firstParameter), target.Arguments[index]);
                Flow(target, call);
                if (controlWaits is not null) Flow(ControlWait(target), ControlWait(call.Parent!.Parent!), Projection.Invocation);
            }

            private State ControlWait(LlvmFunction function)
            {
                if (!controlWaits!.TryGetValue(function, out var state)) controlWaits.Add(function, state = new());
                return state;
            }

            private void Read(LlvmLoadInstruction load)
            {
                LlvmValue address = load.PointerOperand;
                if (Lanes(address) is { } lanes)
                    foreach (Storage lane in lanes)
                        Flow(Cell(lane), Value(load));
                else
                {
                    Flow(exposedReads, Value(load));
                    Flow(exposedWrites, Value(load));
                }
                // A builtin may populate a returned aggregate or range with deferred values.
                Flow(address, load);
            }

            private void Store(LlvmValue address, LlvmValue value)
            {
                if (Lanes(address) is { } lanes)
                    foreach (Storage lane in lanes)
                        Flow(value, Cell(lane));
                else Flow(value, exposedWrites);
            }

            private IEnumerable<Storage>? Lanes(LlvmValue address)
            {
                if (address is LlvmArgument argument) return CapturedLanes(argument);
                return DirectLanes(address);
            }

            private IEnumerable<Storage>? DirectLanes(LlvmValue address)
            {
                if (address is LlvmAllocaInstruction) return [new(address)];
                if (address is LlvmGetElementPtrInstruction { TargetStorageType: { } structure, IndexCount: 2 } gep
                    && gep.GetIndex(1) is LlvmConstantInt ordinal)
                    return [new(null, owner.structLayouts[structure], (int)ordinal.Value.GetLimitedValue())];
                if (address is LlvmCallInstruction { CalledFunction: { } target }
                    && owner.valueOperations.TryGetValue(target, out var binding)
                    && binding.Projection is { } projection)
                    return projection.Cases.Select(lane => new Storage(null, lane.Layout, lane.Field));
                return null;
            }

            private Storage[]? CapturedLanes(LlvmArgument argument)
            {
                if (captureLanes.TryGetValue(argument, out var cached)) return cached;
                HashSet<Storage> lanes = [];
                HashSet<LlvmArgument> visited = [];
                Stack<LlvmValue> pending = new();
                pending.Push(argument);
                bool complete = true;
                while (pending.TryPop(out var address))
                {
                    if (address is LlvmArgument captured)
                    {
                        if (!visited.Add(captured)) continue;
                        if (!captureSources.TryGetValue(captured, out var sources)) { complete = false; break; }
                        foreach (var source in sources) pending.Push(source);
                    }
                    else if (DirectLanes(address) is { } direct) lanes.UnionWith(direct);
                    else { complete = false; break; }
                }
                Storage[]? result = complete && lanes.Count != 0 ? lanes.ToArray() : null;
                captureLanes.Add(argument, result);
                return result;
            }

            private void ConnectCaptureLanes()
            {
                foreach (var argument in captureSources.Keys)
                {
                    if (CapturedLanes(argument) is not { Length: > 1 } lanes) continue;
                    var first = Cell(lanes[0]);
                    for (int index = 1; index < lanes.Length; ++index)
                    {
                        var next = Cell(lanes[index]);
                        Flow(first, next);
                        Flow(next, first);
                    }
                }
            }

            internal void RecordFutureStorage()
            {
                foreach (var (lane, state) in storage)
                {
                    if (lane.Layout < 0 || (state.Deferred & Deferred.Value) == 0) continue;
                    int type = owner.metadata.Layouts[lane.Layout].Fields[lane.Field].Type;
                    if ((owner.metadata.Types[type].Kind & (TypeKind.Number | TypeKind.Bool)) != 0)
                        owner.futureFields.Add(new(lane.Layout, lane.Field));
                }
                foreach (var (argument, lanes) in captureLanes)
                {
                    if (lanes is null) continue;
                    var fields = lanes.Select(lane => new LlvmFieldCarrier(lane.Layout, lane.Field)).ToArray();
                    if (fields.All(owner.futureFields.Contains)) owner.futureCaptures.Add(argument, fields);
                }
                owner.trace?.Detail?.Metric("flow.future_fields", owner.futureFields.Count);
                owner.trace?.Detail?.Metric("flow.future_captures", owner.futureCaptures.Count);
            }

            private State Cell(Storage lane)
            {
                if (storage.TryGetValue(lane, out State? state)) return state;
                state = new State();
                storage.Add(lane, state);
                if (lane.Local is null)
                {
                    Flow(state, exposedReads);
                    Flow(exposedWrites, state);
                }
                return state;
            }

            private void Seed(LlvmValue value, string reason) => Seed(Value(value), reason, Deferred.Both);
            private void Seed(State state, string reason, Deferred kind = Deferred.Both)
            {
                if (!Mark(state, kind)) return;
                sources++;
                if (sourceCounts is not null) sourceCounts[reason] = sourceCounts.GetValueOrDefault(reason) + 1;
            }
            private bool Mark(State state, Deferred kind)
            {
                if ((state.Deferred | kind) == state.Deferred) return false;
                state.Deferred |= kind;
                work.Enqueue(state);
                return true;
            }
            private State Value(LlvmValue value)
            {
                if (!values.TryGetValue(value, out var state)) values.Add(value, state = new State());
                return state;
            }
            private void FlowOperands(LlvmValue result, IEnumerable<LlvmValue?> operands, Projection projection = Projection.Identity)
            {
                foreach (var operand in operands)
                    if (operand is not null) Flow(operand, result, projection);
            }
            private void Flow(LlvmValue from, LlvmValue to, Projection projection = Projection.Identity) => Flow(from, Value(to), projection);
            private void Flow(LlvmValue from, State to, Projection projection = Projection.Identity)
            {
                if (from is not LlvmConstant || from is LlvmFunction) Flow(Value(from), to, projection);
            }
            private void Flow(State from, State to, Projection projection = Projection.Identity)
            {
                (from.Uses ??= []).Add(new(to, projection));
                edges++;
            }
        }
    }
}
