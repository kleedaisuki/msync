using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

internal static class LlvmSsaBody
{
    internal static NativeSsaBody? Select(LlvmHirLoweringResult graph, LlvmFunction function, NativeFunctionCode linked,
        IReadOnlyList<NativeCodeType> types, IReadOnlyDictionary<LlvmFunction, int> functions, CompilationTrace? trace)
    {
        NativeKind? result = Storage(types[linked.Signature.Result]);
        if (result is null || function.BasicBlocks.Count is 0 or > NativeSsaBody.MaxBlocks) return Reject("signature");
        Dictionary<LlvmValue, int> ids = [];
        for (int i = 0; i < function.Arguments.Count; i++)
        {
            LlvmArgument argument = function.Arguments[i];
            if (i < linked.CaptureCount)
            {
                if (argument.Users.Any()) return Reject("capture");
                continue;
            }
            int parameter = i - linked.CaptureCount;
            if (Storage(types[linked.Signature.Parameters[parameter]]) is not { } kind || Kind(argument) != kind)
                return Reject("parameter");
            ids.Add(argument, ids.Count);
        }
        List<NativeSsaLiteral> literals = [];
        LlvmBasicBlock[] blocks = function.BasicBlocks.ToArray();
        Dictionary<LlvmBasicBlock, int> labels = new(blocks.Length);
        for (int i = 0; i < blocks.Length; i++) labels.Add(blocks[i], i);
        foreach (LlvmBasicBlock block in blocks)
        foreach (LlvmInstruction instruction in block.Instructions)
        {
            if (instruction.IsTerminator || IsReadyDemand(instruction)) continue;
            if (Kind(instruction) is null || Form(instruction).Op == LlvmOpcode.Call && DirectTarget(instruction) < 0)
                return Reject(instruction is LlvmCallInstruction call ? "call." + call.CalledFunction?.Name : instruction.Opcode.ToString());
            ids.Add(instruction, ids.Count);
        }
        NativeSsaBlock[] plans = new NativeSsaBlock[blocks.Length];
        bool valid = true;
        for (int index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            List<NativeSsaInstruction> ops = [];
            foreach (LlvmInstruction instruction in block.Instructions)
            {
                if (instruction.IsTerminator || instruction is LlvmPhiInstruction) continue;
                if (IsReadyDemand(instruction))
                {
                    Value(((LlvmCallInstruction)instruction).GetArgument(0));
                    trace?.Metric("ssa.ready_demands_elided", 1);
                    continue;
                }
                var form = Form(instruction);
                if (form.Op == LlvmOpcode.Call && instruction is LlvmCallInstruction direct)
                {
                    int[] args = new int[direct.ArgumentCount];
                    for (int i = 0; i < args.Length; i++) args[i] = Value(direct.GetArgument(i));
                    ops.Add(new(LlvmOpcode.Call, ids[instruction], -1, -1, Target: DirectTarget(direct), Arguments: args));
                    continue;
                }
                var (a, b, c) = instruction switch
                {
                    LlvmSelectInstruction choice => (Value(choice.Condition), Value(choice.TrueValue), Value(choice.FalseValue)),
                    LlvmCallInstruction call => (Value(call.GetArgument(0)), form.Op == LlvmOpcode.Freeze ? -1 : Value(call.GetArgument(1)), -1),
                    _ => (Value(instruction.GetOperand(0)!), form.Op is LlvmOpcode.FNeg or LlvmOpcode.Freeze ? -1 : Value(instruction.GetOperand(1)!), -1)
                };
                ops.Add(new(form.Op, ids[instruction], a, b, c, form.Predicate));
            }
            plans[index] = block.GetTerminator() switch
            {
                LlvmReturnInstruction { ReturnValue: { } value } when Kind(value) == result =>
                    new(ops.ToArray(), Value(value)),
                LlvmUncondBrInstruction jump => new(ops.ToArray(), -1, Edge(block, jump.Destination)),
                LlvmCondBrInstruction branch when branch.GetSuccessor(0) == branch.GetSuccessor(1) =>
                    new(ops.ToArray(), -1, Edge(block, branch.GetSuccessor(0))),
                LlvmCondBrInstruction branch => new(ops.ToArray(), Value(branch.Condition),
                    Edge(block, branch.GetSuccessor(0)), Edge(block, branch.GetSuccessor(1))),
                _ => null!
            };
            if (plans[index] is null || !valid || ids.Count > NativeSsaBody.MaxValues) return Reject("control_or_value");
        }
        trace?.Metric("ssa.whole_functions", 1);
        trace?.Metric("ssa.whole_blocks", plans.Length);
        trace?.Metric("ssa.whole_values", ids.Count);
        NativeKind[] kinds = new NativeKind[ids.Count];
        foreach (var (value, index) in ids) kinds[index] = Kind(value)!.Value;
        return new(kinds, linked.ParameterCount, result.Value, literals.ToArray(), plans);

        NativeSsaBody? Reject(string reason)
        {
            trace?.Metric("ssa.rejected." + reason, 1);
            return null;
        }

        // This body has no producer of a future and admits only ready inputs.
        bool IsReadyDemand(LlvmInstruction instruction) =>
            instruction is LlvmCallInstruction { CalledFunction: { } target, ArgumentCount: 1 } call
            && Kind(call.GetArgument(0)) == NativeKind.Bool
            && graph.ValueOperations.TryGetValue(target, out var value) && value.Operation == LlvmRuntimeValueOperation.DemandBool;

        int DirectTarget(LlvmInstruction instruction)
        {
            if (instruction is not LlvmCallInstruction { CalledFunction: { } target } call
                || !graph.SourceFunctions.TryGetValue(target, out var source) || source.CaptureCount != 0
                || call.GetOperandBundle("msync.callee") is not { InputCount: 1 } bundle
                || bundle.GetInput(0) is not LlvmConstantStruct descriptor
                || !ReferenceEquals(descriptor.GetAggregateElement(0), target)
                || descriptor.GetAggregateElement(1) is not LlvmConstantPointerNull
                || descriptor.GetAggregateElement(2) is not LlvmConstantPointerNull) return -1;
            return functions.GetValueOrDefault(target, -1);
        }

        (LlvmOpcode Op, LlvmCmpPredicate Predicate) Form(LlvmInstruction instruction)
        {
            if (instruction is LlvmCallInstruction { CalledFunction: { } target } call)
            {
                if (call.ArgumentCount == 1 && call.Type == call.GetArgument(0).Type
                    && graph.RuntimeConversions.TryGetValue(target, out var conversion) && conversion.Operation == LlvmRuntimeConversionOperation.Represent)
                    return (LlvmOpcode.Freeze, default);
                if (call.ArgumentCount == 2 && Kind(call.GetArgument(0)) == NativeKind.Number
                    && Kind(call.GetArgument(1)) == NativeKind.Number && graph.RuntimeCalls.TryGetValue(target, out var binary))
                    return binary switch
                    {
                        NativeBinary.Add => (LlvmOpcode.FAdd, default),
                        NativeBinary.Subtract => (LlvmOpcode.FSub, default),
                        NativeBinary.Multiply => (LlvmOpcode.FMul, default),
                        NativeBinary.Divide => (LlvmOpcode.FDiv, default),
                        NativeBinary.Equal => (LlvmOpcode.FCmp, LlvmCmpPredicate.FCmpOeq),
                        NativeBinary.NotEqual => (LlvmOpcode.FCmp, LlvmCmpPredicate.FCmpUne),
                        NativeBinary.Less => (LlvmOpcode.FCmp, LlvmCmpPredicate.FCmpOlt),
                        NativeBinary.LessEqual => (LlvmOpcode.FCmp, LlvmCmpPredicate.FCmpOle),
                        NativeBinary.Greater => (LlvmOpcode.FCmp, LlvmCmpPredicate.FCmpOgt),
                        NativeBinary.GreaterEqual => (LlvmOpcode.FCmp, LlvmCmpPredicate.FCmpOge),
                        _ => (LlvmOpcode.Call, default)
                    };
            }
            return Supported(instruction) ? (instruction.Opcode, instruction is LlvmCmpInstruction cmp ? cmp.Predicate : default)
                : (LlvmOpcode.Call, default);
        }

        int Value(LlvmValue value)
        {
            if (ids.TryGetValue(value, out int index)) return index;
            ulong? bits = value switch
            {
                LlvmConstantInt integer when Kind(integer) == NativeKind.Bool => integer.IsZero ? 0UL : 1UL,
                LlvmConstantFP floating when Kind(floating) == NativeKind.Number => floating.Value.BitcastToAPInt().GetZExtValue(),
                _ => null
            };
            if (bits is null) { valid = false; return -1; }
            index = ids.Count;
            ids.Add(value, index);
            literals.Add(new(index, bits.Value));
            return index;
        }

        NativeSsaEdge Edge(LlvmBasicBlock source, LlvmBasicBlock target)
        {
            List<NativeSsaMove> moves = [];
            foreach (LlvmPhiInstruction phi in target.Instructions.TakeWhile(instruction => instruction is LlvmPhiInstruction))
            for (int i = 0; i < phi.IncomingCount; i++)
            {
                if (phi.GetIncomingBlock(i) != source) continue;
                int input = Value(phi.GetIncomingValue(i));
                if (input != ids[phi]) moves.Add(new(ids[phi], input));
                break;
            }
            return new(labels[target], moves.ToArray());
        }
    }

    private static bool Supported(LlvmInstruction instruction) => instruction.Opcode switch
    {
        LlvmOpcode.FAdd or LlvmOpcode.FSub or LlvmOpcode.FMul or LlvmOpcode.FDiv or LlvmOpcode.FNeg
            => Kind(instruction) == NativeKind.Number,
        LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor => Kind(instruction) == NativeKind.Bool,
        LlvmOpcode.FCmp => instruction is LlvmFCmpInstruction cmp && Kind(cmp.Left) == NativeKind.Number,
        LlvmOpcode.PHI or LlvmOpcode.Select or LlvmOpcode.Freeze => true,
        _ => false
    };

    private static NativeKind? Kind(LlvmValue value) => value.Type.IsDoubleTy() ? NativeKind.Number
        : value.Type.IsIntegerTy(1) ? NativeKind.Bool : null;

    private static NativeKind? Storage(NativeCodeType type) => type is { IsMachine: false, Pointee: < 0 }
        ? type.Kind switch { TypeKind.Number => NativeKind.Number, TypeKind.Bool => NativeKind.Bool, _ => null }
        : null;
}
