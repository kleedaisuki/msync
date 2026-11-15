// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp
// getFreelyInvertedImpl; InstCombiner.h getFreelyInverted/isFreeToInvert.
// Success with no Value represents C++'s analysis-only pointer(1) sentinel.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private readonly record struct InvertedValue(bool Success, LlvmValue? Value)
        {
            internal static InvertedValue Failure => new(false, null);
            internal static InvertedValue AnalysisSuccess => new(true, null);
            internal static InvertedValue Concrete(LlvmValue value) => new(true, value);
        }
        private LlvmValue? GetFreelyInverted(LlvmValue value, bool allUses, LlvmTransformBuilder builder) =>
            GetFreelyInverted(value, allUses, builder, out _);
        private LlvmValue? GetFreelyInverted(LlvmValue value, bool allUses, LlvmTransformBuilder builder, out bool consumes)
        {
            consumes = false;
            var result = GetFreelyInvertedImpl(value, allUses, builder, ref consumes, 0);
            Debug.Assert(!result.Success || result.Value is not null);
            return result.Value;
        }
        private bool IsFreeToInvert(LlvmValue value, bool allUses) => IsFreeToInvert(value, allUses, out _);
        private bool IsFreeToInvert(LlvmValue value, bool allUses, out bool consumes)
        {
            consumes = false;
            return GetFreelyInvertedImpl(value, allUses, null, ref consumes, 0).Success;
        }
        private InvertedValue GetFreelyInvertedImpl(LlvmValue value, bool allUses, LlvmTransformBuilder? builder,
            ref bool consumes, uint depth)
        {
            Count("invert.values_visited");
            if (Negator.MatchNot(value, out LlvmValue complemented))
            { consumes = true; return InvertedValue.Concrete(complemented); }
            if (IsAddSubImmediateConstant(value))
                return InvertedValue.Concrete(InvertScalarConstant((LlvmConstant)value));
            if (depth++ >= 6) { Count("invert.depth_limit"); return InvertedValue.Failure; }
            if (!allUses) return InvertedValue.Failure;
            if (value is LlvmCmpInstruction compare)
            {
                if (builder is null) return InvertedValue.AnalysisSuccess;
                var predicate = LlvmCmpInstruction.GetInversePredicate(compare.Predicate);
                return InvertedValue.Concrete(compare is LlvmICmpInstruction
                    ? builder.CreateICmp(predicate, compare.Left, compare.Right)
                    : builder.CreateFCmp(predicate, compare.Left, compare.Right));
            }
            if (value is LlvmBinaryOperator binary)
            {
                if (binary.Opcode is LlvmOpcode.Add or LlvmOpcode.Xor)
                {
                    var right = GetFreelyInvertedImpl(binary.Right, binary.Right.HasOneUse, builder, ref consumes, depth);
                    if (right.Success)
                        return builder is null ? InvertedValue.AnalysisSuccess : InvertedValue.Concrete(
                            binary.Opcode == LlvmOpcode.Add ? builder.CreateSub(right.Value!, binary.Left) : builder.CreateXor(binary.Left, right.Value!));
                    var left = GetFreelyInvertedImpl(binary.Left, binary.Left.HasOneUse, builder, ref consumes, depth);
                    if (left.Success)
                        return builder is null ? InvertedValue.AnalysisSuccess : InvertedValue.Concrete(
                            binary.Opcode == LlvmOpcode.Add ? builder.CreateSub(left.Value!, binary.Right) : builder.CreateXor(left.Value!, binary.Right));
                    return InvertedValue.Failure;
                }
                if (binary.Opcode is LlvmOpcode.Sub or LlvmOpcode.AShr)
                {
                    var left = GetFreelyInvertedImpl(binary.Left, binary.Left.HasOneUse, builder, ref consumes, depth);
                    if (!left.Success) return InvertedValue.Failure;
                    return builder is null ? InvertedValue.AnalysisSuccess : InvertedValue.Concrete(
                        binary.Opcode == LlvmOpcode.Sub ? builder.CreateAdd(left.Value!, binary.Right) :
                        builder.CreateBinary(LlvmOpcode.AShr, left.Value!, binary.Right));
                }
            }
            LlvmValue? first = null, second = null;
            bool select = value is LlvmSelectInstruction selected && !ShouldAvoidAbsorbingNotIntoSelect(selected);
            LlvmIntrinsicKind? minmaxKind = null;
            if (select) { first = ((LlvmSelectInstruction)value).TrueValue; second = ((LlvmSelectInstruction)value).FalseValue; }
            else if (value is LlvmCallInstruction call && IsInversionMinMax(call.CalledFunction?.IntrinsicDescriptor?.Kind))
            { first = call.GetArgument(0); second = call.GetArgument(1); minmaxKind = call.CalledFunction!.IntrinsicDescriptor!.Kind; }
            if (first is not null)
            {
                bool localConsumes = consumes;
                if (!GetFreelyInvertedImpl(second!, second!.HasOneUse, null, ref localConsumes, depth).Success) return InvertedValue.Failure;
                var notFirst = GetFreelyInvertedImpl(first, first.HasOneUse, builder, ref localConsumes, depth);
                if (notFirst.Success)
                {
                    consumes = localConsumes;
                    if (builder is null) return InvertedValue.AnalysisSuccess;
                    var notSecond = GetFreelyInvertedImpl(second, second.HasOneUse, builder, ref consumes, depth);
                    Debug.Assert(notSecond.Success);
                    return InvertedValue.Concrete(minmaxKind is { } kind
                        ? builder.CreateBinaryIntrinsic(LlvmIntrinsic.GetInverseMinMax(kind), notFirst.Value!, notSecond.Value!)
                        : builder.CreateSelect(((LlvmSelectInstruction)value).Condition, notFirst.Value!, notSecond.Value!));
                }
            }
            if (value is LlvmPhiInstruction phi)
            {
                bool localConsumes = consumes;
                List<(LlvmValue Value, LlvmBasicBlock Block)> incoming = new(phi.IncomingCount);
                for (int index = 0; index < phi.IncomingCount; ++index)
                {
                    var inverted = GetFreelyInvertedImpl(phi.GetIncomingValue(index), false, null, ref localConsumes, 5);
                    if (!inverted.Success || ReferenceEquals(inverted.Value, value)) return InvertedValue.Failure;
                    if (builder is not null) incoming.Add((inverted.Value!, phi.GetIncomingBlock(index)));
                }
                consumes = localConsumes;
                if (builder is null) return InvertedValue.AnalysisSuccess;
                var saved = builder.SaveInsertionPoint(); var savedSource = builder.CurrentSource;
                builder.SetInsertPoint(phi);
                try
                {
                    var result = builder.CreatePhi(phi.Type, phi.IncomingCount);
                    foreach (var edge in incoming) result.AddIncoming(edge.Value, edge.Block);
                    return InvertedValue.Concrete(result);
                }
                finally { builder.RestoreInsertionPoint(saved); builder.CurrentSource = savedSource; }
            }
            if (value is LlvmCastInstruction cast &&
                (cast.Opcode is LlvmOpcode.SExt or LlvmOpcode.Trunc || cast is { Opcode: LlvmOpcode.ZExt, IsNonNegative: true }))
            {
                var inverted = GetFreelyInvertedImpl(cast.Operand, cast.Operand.HasOneUse, builder, ref consumes, depth);
                if (!inverted.Success) return InvertedValue.Failure;
                return builder is null ? InvertedValue.AnalysisSuccess : InvertedValue.Concrete(
                    builder.CreateCast(cast.Opcode == LlvmOpcode.Trunc ? LlvmOpcode.Trunc : LlvmOpcode.SExt, inverted.Value!, cast.Type));
            }
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Or or LlvmOpcode.And } logic)
                return InvertDeMorgan(value, logic.Opcode == LlvmOpcode.Or ? LlvmOpcode.And : LlvmOpcode.Or, false,
                    logic.Left, logic.Right, builder, ref consumes, depth);
            if (MatchScalarLogical(value, false, out var leftLogical, out var rightLogical))
                return InvertDeMorgan(value, LlvmOpcode.And, true, leftLogical, rightLogical, builder, ref consumes, depth);
            if (MatchScalarLogical(value, true, out leftLogical, out rightLogical))
                return InvertDeMorgan(value, LlvmOpcode.Or, true, leftLogical, rightLogical, builder, ref consumes, depth);
            return InvertedValue.Failure;
        }
        private InvertedValue InvertDeMorgan(LlvmValue original, LlvmOpcode opcode, bool logical, LlvmValue left,
            LlvmValue right, LlvmTransformBuilder? builder, ref bool consumes, uint depth)
        {
            bool localConsumes = consumes;
            if (!GetFreelyInvertedImpl(right, right.HasOneUse, null, ref localConsumes, depth).Success) return InvertedValue.Failure;
            var notLeft = GetFreelyInvertedImpl(left, left.HasOneUse, builder, ref localConsumes, depth);
            if (!notLeft.Success) return InvertedValue.Failure;
            var notRight = GetFreelyInvertedImpl(right, right.HasOneUse, builder, ref localConsumes, depth);
            consumes = localConsumes;
            if (builder is null) return InvertedValue.AnalysisSuccess;
            return InvertedValue.Concrete(logical
                ? opcode == LlvmOpcode.And ? builder.CreateLogicalAnd(notLeft.Value!, notRight.Value!) :
                    builder.CreateLogicalOr(notLeft.Value!, notRight.Value!)
                : builder.CreateBinOp(opcode, notLeft.Value!, notRight.Value!));
        }
        private static LlvmConstant InvertScalarConstant(LlvmConstant constant)
        {
            if (constant is LlvmUndefValue) return constant;
            if (constant is LlvmConstantInt integer) return LlvmConstantInt.Get(integer.Context, ~integer.Value);
            throw new NotSupportedException("Freely-inverted scalar constants require the represented integer constant kind.");
        }
        private static bool IsAddSubImmediateConstant(LlvmValue value) =>
            value is LlvmConstant && value.SubclassId != (byte)LlvmValueKind.ConstantExprVal;
        private static bool IsInversionMinMax(LlvmIntrinsicKind? kind) => kind == LlvmIntrinsicKind.SMin ||
            kind == LlvmIntrinsicKind.SMax || kind == LlvmIntrinsicKind.UMin || kind == LlvmIntrinsicKind.UMax;
    }
}
