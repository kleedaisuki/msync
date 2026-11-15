// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineNegator.cpp
// Negator constructor / negate / run / Negate and getSortedOperandsOfBinOp.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private sealed partial class Negator
        {
            private readonly Work owner;
            private readonly LlvmTransformBuilder builder;
            private readonly bool trulyNegating;
            private readonly uint maxDepth;
            private readonly List<LlvmInstruction> created = [];
            private readonly Dictionary<LlvmValue, LlvmValue?> cache = [];
#if DEBUG
            private readonly HashSet<LlvmValue> active = [];
#endif
            private Negator(Work owner, bool trulyNegating, uint maxDepth)
            {
                this.owner = owner; this.trulyNegating = trulyNegating; this.maxDepth = maxDepth;
                builder = new(owner.DL, owner.Metrics, instruction =>
                {
                    created.Add(instruction);
                    owner.Count("negator.instructions_created_total");
                }, targetFolder: new LlvmTargetFolder(owner.DL, owner.Metrics));
            }
            internal static LlvmValue? Negate(bool lhsIsZero, bool isNSW, LlvmValue root, Work owner)
            {
                if (owner.Trace is null) return NegateCore(lhsIsZero, isNSW, root, owner);
                Dictionary<string, string> attributes = new() { ["function.name"] = owner.F.Name };
                if (root is LlvmInstruction instruction)
                {
                    attributes["instruction.opcode"] = instruction.Opcode.ToString();
                    if (instruction.Source is { } source)
                    {
                        attributes["source.file"] = source.Source;
                        attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                return owner.Trace.MeasureDetailed("optimizer.instcombine.negator",
                    (Zero: lhsIsZero, NSW: isNSW, Root: root, Owner: owner),
                    static state => NegateCore(state.Zero, state.NSW, state.Root, state.Owner),
                    attributes: attributes, metrics: _ => owner.Metrics!);
            }
            private static LlvmValue? NegateCore(bool lhsIsZero, bool isNSW, LlvmValue root, Work owner)
            {
                owner.Count("negator.attempts");
                if (!owner.CLOpts.NegatorEnabled) return null;
                var negator = new Negator(owner, lhsIsZero, owner.CLOpts.NegatorMaxDepth);
                LlvmValue? result = negator.NegateValue(root, isNSW, 0);
                if (result is null)
                {
                    for (int index = negator.created.Count - 1; index >= 0; --index)
                        negator.created[index].EraseFromParent();
                    owner.Count("negator.failed");
                    return null;
                }
                owner.Count("negator.trees_negated");
                owner.Count("negator.instructions_negated_success", (ulong)negator.created.Count);
                foreach (LlvmInstruction instruction in negator.created) owner.Worklist.Add(instruction);
                return result;
            }
            private LlvmValue? NegateValue(LlvmValue value, bool isNSW, uint depth)
            {
                owner.Count("negator.values_visited");
                if (owner.Metrics is not null)
                    owner.Metrics["negator.max_depth"] = Math.Max(owner.Metrics.GetValueOrDefault("negator.max_depth"), depth);
                if (cache.TryGetValue(value, out LlvmValue? negated))
                {
                    owner.Count("negator.cache_hits");
#if DEBUG
                    Debug.Assert(!active.Contains(value), "Encountered a cycle during source negation.");
#endif
                    return negated;
                }
#if DEBUG
                active.Add(value);
                cache[value] = null;
#endif
                negated = Visit(value, isNSW, depth);
                cache[value] = negated;
#if DEBUG
                active.Remove(value);
#endif
                return negated;
            }
            private static (LlvmValue Left, LlvmValue Right) SortedOperands(LlvmBinaryOperator operation) =>
                operation.IsCommutative && GetComplexity(operation.Left) < GetComplexity(operation.Right)
                    ? (operation.Right, operation.Left) : (operation.Left, operation.Right);
            private static bool MatchNegation(LlvmValue value, out LlvmValue operand)
            {
                operand = null!;
                if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { IsZero: true } } subtract) return false;
                operand = subtract.Right;
                return true;
            }
            internal static bool MatchNot(LlvmValue value, out LlvmValue operand)
            {
                operand = null!;
                if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor) return false;
                if (xor.Right is LlvmConstantInt { Value.IsAllOnes: true }) { operand = xor.Left; return true; }
                if (xor.Left is LlvmConstantInt { Value.IsAllOnes: true }) { operand = xor.Right; return true; }
                return false;
            }
            private static LlvmConstant NegateConstant(LlvmConstant constant)
            {
                if (constant is LlvmUndefValue) return constant;
                if (constant is LlvmConstantInt integer) return LlvmConstantInt.Get(integer.Context, -integer.Value);
                throw new NotSupportedException("Negator constant vector/ConstantExpr negation has not been represented.");
            }
            private static LlvmValue One(LlvmType type) => LlvmConstantInt.Get((LlvmIntegerType)type, 1);
            private LlvmValue Shift(LlvmOpcode opcode, LlvmValue value, LlvmValue amount, string name = "", bool nsw = false) =>
                builder.CreateBinary(opcode, value, amount, name, nsw: nsw);
            private LlvmValue TruncateOrBitcast(LlvmValue value, LlvmType destination) =>
                ReferenceEquals(value.Type, destination) ? value : builder.CreateCast(
                    value.Type.GetScalarSizeInBits() == destination.GetScalarSizeInBits() ? LlvmOpcode.BitCast : LlvmOpcode.Trunc,
                    value, destination);
        }
    }
}
