using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Body
    {
        private int sessionNativeSites, sessionNativeArms;

        private LlvmValue SessionNativeCall(HirNode node, LlvmValue descriptor, IReadOnlyList<LlvmValue> arguments,
            LlvmSessionDispatchInfo selected)
        {
            LlvmValue code = Add(new LlvmExtractValueInstruction(descriptor, [0]));
            var done = new LlvmBasicBlock(owner.context, "session.call.done", function);
            List<(LlvmValue Value, LlvmBasicBlock Block)> incoming = [];
            foreach (var native in selected.NativeTargets)
            {
                var arm = new LlvmBasicBlock(owner.context, "session.call.native", function);
                var next = new LlvmBasicBlock(owner.context, "session.call.next", function);
                var match = Add(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, code, owner.NativeCode(native.Name)));
                Add(new LlvmCondBrInstruction(match, arm, next));
                block = arm;
                Join(EmitSessionNativeArm(node, native, arguments));
                block = next;
            }
            if (selected.Targets.Count != 0)
                Join(CallableRuntime(LlvmCallableRuntimeOperation.Invoke, node, descriptor, arguments, [], selected.Slot));
            else
            {
                OperationCall(new(LlvmRuntimeValueOperation.FailMember, Symbol: "Function is outside the admitted HPIR call targets."),
                    LlvmType.GetVoidTy(owner.context), owner.metadata.RuntimeType(TypeRef.Null), []);
                Add(new LlvmUnreachableInstruction(owner.context));
            }

            block = done;
            current = node;
            ++sessionNativeSites;
            sessionNativeArms += selected.NativeTargets.Count;
            if (incoming.Count == 1) return incoming[0].Value;
            var result = Add(new LlvmPhiInstruction(owner.Type(node.Type), incoming.Count));
            foreach (var arm in incoming) result.AddIncoming(arm.Value, arm.Block);
            return result;

            void Join(LlvmValue value)
            {
                incoming.Add((Represent(value, node.Type), block));
                Add(new LlvmUncondBrInstruction(done));
            }
        }

        private LlvmValue EmitSessionNativeArm(HirNode node, HirSessionNativeTarget native, IReadOnlyList<LlvmValue> arguments)
        {
            var actual = new LlvmValue[arguments.Count];
            for (int index = 0; index < arguments.Count; ++index)
            {
                var abi = native.Arguments[index];
                LlvmValue argument = arguments[index];
                LlvmType type = owner.Type(abi.Type);
                if (!abi.Coerce || ReferenceEquals(argument.Type, type)) actual[index] = Represent(argument, abi.Type);
                else
                {
                    int target = owner.metadata.Type(abi.Type);
                    actual[index] = ConversionCall(LlvmRuntimeConversionOperation.Coerce, type, target, [argument], target);
                }
            }
            return EmitNativeInvocation(node with { Type = native.Result }, native.Name, actual);
        }
    }
}
