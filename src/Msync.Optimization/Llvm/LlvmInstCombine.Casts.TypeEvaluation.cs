// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp, TypeEvaluationHelper.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private sealed partial class TypeEvaluationHelper(uint maximumDepth, Dictionary<string, ulong>? metrics = null)
        {
            private uint depth;
            private readonly Dictionary<LlvmValue, bool> visited = new();
            private readonly List<LlvmValue> pending = new();
            private void CountTypeEvaluation(string key)
            { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
            private bool AllPendingVisited()
            {
                foreach (LlvmValue value in pending)
                {
                    CountTypeEvaluation("casts.type_evaluation.pending.checked");
                    if (!visited.ContainsKey(value)) { CountTypeEvaluation("casts.type_evaluation.pending.unvisited"); return false; }
                }
                return true;
            }
            private static bool CanAlwaysEvaluateInType(LlvmValue value, LlvmType type) =>
                value is LlvmConstantData || value is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt or LlvmOpcode.Trunc } cast && cast.Operand.Type == type;
            private static bool CanNotEvaluateInType(LlvmValue value) => value is not LlvmInstruction || !value.HasOneUse;
            private bool CanEvaluate(LlvmValue value, LlvmType type, bool truncate, Work? work = null, LlvmInstruction? context = null)
            {
                CountTypeEvaluation("casts.type_evaluation.visits");
                if (depth >= maximumDepth) { CountTypeEvaluation("casts.type_evaluation.depth_limit"); return false; }
                if (CanAlwaysEvaluateInType(value, type)) { CountTypeEvaluation("casts.type_evaluation.free_leaf"); return true; }
                if (value is not LlvmInstruction instruction) return false;
                if (visited.TryGetValue(value, out bool cached)) { CountTypeEvaluation("casts.type_evaluation.cached"); return cached; }
                visited.Add(value, false);
                if (!instruction.HasOneUse)
                    foreach (var use in instruction.Uses)
                    {
                        if (use.User is LlvmSelectInstruction && use.OperandNo == 0) return false;
                        pending.Add(use.User);
                        CountTypeEvaluation("casts.type_evaluation.pending.added");
                    }
                depth++;
                bool result = truncate ? CanEvaluateTruncatedPred(value, type, work!, context!) : CanEvaluateSExtdPred(value, type);
                depth--;
                visited[value] = result;
                CountTypeEvaluation(result ? "casts.type_evaluation.accepted" : "casts.type_evaluation.rejected");
                return result;
            }
            internal static bool CanEvaluateTruncated(LlvmValue value, LlvmType type, Work work, LlvmInstruction context)
            {
                work.Count("casts.type_evaluation.truncate.queries");
                var helper = new TypeEvaluationHelper(work.CLOpts.MaxTypeEvalDepth, work.Metrics);
                return helper.CanEvaluateTruncatedImpl(value, type, work, context) && helper.AllPendingVisited();
            }
            private bool CanEvaluateTruncatedImpl(LlvmValue value, LlvmType type, Work work, LlvmInstruction context) =>
                CanEvaluate(value, type, true, work, context);
            internal static bool CanEvaluateSExtd(LlvmValue value, LlvmType type, uint maximumDepth, Dictionary<string, ulong>? metrics = null)
            {
                var helper = new TypeEvaluationHelper(maximumDepth, metrics);
                return helper.CanEvaluateSExtdImpl(value, type) && helper.AllPendingVisited();
            }
            private bool CanEvaluateSExtdImpl(LlvmValue value, LlvmType type) => CanEvaluate(value, type, false);
            private bool CanEvaluateSExtdPred(LlvmValue value, LlvmType type)
            {
                var instruction = (LlvmInstruction)value;
                return instruction switch
                {
                    LlvmCastInstruction { Opcode: LlvmOpcode.SExt or LlvmOpcode.ZExt or LlvmOpcode.Trunc } => true,
                    LlvmBinaryOperator binary when binary.Opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor or LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul =>
                        CanEvaluateSExtdImpl(binary.Left, type) && CanEvaluateSExtdImpl(binary.Right, type),
                    LlvmSelectInstruction select => CanEvaluateSExtdImpl(select.TrueValue, type) && CanEvaluateSExtdImpl(select.FalseValue, type),
                    LlvmPhiInstruction phi => CanEvaluateSExtdPhi(phi, type),
                    _ => false
                };
            }
            private bool CanEvaluateSExtdPhi(LlvmPhiInstruction phi, LlvmType type)
            {
                for (int index = 0; index < phi.IncomingCount; ++index)
                    if (!CanEvaluateSExtdImpl(phi.GetIncomingValue(index), type)) return false;
                return true;
            }
            internal static bool CanEvaluateZExtd(LlvmValue value, LlvmType type, out uint bitsToClear, Work work, LlvmInstruction context)
            {
                work.Count("casts.type_evaluation.zext.queries");
                return new TypeEvaluationHelper(work.CLOpts.MaxTypeEvalDepth, work.Metrics).CanEvaluateZExtdImpl(value, type, out bitsToClear, work, context);
            }
            private bool CanEvaluateZExtdImpl(LlvmValue value, LlvmType type, out uint bitsToClear, Work work, LlvmInstruction context)
            {
                bitsToClear = 0;
                CountTypeEvaluation("casts.type_evaluation.zext.visits");
                if (depth >= maximumDepth) { CountTypeEvaluation("casts.type_evaluation.depth_limit"); return false; }
                if (CanAlwaysEvaluateInType(value, type)) return true;
                if (CanNotEvaluateInType(value)) { CountTypeEvaluation("casts.type_evaluation.zext.not_single_use"); return false; }
                depth++;
                bool result = CanEvaluateZExtdPred(value, type, out bitsToClear, work, context);
                depth--;
                return result;
            }
            private bool CanEvaluateZExtdPred(LlvmValue value, LlvmType type, out uint bitsToClear, Work work, LlvmInstruction context)
            {
                bitsToClear = 0;
                switch (value)
                {
                    case LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt or LlvmOpcode.Trunc }: return true;
                    case LlvmBinaryOperator binary when binary.Opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor or LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul:
                        if (!CanEvaluateZExtdImpl(binary.Left, type, out bitsToClear, work, context) ||
                            !CanEvaluateZExtdImpl(binary.Right, type, out uint other, work, context)) return false;
                        if (bitsToClear == 0 && other == 0) return true;
                        if (other != 0 || binary.Opcode is not (LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor)) return false;
                        if (!work.MaskedValueIsZero(binary.Right, LlvmAPInt.GetHighBitsSet(value.Type.GetScalarSizeInBits(), bitsToClear), context)) return false;
                        if (binary.Opcode == LlvmOpcode.And) bitsToClear = 0;
                        return true;
                    case LlvmBinaryOperator binary when binary.Opcode is LlvmOpcode.Shl or LlvmOpcode.LShr && binary.Right is LlvmConstantInt:
                        var amount = ((LlvmConstantInt)binary.Right).Value;
                        if (amount.GetActiveBits() > 64) return false;
                        if (!CanEvaluateZExtdImpl(binary.Left, type, out bitsToClear, work, context)) return false;
                        ulong shift = amount.GetZExtValue();
                        bitsToClear = binary.Opcode == LlvmOpcode.Shl ? shift < bitsToClear ? bitsToClear - (uint)shift : 0 :
                            Math.Min(unchecked(bitsToClear + (uint)shift), value.Type.GetScalarSizeInBits());
                        return true;
                    case LlvmSelectInstruction select:
                        return CanEvaluateZExtdImpl(select.TrueValue, type, out uint trueBits, work, context) &&
                            CanEvaluateZExtdImpl(select.FalseValue, type, out bitsToClear, work, context) && trueBits == bitsToClear;
                    case LlvmPhiInstruction phi:
                        if (!CanEvaluateZExtdImpl(phi.GetIncomingValue(0), type, out bitsToClear, work, context)) return false;
                        for (int index = 1; index < phi.IncomingCount; index++)
                            if (!CanEvaluateZExtdImpl(phi.GetIncomingValue(index), type, out uint incoming, work, context) || incoming != bitsToClear) return false;
                        return true;
                    case LlvmCallBase call when call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.VScale: return true;
                    default: return false;
                }
            }
        }
    }
}
