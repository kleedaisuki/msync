using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private bool RuntimeBuiltinCall(LlvmCallInstruction call, LlvmFunction target)
        {
            if (owner.Input.Constructors.TryGetValue(target, out NativeKind kind))
            {
                if (kind != NativeKind.Range) throw Unsupported(call, "builtin constructor " + kind);
                EmitBuiltinBinding(call, "constructor.range", true);
                return true;
            }
            if (!owner.Input.NativeCalls.TryGetValue(target, out string? name)) return false;
            EmitBuiltinBinding(call, name, false);
            return true;
        }

        private void EmitBuiltinBinding(LlvmCallInstruction call, string name, bool constructor)
        {
            if (trace is null) { EmitBuiltin(call, name, constructor); return; }
            var attributes = FunctionAttributes(function.Name, functionIndex, call.Source);
            attributes["builtin.name"] = name;
            attributes["binding.kind"] = constructor ? "constructor" : "native";
            if (!constructor)
            {
                var signature = NativeSignatures.Get(name);
                attributes["builtin.effect"] = signature.Effect.ToString();
                attributes["builtin.behavior"] = signature.Behavior.GetValueOrDefault().ToString();
                attributes["builtin.result_rule"] = signature.ResultRule.ToString();
            }
            trace.Run("compiler.llvm.target.builtin-binding", (Body: this, call, name, constructor),
                static state => state.Body.EmitBuiltin(state.call, state.name, state.constructor), attributes);
        }

        private void EmitBuiltin(LlvmCallInstruction call, string name, bool constructor)
        {
            var arguments = ArgumentSlots(call);
            Add(constructor
                ? new(NativeOp.MakeRange, Slot(call), Type: owner.Type(call), Inputs: arguments, Source: call.Source)
                : new(NativeOp.CallNative, Slot(call), Type: owner.Type(call), Symbol: name,
                    Inputs: arguments, Source: call.Source));
            trace?.Metric("arguments", arguments.Length);
            trace?.Metric("result.type", owner.Type(call));
            int element = owner.Types[owner.Type(call)].Element;
            if (element >= 0) trace?.Metric("result.element_type", element);
        }
    }
}
