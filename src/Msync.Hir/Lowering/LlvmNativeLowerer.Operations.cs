using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private bool RuntimeBinaryCall(LlvmCallInstruction call, LlvmFunction target)
        {
            if (!owner.Input.RuntimeCalls.TryGetValue(target, out var operation)) return false;
            if (trace is null) EmitRuntimeBinary(call, operation);
            else
            {
                var attributes = FunctionAttributes(function.Name, functionIndex, call.Source);
                attributes["operation.name"] = operation.ToString();
                trace.Run("compiler.llvm.target.value-binary", (Body: this, call, operation),
                    static state => state.Body.EmitRuntimeBinary(state.call, state.operation), attributes);
            }
            return true;
        }
        private void EmitRuntimeBinary(LlvmCallInstruction call, NativeBinary operation)
        {
            Add(new(NativeOp.Binary, Slot(call), ArgumentSlot(call, 0), ArgumentSlot(call, 1),
                Immediate: (int)operation, Source: call.Source));
            trace?.Metric("operations.binary", 1);
            trace?.Metric("operations.result_type", owner.Type(call));
        }
        private bool RuntimeOperationCall(LlvmCallInstruction call, LlvmFunction target)
        {
            if (!owner.Input.ValueOperations.TryGetValue(target, out var binding)) return false;
            if (trace is null) EmitOperation(call, binding);
            else
            {
                var attributes = FunctionAttributes(function.Name, functionIndex, call.Source);
                attributes["operation.name"] = binding.Operation.ToString();
                attributes["operation.mode"] = binding.Mode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (binding.Symbol is not null) attributes["operation.symbol"] = binding.Symbol;
                trace.Run("compiler.llvm.target.value-operation", (Body: this, call, binding),
                    static state => state.Body.EmitOperation(state.call, state.binding), attributes);
            }
            return true;
        }
        private void EmitOperation(LlvmCallInstruction call, LlvmRuntimeValueBinding binding)
        {
            switch (binding.Operation)
            {
                case LlvmRuntimeValueOperation.SelectPath:
                    Add(new(NativeOp.SelectPath, Slot(call), ArgumentSlot(call, 0), Symbol: binding.Symbol, Source: call.Source));
                    break;
                case LlvmRuntimeValueOperation.Barrier:
                    Add(new(NativeOp.Barrier, A: (int)BarrierDirection.Release, B: binding.Mode, Symbol: binding.Symbol, Source: call.Source));
                    break;
                case LlvmRuntimeValueOperation.Acquire:
                    Add(call.ArgumentCount == 0
                        ? new(NativeOp.Acquire, A: -1, Source: call.Source)
                        : new(NativeOp.Acquire, Slot(call), ArgumentSlot(call, 0), Source: call.Source));
                    break;
                case LlvmRuntimeValueOperation.HasLayout:
                    Add(new(NativeOp.Unary, Slot(call), ArgumentSlot(call, 0), binding.Mode,
                        Immediate: (int)NativeUnary.HasLayout, Source: call.Source));
                    break;
                case LlvmRuntimeValueOperation.ProcessCall:
                    Add(new(NativeOp.ProcessCall, Slot(call), ArgumentSlot(call, 0), binding.Mode,
                        Inputs: ArgumentSlots(call, 1), Source: call.Source));
                    break;
                case LlvmRuntimeValueOperation.DemandBool:
                    DemandBool(call);
                    break;
            }
            trace?.Metric("operations.arguments", call.ArgumentCount);
            trace?.Metric("operations.result_type", owner.Type(call));
        }

        private readonly List<int> boolDemandContinuations = [];

        private void DemandBool(LlvmCallInstruction call)
        {
            if (call.GetArgument(0) is LlvmConstantInt)
            { trace?.Metric("operations.demand_bool.constant", 1); return; }
            if (call.Next is LlvmCondBrInstruction branch && ReferenceEquals(branch.Condition, call.GetArgument(0)))
            { trace?.Metric("operations.demand_bool.fused_branch", 1); return; }
            // Native Branch already performs the language's Bool demand. Equal
            // continuations retain that effect when the LLVM branch is gone.
            boolDemandContinuations.Add(code.Count);
            Add(new(NativeOp.Branch, A: ArgumentSlot(call, 0), Source: call.Source));
            trace?.Metric("operations.demand_bool.continuation", 1);
        }
    }
}
