using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private int resultPointer = -1;
        private int guestCalls, importCalls, sessionReturnDemands;

        private HpirWidth Result => geometry.FutureResult(function) || geometry.TextResult(function) ? HpirWidth.U64
            : Aggregate(function.ReturnType) ? HpirWidth.Void : Width(function.ReturnType);

        private HpirParameter[] Parameters()
        {
            var parameters = new List<HpirParameter>();
            if (geometry.MixedTextResult(function))
            {
                textResultPointer = Reserve(8, 8);
                parameters.Add(new(textResultPointer, HpirWidth.U64));
            }
            if (geometry.FutureResult(function)
                || !geometry.TextResult(function) && (!geometry.IsEntry(function) || geometry.IsPersistentSession)
                    && Aggregate(function.ReturnType) && geometry.Leaves(function.ReturnType).Length != 0)
            {
                resultPointer = Reserve(8, 8);
                parameters.Add(new(resultPointer, HpirWidth.U64));
            }
            foreach (var argument in function.Arguments)
            {
                if (geometry.TextParameter(argument))
                {
                    parameters.Add(new(Location(argument), HpirWidth.U64));
                    if (geometry.MixedTextResult(argument)) parameters.Add(new(TextTag(argument), HpirWidth.U8));
                }
                else if (geometry.FutureParameter(argument))
                {
                    parameters.Add(new(Location(argument), HpirWidth.U64));
                    parameters.Add(new(Payload(argument), Width(argument.Type)));
                }
                else if (Aggregate(argument.Type))
                {
                    foreach (var leaf in geometry.Leaves(argument.Type))
                        parameters.Add(new(LeafLocation(argument, leaf.Offset), Width(leaf.Type)));
                    if (geometry.FutureUnionParameter(argument))
                        parameters.Add(new(UnionCompletion(argument), HpirWidth.U64));
                }
                else if (Width(argument.Type) is { } width && width != HpirWidth.Void)
                    parameters.Add(new(Location(argument), width));
            }
            return parameters.ToArray();
        }

        private void Return(LlvmReturnInstruction ret)
        {
            if (geometry.TextResult(function))
            {
                var value = ret.ReturnValue!;
                if (textResultPointer >= 0)
                    Copy(HpirWidth.U8, ~textResultPointer, TextTag(value), ret.Source);
                var textRoot = TextCell(value) && !geometry.MixedTextResult(value)
                    ? HpirReturnRoot.Exact : HpirReturnRoot.Borrowed;
                Add(new(HpirOp.Return, A: Location(value), Immediate: (long)textRoot), ret.Source);
                ++textReturns;
                return;
            }
            if (ret.ReturnValue is { } text && TextCell(text) && geometry.SessionTextEntry(function))
            {
                Add(new(HpirOp.Return, A: ObserveText(ret, text)), ret.Source);
                ++sessionReturnDemands;
                return;
            }
            if (geometry.FutureResult(function))
            {
                var answer = ret.ReturnValue!;
                HpirFutureInput mode = FutureInput(answer);
                if (mode != HpirFutureInput.Deferred)
                    Copy(Width(answer.Type), Indirect(resultPointer, 0, ret),
                        mode == HpirFutureInput.Either ? Payload(answer) : Location(answer), ret.Source);
                Add(new(HpirOp.Return, A: mode == HpirFutureInput.Ready ? absentFuture : Location(answer),
                    Immediate: (long)HpirReturnRoot.Exact), ret.Source);
                return;
            }
            if (ret.ReturnValue is { } cell && FutureCell(cell))
            {
                int result = Reserve((int)Result, (int)Result);
                Demand(ret, cell, result);
                Add(new(HpirOp.Return, A: result), ret.Source);
                if (geometry.IsPersistentSession && geometry.IsEntry(function)) ++sessionReturnDemands;
                return;
            }
            if (resultPointer >= 0)
            {
                var value = ret.ReturnValue!;
                int scalar = geometry.IsPersistentSession && geometry.IsEntry(function) && UnionCell(value)
                    ? ObserveSessionUnion(ret, value) : -1;
                if (scalar >= 0) ++sessionReturnDemands;
                foreach (var leaf in geometry.Leaves(value.Type))
                    Copy(Width(leaf.Type), Indirect(resultPointer, leaf.Offset, ret),
                        scalar >= 0 && leaf.Offset == geometry.FieldOffset(value.Type, 1) ? scalar : LeafLocation(value, leaf.Offset), ret.Source);
                if (geometry.FutureUnionResult(function))
                    Copy(HpirWidth.U64, Indirect(resultPointer, UnionTail(value.Type), ret), UnionCompletion(value), ret.Source);
                if (Geometry.IsPathRecord(value.Type))
                {
                    Add(new(HpirOp.Return, A: resultPointer,
                        Immediate: (long)HpirReturnRoot.AggregateLayout + geometry.PathBatchStorage()), ret.Source);
                    return;
                }
            }
            HpirReturnRoot root = processHandles?.Contains(ret.ReturnValue!) == true ? HpirReturnRoot.Exact
                : geometry.IsGuestPointer(function.ReturnType) ? HpirReturnRoot.Borrowed : HpirReturnRoot.None;
            Add(new(HpirOp.Return, A: Result == HpirWidth.Void ? -1 : Location(ret.ReturnValue!),
                Immediate: (long)root), ret.Source);
        }

        private void Call(LlvmCallInstruction call, int target) => Call(call, target, call.Arguments, [], call.CalledFunction);

        private void Call(LlvmCallInstruction call, int target, IEnumerable<LlvmValue> incoming, int[] captured,
            LlvmFunction? abi = null)
        {
            var arguments = new List<int>();
            int result = -1;
            bool tail = IsSelfTail(call);
            bool mixed = geometry.MixedTextResult(call);
            bool mixedTarget = abi is not null && geometry.MixedTextResult(abi);
            if (mixedTarget)
            {
                int address = Reserve(8, 8);
                Add(new(HpirOp.AddressOf, address, TextTag(call)), call.Source);
                arguments.Add(address);
            }
            if (abi is not null && geometry.FutureResult(abi))
            {
                int address = Reserve(8, 8);
                Add(new(HpirOp.AddressOf, address, Payload(call)), call.Source);
                arguments.Add(address);
                result = Location(call);
            }
            else if (abi is not null && geometry.TextResult(abi))
                result = Location(call);
            else if (Aggregate(call.Type))
            {
                if (geometry.Leaves(call.Type).Length != 0)
                {
                    int buffer = RecordLocation(call);
                    int pointer = Reserve(8, 8);
                    Add(new(HpirOp.AddressOf, pointer, buffer), call.Source);
                    arguments.Add(pointer);
                }
            }
            else if (!tail && Width(call.Type) != HpirWidth.Void) result = Location(call);
            arguments.AddRange(captured);
            int index = captured.Length;
            foreach (var argument in incoming)
            {
                if (abi is not null && geometry.TextParameter(abi.Arguments[index]))
                {
                    arguments.Add(Location(argument));
                    if (geometry.MixedTextResult(abi.Arguments[index])) arguments.Add(TextTag(argument));
                }
                else if (abi is not null && geometry.FutureParameter(abi.Arguments[index]))
                {
                    HpirFutureInput mode = FutureInput(argument);
                    arguments.Add(mode == HpirFutureInput.Ready ? absentFuture : Location(argument));
                    arguments.Add(mode switch
                    {
                        HpirFutureInput.Either => Payload(argument),
                        HpirFutureInput.Deferred => absentFuture,
                        _ => Location(argument)
                    });
                }
                else if (Aggregate(argument.Type))
                {
                    foreach (var leaf in geometry.Leaves(argument.Type)) arguments.Add(LeafLocation(argument, leaf.Offset));
                    if (abi is not null && geometry.FutureUnionParameter(abi.Arguments[index]))
                        arguments.Add(UnionCompletion(argument));
                }
                else if (Width(argument.Type) != HpirWidth.Void) arguments.Add(Location(argument));
                index++;
            }
            if (tail) EmitSelfTail(call, arguments);
            else EmitCall(call, target, result, arguments.ToArray());
            if (mixed)
            {
                if (!mixedTarget)
                    Copy(HpirWidth.U8, TextTag(call), abi is not null && geometry.TextResult(abi) ? pendingTextTag : readyTextTag, call.Source);
                ++mixedTextCalls;
            }
        }

        private void EmitFailure(LlvmInstruction site, HpirFailure failure) =>
            EmitCall(site, ~geometry.FailureImport(failure), -1, [], safepoint: false);

        private void EmitFailure(LlvmInstruction site, string message) =>
            EmitCall(site, ~geometry.FailureImport(message), -1, [], safepoint: false);

        private void EmitImportCall(LlvmCallInstruction call, int importIndex, int[] arguments, bool safepoint = true)
        {
            int result = Width(call.Type) == HpirWidth.Void ? -1 : Location(call);
            EmitCall(call, ~importIndex, result, arguments, safepoint);
        }

        private void EmitCall(LlvmInstruction origin, int target, int destination, int[] arguments, bool safepoint = true)
        {
            int site = calls.Count;
            if (target < 0) ++importCalls; else ++guestCalls;
            calls.Add(new(target, arguments));
            if (safepoint) safepointInstructions.Add((code.Count, origin));
            Add(new(HpirOp.Call, destination, site), origin.Source);
        }
    }
}
