// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// FAddCombine. Values replace pointers to scoped C++ addend temporaries.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private struct FAddCombine(LlvmTransformBuilder builder, Dictionary<string, ulong>? metrics)
        {
            private LlvmInstruction instruction = null!;
            internal LlvmValue? Simplify(LlvmBinaryOperator operation)
            {
                Debug.Assert((operation.FastMathFlags & (LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoSignedZeros)) ==
                    (LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoSignedZeros));
                if (operation.Type.IsVectorTy()) return null;
                instruction = operation;
                uint count = FAddend.DrillValue(operation, out var first, out var second);
                FAddend first0 = default, first1 = default, second0 = default, second1 = default;
                uint firstCount = first.IsConstant ? 0 : first.Drill(out first0, out first1);
                uint secondCount = count != 2 || second.IsConstant ? 0 : second.Drill(out second0, out second1);
                if (firstCount != 0 && secondCount != 0)
                {
                    List<FAddend?> all = [first0, second0];
                    if (firstCount == 2) all.Add(first1);
                    if (secondCount == 2) all.Add(second1);
                    uint quota = operation.Left is not LlvmConstant && operation.Left.HasOneUse &&
                        operation.Right is not LlvmConstant && operation.Right.HasOneUse ? 2U : 1U;
                    if (SimplifyFAdd(all, quota) is { } folded) return folded;
                }
                if (count != 2) return first.Coefficient.IsOne ? first.Symbol : null;
                if (secondCount != 0)
                {
                    List<FAddend?> all = [first, second0];
                    if (secondCount == 2) all.Add(second1);
                    if (SimplifyFAdd(all, 1) is { } folded) return folded;
                }
                if (firstCount != 0)
                {
                    List<FAddend?> all = [second, first0];
                    if (firstCount == 2) all.Add(first1);
                    if (SimplifyFAdd(all, 1) is { } folded) return folded;
                }
                return null;
            }
            private LlvmValue? SimplifyFAdd(List<FAddend?> addends, uint quota)
            {
                List<FAddend> simplified = new(4);
                for (int index = 0; index < addends.Count; ++index)
                {
                    if (addends[index] is not { } first) continue;
                    int start = simplified.Count;
                    simplified.Add(first);
                    for (int other = index + 1; other < addends.Count; ++other)
                        if (addends[other] is { } next && ReferenceEquals(next.Symbol, first.Symbol))
                        { addends[other] = null; simplified.Add(next); }
                    if (start + 1 == simplified.Count) continue;
                    FAddend result = simplified[start];
                    for (int other = start + 1; other < simplified.Count; ++other) result.Add(simplified[other]);
                    simplified.RemoveRange(start, simplified.Count - start);
                    if (!result.IsZero) simplified.Add(result);
                    Count("coefficient_groups_folded");
                }
                return simplified.Count != 0 ? CreateNaryFAdd(simplified, quota) :
                    LlvmConstantFP.Get(instruction.Type, LlvmAPFloat.GetZero(instruction.Type.GetFltSemantics()));
            }
            private LlvmValue? CreateNaryFAdd(IReadOnlyList<FAddend> addends, uint quota)
            {
                uint needed = (uint)addends.Count - 1;
                foreach (var addend in addends)
                    if (!addend.IsConstant && addend.Symbol is not LlvmUndefValue &&
                        !addend.Coefficient.IsMinusOne && !addend.Coefficient.IsOne) ++needed;
                if (needed > quota) { Count("instruction_quota"); return null; }
                LlvmValue? last = null;
                bool lastNeedsNegation = false;
                foreach (var addend in addends)
                {
                    LlvmValue current = CreateAddend(addend, out bool needsNegation);
                    if (last is null) { last = current; lastNeedsNegation = needsNegation; continue; }
                    if (lastNeedsNegation == needsNegation) { last = Create(LlvmOpcode.FAdd, last, current); continue; }
                    last = lastNeedsNegation ? Create(LlvmOpcode.FSub, current, last) : Create(LlvmOpcode.FSub, last, current);
                    lastNeedsNegation = false;
                }
                return lastNeedsNegation ? CreateNeg(last!) : last;
            }
            private LlvmValue CreateAddend(FAddend addend, out bool needsNegation)
            {
                var coefficient = addend.Coefficient;
                needsNegation = false;
                if (addend.IsConstant) return coefficient.GetValue(instruction.Type);
                if (coefficient.IsOne || coefficient.IsMinusOne)
                { needsNegation = coefficient.IsMinusOne; return addend.Symbol!; }
                if (coefficient.IsTwo || coefficient.IsMinusTwo)
                { needsNegation = coefficient.IsMinusTwo; return Create(LlvmOpcode.FAdd, addend.Symbol!, addend.Symbol!); }
                return Create(LlvmOpcode.FMul, addend.Symbol!, coefficient.GetValue(instruction.Type));
            }
            private LlvmValue Create(LlvmOpcode opcode, LlvmValue left, LlvmValue right)
            {
                LlvmValue result = builder.CreateBinary(opcode, left, right);
                PostProcess(result);
                return result;
            }
            private LlvmValue CreateNeg(LlvmValue value)
            {
                LlvmValue result = builder.CreateFNeg(value);
                PostProcess(result);
                return result;
            }
            private void PostProcess(LlvmValue value)
            {
                if (value is not LlvmInstruction created) return;
                created.Source = instruction.Source;
                ((ILlvmFastMathInstruction)created).FastMathFlags = ((ILlvmFastMathInstruction)instruction).FastMathFlags;
                Count("instructions_created");
            }
            private readonly void Count(string suffix)
            {
                if (metrics is null) return;
                string key = "instcombine.fadd_combine." + suffix;
                metrics[key] = metrics.GetValueOrDefault(key) + 1;
            }
        }
    }
}
