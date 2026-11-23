using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private readonly Dictionary<LlvmFunction, int> observedTextArguments = [];
        private readonly Dictionary<(LlvmFunction, int), LlvmFunction> observedTextCalls = [];

        private void SelectObservedTextConversions()
        {
            Dictionary<LlvmBasicBlock, Dictionary<LlvmValue, LlvmCallInstruction>>? observed = null;
            foreach (var demand in controlDemands)
            {
                LlvmBasicBlock observer = demand.Parent!;
                if (ObservedPredicate(demand.GetArgument(0)) is not LlvmCallInstruction { CalledFunction: { } target } predicate
                    || predicate.Parent != observer
                    || !runtimeConversions.TryGetValue(target, out var conversion)
                    || conversion.Operation != LlvmRuntimeConversionOperation.IsNull
                    || BlockTextProducer(predicate.GetArgument(0), observer, observer) is not { } producer) continue;
                observed ??= [];
                if (!observed.TryGetValue(observer, out var byProducer)) observed.Add(observer, byProducer = []);
                if (!byProducer.TryGetValue(producer, out var previous) || demand.ComesBefore(previous))
                    byProducer[producer] = demand;
            }
            if (observed is null) return;

            int specialized = 0, nonentry = 0, selectedOperands = 0;
            foreach (var (observer, byProducer) in observed)
                foreach (var block in TextContinuations(observer))
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is not LlvmCallInstruction { CalledFunction: { } target } call) continue;
                    if (nativeCalls.GetValueOrDefault(target) == "string.concat"
                        || runtimeCalls.GetValueOrDefault(target) is NativeBinary.Equal or NativeBinary.NotEqual
                            or NativeBinary.Less or NativeBinary.LessEqual or NativeBinary.Greater or NativeBinary.GreaterEqual)
                    {
                        int known = observedTextArguments.GetValueOrDefault(target);
                        int mask = known;
                        for (int index = 0; index < call.ArgumentCount; ++index)
                            if (Observed(call, index)) mask |= 1 << index;
                        if (mask != known)
                        {
                            call.CalledOperand = ObservedTextCall(target, mask);
                            ++selectedOperands;
                        }
                        continue;
                    }
                    if (call.ArgumentCount != 1 || !runtimeConversions.TryGetValue(target, out var conversion)
                        || conversion.Operation != LlvmRuntimeConversionOperation.Convert
                        || conversion.TargetType < 0
                        || metadata.Types[conversion.TargetType].Kind is not (TypeKind.Number or TypeKind.Bool)
                        || !Observed(call, 0)) continue;
                    var operation = metadata.Types[conversion.TargetType].Kind == TypeKind.Number
                        ? LlvmRuntimeConversionOperation.ObservedTextNumber : LlvmRuntimeConversionOperation.ObservedTextBool;
                    call.CalledOperand = ConversionBinding(target.FunctionType, conversion with { Operation = operation });
                    specialized++;
                    if (observer != observer.Parent!.BasicBlocks[0]) nonentry++;

                    bool Observed(LlvmCallInstruction consumer, int index) =>
                        BlockTextProducer(consumer.GetArgument(index), observer, block) is { } producer
                        && byProducer.TryGetValue(producer, out var demand)
                        && (block != observer || demand.ComesBefore(consumer));
                }
            trace?.Detail?.Metric("flow.observed_text_conversions", specialized);
            trace?.Detail?.Metric("flow.observed_text_nonentry", nonentry);
            trace?.Detail?.Metric("flow.observed_text_call_sites", selectedOperands);
        }

        private static LlvmValue ObservedPredicate(LlvmValue condition)
        {
            if (condition is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } negated || !condition.Type.IsIntegerTy(1))
                return condition;
            if (negated.Right is LlvmConstantInt { Value.IsAllOnes: true }) return negated.Left;
            return negated.Left is LlvmConstantInt { Value.IsAllOnes: true } ? negated.Right : condition;
        }

        private LlvmFunction ObservedTextCall(LlvmFunction target, int mask)
        {
            if (observedTextCalls.TryGetValue((target, mask), out var selected)) return selected;
            selected = LlvmFunction.Create(target.FunctionType, LlvmLinkage.ExternalLinkage,
                target.Name + ".observed." + mask, module);
            if (nativeCalls.TryGetValue(target, out var native)) nativeCalls.Add(selected, native);
            else runtimeCalls.Add(selected, runtimeCalls[target]);
            observedTextArguments.Add(selected, mask);
            observedTextCalls.Add((target, mask), selected);
            return selected;
        }

        private static IEnumerable<LlvmBasicBlock> TextContinuations(LlvmBasicBlock observer)
        {
            if (observer == observer.Parent!.BasicBlocks[0]) return observer.Parent.BasicBlocks;
            // This is a property of the actual control edge. On each entry
            // into the successor, that same execution published its result.
            return new[] { observer }.Concat(observer.GetTerminator().Successors
                .Where(next => next != observer && next.Users.OfType<LlvmInstruction>()
                    .All(use => !use.IsTerminator || use.Parent == observer))
                .Distinct());
        }

        private LlvmCallInstruction? BlockTextProducer(LlvmValue value, LlvmBasicBlock producer, LlvmBasicBlock consumer)
        {
            // The private single store publishes the same completion in the
            // producer block, not a standing guarantee about a mutable slot.
            bool entry = producer == producer.Parent!.BasicBlocks[0];
            while (true)
            {
                if (value is LlvmCallInstruction { CalledFunction: { } target } call)
                {
                    if (nativeCalls.GetValueOrDefault(target) == "io.read_line" || ReturnsLineCompletion(target))
                        return call.Parent == producer ? call : null;
                    if (runtimeConversions.GetValueOrDefault(target)?.Operation != LlvmRuntimeConversionOperation.Represent)
                        return null;
                    value = call.GetArgument(0);
                    continue;
                }
                if (value is not LlvmLoadInstruction { PointerOperand: LlvmAllocaInstruction address } load
                    || !entry && load.Parent != producer && load.Parent != consumer
                    || SingleProducerStore(address, producer) is not { } store
                    || load.Parent == producer && !store.ComesBefore(load)) return null;
                value = store.ValueOperand;
            }
        }

        private bool ReturnsLineCompletion(LlvmFunction target)
        {
            if (target.IsDeclaration || !sourceFunctions.TryGetValue(target, out var source)
                || (metadata.Types[source.ResultType].Kind & ~TypeKind.Null) != TypeKind.String) return false;
            bool seen = false;
            foreach (var block in target.BasicBlocks)
            {
                if (block.LastInstruction is not LlvmReturnInstruction result) continue;
                if (result.ReturnValue is not LlvmCallInstruction { CalledFunction: { } read }
                    || nativeCalls.GetValueOrDefault(read) != "io.read_line") return false;
                seen = true;
            }
            return seen;
        }

        private static LlvmStoreInstruction? SingleProducerStore(LlvmAllocaInstruction address, LlvmBasicBlock producer)
        {
            LlvmStoreInstruction? store = null;
            foreach (var user in address.Users)
            {
                if (user is LlvmLoadInstruction load && load.PointerOperand == address) continue;
                if (user is not LlvmStoreInstruction write || write.PointerOperand != address
                    || write.ValueOperand == address || write.Parent != producer || store is not null) return null;
                store = write;
            }
            return store;
        }
    }
}
