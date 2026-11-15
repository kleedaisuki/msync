using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private bool RuntimeConversionCall(LlvmCallInstruction call, LlvmFunction target)
        {
            if (!owner.Input.RuntimeConversions.TryGetValue(target, out var binding)) return false;
            if (trace is null) EmitConversion(call, binding);
            else
            {
                var attributes = FunctionAttributes(function.Name, functionIndex, call.Source);
                attributes["conversion.operation"] = binding.Operation.ToString();
                attributes["conversion.target_type"] = binding.TargetType.ToString(System.Globalization.CultureInfo.InvariantCulture);
                trace.Run("compiler.llvm.target.value-conversion", (Body: this, call, binding),
                    static state => state.Body.EmitConversion(state.call, state.binding), attributes);
            }
            return true;
        }
        private void EmitConversion(LlvmCallInstruction call, LlvmRuntimeConversionBinding binding)
        {
            int result = Slot(call), input = ArgumentSlot(call, 0);
            var instruction = binding.Operation switch
            {
                // Native2's own slot encoder handles mono/union payload movement.
                // This is representation movement, not a runtime type admission.
                LlvmRuntimeConversionOperation.Represent => new NativeInstruction(NativeOp.Copy, result, input,
                    Source: call.Source),
                LlvmRuntimeConversionOperation.Coerce or LlvmRuntimeConversionOperation.Convert =>
                    new(NativeOp.Convert, result, input, Type: binding.TargetType,
                        Immediate: binding.Operation == LlvmRuntimeConversionOperation.Convert ? 1 : 0, Source: call.Source),
                _ => new(NativeOp.Unary, result, input,
                    B: binding.Operation == LlvmRuntimeConversionOperation.IsType ? ArgumentSlot(call, 1) : -1,
                    Immediate: (int)(binding.Operation switch
                    {
                        LlvmRuntimeConversionOperation.IsNull => NativeUnary.IsNull,
                        LlvmRuntimeConversionOperation.IsType => NativeUnary.IsType,
                        _ => NativeUnary.ValueType
                    }), Source: call.Source)
            };
            Add(instruction);
            trace?.Metric("arguments", call.ArgumentCount);
            trace?.Metric("result.type", owner.Type(call));
        }
    }
}
