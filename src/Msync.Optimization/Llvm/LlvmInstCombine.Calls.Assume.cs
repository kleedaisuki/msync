// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// llvm.assume visitor (3921–4220), represented bundle and scalar condition paths.
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitAssume(LlvmCallInstruction call)
        {
            Count("calls.assume.visited");
            for (int index = 0; index < call.OperandBundleCount; ++index)
            {
                LlvmOperandBundleUse bundle = call.GetOperandBundleAt(index);
                switch (bundle.Tag)
                {
                    case "align":
                    {
                        var info = LlvmAssumeBundleQueries.GetAssumeAlignInfo(bundle);
                        if (info.Alignment is not { } alignment) break;
                        if (alignment == 1 || !BitOperations.IsPow2(alignment)) return RemoveAssumeBundle(call, index);
                        if (LlvmGepOperator.TryGet(info.Pointer, out var gep) && gep.GetMaxPreservedAlignment(DL).Value >= alignment)
                        {
                            Builder.CreateAlignmentAssumption(DL, gep.PointerOperand, alignment, bundle.InputCount == 3 ? bundle.GetInput(2) : null);
                            return RemoveAssumeBundle(call, index);
                        }
                        if (info.Offset is not { } offset) break;
                        if (LlvmGepOperator.TryGet(info.Pointer, out var ptrAdd) && ptrAdd.SourceElementType.IsIntegerTy(8)
                            && ptrAdd.GetIndex(0).Type.IsIntegerTy(DL.GetIndexTypeSizeInBits(ptrAdd.Type))
                            && ptrAdd.GetIndex(0) is LlvmConstantInt ptrOffset && ptrOffset.Value.TrySExtValue() is { } displacement)
                        {
                            Builder.CreateAlignmentAssumption(DL, ptrAdd.PointerOperand, alignment,
                                LlvmConstantInt.Get(LlvmType.GetIntNTy(call.Context, 64), unchecked(offset - (ulong)displacement)));
                            return RemoveAssumeBundle(call, index);
                        }
                        if (LlvmValueTracking.GetUnderlyingObject(info.Pointer, metrics: Metrics) is LlvmArgument) break;
                        LlvmKnownBits known = ComputeKnownBits(info.Pointer, call);
                        LlvmAPInt mask = new(known.BitWidth, alignment - 1, implicitTrunc: true);
                        if ((known.Zero & mask) == new LlvmAPInt(known.BitWidth, ~offset & (alignment - 1), implicitTrunc: true)
                            && (known.One & mask) == new LlvmAPInt(known.BitWidth, offset & (alignment - 1), implicitTrunc: true))
                            return RemoveAssumeBundle(call, index);
                        break;
                    }
                    case "dereferenceable":
                    {
                        if (bundle.GetInput(1) is not LlvmConstantInt count) break;
                        ulong size = count.GetZExtValue();
                        if (size == 0 || LlvmValueTracking.IsDereferenceableAndAlignedPointer(bundle.GetInput(0), new LlvmAlign(1),
                            new LlvmAPInt(64, size), DL, call, DT, metrics: Metrics, assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: DC))
                            return RemoveAssumeBundle(call, index);
                        break;
                    }
                    case "ignore": return RemoveAssumeBundle(call, index);
                    case "nonnull":
                    {
                        LlvmValue nonnullPointer = bundle.GetInput(0);
                        if (IsKnownNonZero(nonnullPointer, call)) return RemoveAssumeBundle(call, index);
                        if (LlvmGepOperator.TryGet(nonnullPointer, out var gep) && gep.IsInBounds && !LlvmFunction.NullPointerIsDefined(F, nonnullPointer.Type.GetPointerAddressSpace()))
                        {
                            Builder.CreateNonnullAssumption(LlvmValueTracking.StripInBoundsOffsets(nonnullPointer, metrics: Metrics));
                            return RemoveAssumeBundle(call, index);
                        }
                        break;
                    }
                    case "noundef":
                        if (LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(bundle.GetInput(0), DT, call, Metrics, assumptions: AC))
                            return RemoveAssumeBundle(call, index);
                        break;
                    case "separate_storage":
                    {
                        for (int operand = 0; operand < 2; ++operand)
                        {
                            LlvmValue hint = bundle.GetInput(operand);
                            LlvmValue underlying = LlvmValueTracking.GetUnderlyingObject(hint, maxLookup: 0, metrics: Metrics);
                            if (!ReferenceEquals(hint, underlying)) ReplaceUse(bundle.GetInputUse(operand), underlying);
                        }
                        if (AA is not null)
                        {
                            LlvmAAQueryInfo query = new(AA) { AllowEphemerals = false };
                            if (AA.Alias(LlvmMemoryLocation.GetBeforeOrAfter(bundle.GetInput(0)), LlvmMemoryLocation.GetBeforeOrAfter(bundle.GetInput(1)), query, call) == LlvmAliasKind.NoAlias)
                                return RemoveAssumeBundle(call, index);
                        }
                        break;
                    }
                    case "range":
                    {
                        var info = LlvmAssumeBundleQueries.GetAssumeRangeInfo(bundle);
                        var query = Query(call);
                        LlvmConstantRange known = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(info.Value, context: call,
                            tree: query.Tree, metrics: query.Metrics, useInstructionInfo: query.UseInstructionInfo, assumptions: query.Assumptions,
                            allowEphemerals: query.AllowEphemerals, conditions: query.DC);
                        if (info.Range.Contains(known)) return RemoveAssumeBundle(call, index);
                        if (info.Range.ExactIntersectWith(known) is { } intersection && intersection != info.Range)
                        { Builder.CreateRangeAssumption(info.Value, intersection); return RemoveAssumeBundle(call, index); }
                        if (info.Value is LlvmBinaryOperator { Right: LlvmConstantInt constant } operation)
                        {
                            if (operation.Opcode == LlvmOpcode.Add)
                            { Builder.CreateRangeAssumption(operation.Left, info.Range.Sub(new(constant.Value))); return RemoveAssumeBundle(call, index); }
                            if (operation.Opcode == LlvmOpcode.Sub)
                            { Builder.CreateRangeAssumption(operation.Left, info.Range.Add(new(constant.Value))); return RemoveAssumeBundle(call, index); }
                        }
                        break;
                    }
                    case "dereferenceable_or_null": case "cold": break;
                    default: throw new InvalidOperationException($"Unexpected attribute operand bundle '{bundle.Tag}' on llvm.assume.");
                }
            }
            if (call.HasOperandBundles)
            {
                if (call.Previous is LlvmCallInstruction previous && previous.CalledFunction?.IntrinsicDescriptor?.IsAssume == true && previous.HasOperandBundles)
                {
                    List<LlvmOperandBundleDef> bundles = [.. previous.GetOperandBundlesAsDefs(), .. call.GetOperandBundlesAsDefs()];
                    Builder.CreateAssumption(bundles); EraseInstFromFunction(previous); return EraseInstFromFunction(call);
                }
                return null;
            }
            LlvmValue condition = call.GetArgument(0);
            if (CallsLogical(condition, LlvmOpcode.And, out var left, out var right))
            {
                Builder.CreateAssumption(left); Builder.CreateAssumption(right); return EraseInstFromFunction(call);
            }
            if (CallsNot(condition, out var negation) && CallsLogical(negation, LlvmOpcode.Or, out left, out right))
            {
                Builder.CreateAssumption(Builder.CreateNot(left)); Builder.CreateAssumption(Builder.CreateNot(right)); return EraseInstFromFunction(call);
            }
            LlvmValue comparison = CallsNot(condition, out var not) ? not : condition;
            bool inverted = !ReferenceEquals(comparison, condition);
            if (comparison is LlvmICmpInstruction cmp && cmp.Predicate == (inverted ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe)
                && cmp.Right is LlvmConstant { IsNullValue: true } && cmp.Left.Type.IsPointerTy())
            { Builder.CreateNonnullAssumption(cmp.Left); return EraseInstFromFunction(call); }
            if (CallsAlignmentCondition(condition, out var pointer, out ulong alignmentMask) && BitOperations.IsPow2(unchecked(alignmentMask + 1)))
            { Builder.CreateAlignmentAssumption(DL, pointer, unchecked(alignmentMask + 1)); return EraseInstFromFunction(call); }
            if (condition is LlvmConstantInt or LlvmUndefValue)
            {
                if (condition is not LlvmConstantInt constant || constant.IsZero) CreateNonTerminatorUnreachable(call);
                return EraseInstFromFunction(call);
            }
            AC.UpdateAffectedValues(call); return null;
        }
        private LlvmInstruction? RemoveAssumeBundle(LlvmCallInstruction call, int index)
        {
            Count("calls.assume.bundle_removed");
            if (call.OperandBundleCount == 1) return EraseInstFromFunction(call);
            var bundles = call.GetOperandBundlesAsDefs().Where((_, ordinal) => ordinal != index).ToArray();
            var replacement = new LlvmCallInstruction(call.FunctionType, call.CalledOperand, call.Arguments.ToArray(), bundles)
                { Source = call.Source, LogicalType = call.LogicalType, TailCallKind = call.TailCallKind, FastMathFlags = call.FastMathFlags };
            replacement.CopyAttributesFrom(call); return replacement;
        }
        private static bool CallsLogical(LlvmValue value, LlvmOpcode opcode, out LlvmValue left, out LlvmValue right)
        {
            if (value is LlvmBinaryOperator binary && binary.Opcode == opcode) { left = binary.Left; right = binary.Right; return true; }
            if (value is LlvmSelectInstruction select)
            {
                if (opcode == LlvmOpcode.And && CallsIsZero(select.FalseValue)) { left = select.Condition; right = select.TrueValue; return true; }
                if (opcode == LlvmOpcode.Or && CallsIsOne(select.TrueValue)) { left = select.Condition; right = select.FalseValue; return true; }
            }
            left = value; right = value; return false;
        }
        private static bool CallsAlignmentCondition(LlvmValue condition, out LlvmValue pointer, out ulong mask)
        {
            LlvmValue? integer = null; mask = 1;
            if (CallsNot(condition, out var value) && value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } truncation) integer = truncation.Operand;
            else if (condition is LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpEq, Right: LlvmConstantInt { IsZero: true },
                Left: LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt constant } andOp } && constant.Value.GetActiveBits() <= 64)
            { integer = andOp.Left; mask = constant.GetZExtValue(); }
            if (integer is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } ptr)
            { pointer = ptr.Operand; return true; }
            pointer = condition; return false;
        }
    }
}
