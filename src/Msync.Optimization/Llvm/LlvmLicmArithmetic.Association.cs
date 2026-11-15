// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: LICM.cpp
// hoistMulAddAssociation / hoistBOAssociation / hoistSubAddAssociation;
// Local.h/.cpp OverflowTracking mergeFlags/applyFlags.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicmArithmetic
{
    private static bool HoistMulAddAssociation(LlvmInstruction instruction, in Query query)
    {
        if (instruction is not LlvmBinaryOperator multiply || !IsReassociable(multiply, LlvmOpcode.Mul, LlvmOpcode.FMul)) return false;
        LlvmValue variant = multiply.Left, invariant = multiply.Right;
        if (query.Loop.IsLoopInvariant(variant)) (variant, invariant) = (invariant, variant);
        if (query.Loop.IsLoopInvariant(variant) || !query.Loop.IsLoopInvariant(invariant)) return false;
        List<LlvmUse> changes = [];
        List<LlvmBinaryOperator> adds = [], work = [];
        if (variant is LlvmBinaryOperator variantBinary) work.Add(variantBinary);
        while (work.Count != 0)
        {
            LlvmBinaryOperator operation = work[^1]; work.RemoveAt(work.Count - 1);
            Count(query, "muladd.visited");
            if (!operation.HasOneUse) return false;
            if (IsReassociable(operation, LlvmOpcode.Add, LlvmOpcode.FAdd) &&
                operation.Left is LlvmBinaryOperator left && operation.Right is LlvmBinaryOperator right)
            { work.Add(left); work.Add(right); adds.Add(operation); continue; }
            if (!IsReassociable(operation, LlvmOpcode.Mul, LlvmOpcode.FMul) || query.Loop.IsLoopInvariant(operation)) return false;
            if (query.Loop.IsLoopInvariant(operation.Left)) changes.Add(operation.GetOperandUse(0));
            else if (query.Loop.IsLoopInvariant(operation.Right)) changes.Add(operation.GetOperandUse(1));
            else return false;
            uint limit = instruction.Type.IsIntOrIntVectorTy() ? query.IntLimit : query.FpLimit;
            if (changes.Count > limit) { Count(query, "muladd.reassociation_limit"); return false; }
        }
        if (changes.Count == 0) return false;
        bool integer = instruction.Type.IsIntOrIntVectorTy();
        if (integer) foreach (var add in adds) DropPoisonFlags(add);
        foreach (LlvmUse use in changes)
        {
            var operation = (LlvmBinaryOperator)use.User;
            LlvmValue factor = CreateBinary(integer ? LlvmOpcode.Mul : LlvmOpcode.FMul, use.Value!, invariant,
                query.InsertPoint, query, integer ? "factor.op.mul" : "factor.op.fmul", integer ? 0 : operation.FastMathFlags);
            if (integer) DropPoisonFlags(operation);
            var result = new LlvmBinaryOperator(operation.Opcode,
                use.OperandNo == 0 ? factor : operation.Left, use.OperandNo == 1 ? factor : operation.Right)
            {
                HasNoUnsignedWrap = operation.HasNoUnsignedWrap, HasNoSignedWrap = operation.HasNoSignedWrap,
                FastMathFlags = operation.FastMathFlags, Source = operation.Source
            };
            result.SetName(operation.Name + ".reass");
            operation.Parent!.InsertBefore(result, operation);
            if (ReferenceEquals(variant, operation)) variant = result;
            operation.ReplaceAllUsesWith(result);
            query.Erase(operation);
            Count(query, "muladd.invariants_hoisted");
        }
        instruction.ReplaceAllUsesWith(variant);
        query.Erase(instruction);
        return true;
    }

    private static bool HoistBinaryAssociation(LlvmInstruction instruction, in Query query)
    {
        if (instruction is not LlvmBinaryOperator outer || !outer.IsAssociative) return false;
        bool variantOnRight = query.Loop.IsLoopInvariant(outer.Left);
        LlvmValue operand = variantOnRight ? outer.Right : outer.Left;
        if (operand is not LlvmBinaryOperator inner || inner.Opcode != outer.Opcode || !inner.IsAssociative ||
            inner.HasNUsesOrMore(inner.Type.IsIntegerTy() ? 2 : 3)) return false;
        LlvmValue variant = inner.Left, first = inner.Right, second = variantOnRight ? outer.Left : outer.Right;
        if (query.Loop.IsLoopInvariant(variant) && !query.Loop.IsLoopInvariant(first)) (variant, first) = (first, variant);
        if (query.Loop.IsLoopInvariant(variant) || !query.Loop.IsLoopInvariant(first) || !query.Loop.IsLoopInvariant(second)) return false;
        LlvmValue invariant = CreateBinary(outer.Opcode, first, second, query.InsertPoint, query, "invariant.op");
        var result = new LlvmBinaryOperator(outer.Opcode, variant, invariant) { Source = instruction.Source };
        result.SetName(outer.Name + ".reass");
        outer.Parent!.InsertBefore(result, outer);
        if (outer.Opcode is LlvmOpcode.FAdd or LlvmOpcode.FMul)
        {
            var intersection = outer.FastMathFlags & inner.FastMathFlags;
            if (invariant is ILlvmFastMathInstruction floating) floating.FastMathFlags = intersection;
            result.FastMathFlags = intersection;
        }
        else
        {
            bool nuw = outer.HasNoUnsignedWrap && inner.HasNoUnsignedWrap;
            bool nsw = outer.HasNoSignedWrap && inner.HasNoSignedWrap;
            bool disjoint = outer.IsDisjoint && inner.IsDisjoint;
            ApplyOverflowFlags(result, nuw, nsw, disjoint);
            if (invariant is LlvmBinaryOperator invariantInstruction) ApplyOverflowFlags(invariantInstruction, nuw, nsw, disjoint);
            if (outer.Opcode == LlvmOpcode.Add && nsw && !nuw &&
                LlvmValueTracking.ComputeOverflowForSignedAdd(first, second, query.Layout, query.InsertPoint, query.Tree, query.Metrics) == LlvmOverflowResult.NeverOverflows)
            {
                if (invariant is LlvmBinaryOperator invariantAdd) invariantAdd.HasNoSignedWrap = true;
                result.HasNoSignedWrap = true;
            }
        }
        outer.ReplaceAllUsesWith(result);
        query.Erase(outer);
        if (inner.UseEmpty) query.Erase(inner);
        return true;
    }

    private static void ApplyOverflowFlags(LlvmBinaryOperator operation, bool nuw, bool nsw, bool disjoint)
    {
        DropPoisonFlags(operation);
        // LICM sets AllKnownNonNegative and AllKnownNonZero to false.
        if (operation.Opcode == LlvmOpcode.Add)
        { operation.HasNoUnsignedWrap = nuw; operation.HasNoSignedWrap = nsw && nuw; }
        if (operation.Opcode == LlvmOpcode.Or) operation.IsDisjoint = disjoint;
    }

    private static bool HoistSubAddAssociation(LlvmInstruction instruction, in Query query)
    {
        if (instruction is not LlvmBinaryOperator outer) return false;
        LlvmBinaryOperator inner;
        LlvmValue variant, first, second;
        LlvmOpcode invariantOpcode, resultOpcode;
        if (outer.Opcode == LlvmOpcode.Sub && outer.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true } added)
        {
            inner = added; variant = added.Left; first = added.Right; second = outer.Right;
            if (query.Loop.IsLoopInvariant(variant) && !query.Loop.IsLoopInvariant(first)) (variant, first) = (first, variant);
            invariantOpcode = LlvmOpcode.Sub; resultOpcode = LlvmOpcode.Add;
        }
        else if (outer.Opcode == LlvmOpcode.Sub && outer.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasOneUse: true } subtracted)
        {
            inner = subtracted; variant = subtracted.Left; first = subtracted.Right; second = outer.Right;
            invariantOpcode = LlvmOpcode.Add; resultOpcode = LlvmOpcode.Sub;
        }
        else if (outer.Opcode == LlvmOpcode.Add && MatchOneUseSub(outer.Left, outer.Right, out inner, out second) ||
                 outer.Opcode == LlvmOpcode.Add && MatchOneUseSub(outer.Right, outer.Left, out inner, out second))
        {
            variant = inner.Left; first = second; second = inner.Right;
            invariantOpcode = LlvmOpcode.Sub; resultOpcode = LlvmOpcode.Add;
        }
        else return false;
        if (query.Loop.IsLoopInvariant(variant) || !query.Loop.IsLoopInvariant(first) || !query.Loop.IsLoopInvariant(second)) return false;
        LlvmValue invariant = CreateBinary(invariantOpcode, first, second, query.InsertPoint, query, "invariant.op");
        var result = new LlvmBinaryOperator(resultOpcode, variant, invariant) { Source = instruction.Source };
        result.SetName(instruction.Name + ".reass");
        instruction.Parent!.InsertBefore(result, instruction);
        instruction.ReplaceAllUsesWith(result);
        query.Erase(instruction);
        query.Erase(inner);
        return true;
    }
    private static bool MatchOneUseSub(LlvmValue value, LlvmValue other, out LlvmBinaryOperator subtract, out LlvmValue invariant)
    {
        subtract = value as LlvmBinaryOperator ?? null!;
        invariant = other;
        return subtract is { Opcode: LlvmOpcode.Sub, HasOneUse: true };
    }
}
