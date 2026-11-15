// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940, InstructionSimplify.cpp:
// expandBinOp, expandCommutativeBinOp, simplifyAssociativeBinOp,
// threadBinOpOverSelect and threadBinOpOverPHI.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? IntegerBin(LlvmOpcode op, LlvmValue a, LlvmValue b, BinaryQuery q, uint depth) =>
        SimplifyBinary(op, a, b, LlvmFastMathFlags.None, q, depth);

    private static LlvmValue? ExpandIntegerBin(LlvmOpcode op, LlvmValue value, LlvmValue other,
        LlvmOpcode expanded, BinaryQuery q, uint depth)
    {
        if (value is not LlvmBinaryOperator bin || bin.Opcode != expanded) return null;
        var lhs = IntegerBin(op, bin.Left, other, q with { CanUseUndef = false }, depth);
        if (lhs is null) return null;
        var rhs = IntegerBin(op, bin.Right, other, q with { CanUseUndef = false }, depth);
        if (rhs is null) return null;
        if ((lhs == bin.Left && rhs == bin.Right) ||
            (bin.IsCommutative && lhs == bin.Right && rhs == bin.Left))
        { Count(q.Metrics, "simplify.binary.expand"); return bin; }
        var result = IntegerBin(expanded, lhs, rhs, q, depth);
        if (result is not null) Count(q.Metrics, "simplify.binary.expand");
        return result;
    }

    private static LlvmValue? ExpandCommutativeIntegerBin(LlvmOpcode op, LlvmValue lhs, LlvmValue rhs,
        LlvmOpcode expanded, BinaryQuery q, uint depth)
    {
        if (depth == 0) { Count(q.Metrics, "simplify.binary.recursion_limit"); return null; }
        return ExpandIntegerBin(op, lhs, rhs, expanded, q, depth - 1) ??
            ExpandIntegerBin(op, rhs, lhs, expanded, q, depth - 1);
    }

    private static LlvmValue? ReassociateIntegerBin(LlvmOpcode op, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth)
    {
        if (depth == 0) { Count(q.Metrics, "simplify.binary.recursion_limit"); return null; }
        --depth;
        if (lhs is LlvmBinaryOperator l && l.Opcode == op)
        {
            var v = IntegerBin(op, l.Right, rhs, q, depth);
            if (v == l.Right) return lhs;
            if (v is not null && IntegerBin(op, l.Left, v, q, depth) is { } w)
            { Count(q.Metrics, "simplify.binary.reassociate"); return w; }
        }
        if (rhs is LlvmBinaryOperator r && r.Opcode == op)
        {
            var v = IntegerBin(op, lhs, r.Left, q, depth);
            if (v == r.Left) return rhs;
            if (v is not null && IntegerBin(op, v, r.Right, q, depth) is { } w)
            { Count(q.Metrics, "simplify.binary.reassociate"); return w; }
        }
        if (lhs is LlvmBinaryOperator cl && cl.Opcode == op)
        {
            var v = IntegerBin(op, rhs, cl.Left, q, depth);
            if (v == cl.Left) return lhs;
            if (v is not null && IntegerBin(op, v, cl.Right, q, depth) is { } w)
            { Count(q.Metrics, "simplify.binary.reassociate"); return w; }
        }
        if (rhs is LlvmBinaryOperator cr && cr.Opcode == op)
        {
            var v = IntegerBin(op, cr.Right, lhs, q, depth);
            if (v == cr.Right) return rhs;
            if (v is not null && IntegerBin(op, cr.Left, v, q, depth) is { } w)
            { Count(q.Metrics, "simplify.binary.reassociate"); return w; }
        }
        return null;
    }

    private static LlvmValue? ThreadIntegerSelect(LlvmOpcode op, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth)
    {
        if (depth == 0) { Count(q.Metrics, "simplify.binary.recursion_limit"); return null; }
        var select = lhs as LlvmSelectInstruction ?? rhs as LlvmSelectInstruction;
        if (select is null) return null;
        Count(q.Metrics, "simplify.binary.select_thread_queries");
        --depth;
        var tv = select == lhs ? IntegerBin(op, select.TrueValue, rhs, q, depth) :
            IntegerBin(op, lhs, select.TrueValue, q, depth);
        var fv = select == lhs ? IntegerBin(op, select.FalseValue, rhs, q, depth) :
            IntegerBin(op, lhs, select.FalseValue, q, depth);
        if (tv == fv) return tv;
        if (tv is not null && q.IsUndef(tv)) return fv;
        if (fv is not null && q.IsUndef(fv)) return tv;
        if (tv == select.TrueValue && fv == select.FalseValue) return select;
        if ((tv is null) == (fv is null)) return null;
        if ((fv ?? tv) is not LlvmBinaryOperator simple || simple.Opcode != op ||
            simple.HasNoSignedWrap || simple.HasNoUnsignedWrap || simple.IsExact || simple.IsDisjoint)
            return null;
        var branch = fv is not null ? select.TrueValue : select.FalseValue;
        var a = select == lhs ? branch : lhs;
        var b = select == lhs ? rhs : branch;
        return (simple.Left == a && simple.Right == b) ||
            (simple.IsCommutative && simple.Left == b && simple.Right == a) ? simple : null;
    }

    private static LlvmValue? ThreadIntegerPhi(LlvmOpcode op, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth)
    {
        if (depth == 0) { Count(q.Metrics, "simplify.binary.recursion_limit"); return null; }
        var phi = lhs as LlvmPhiInstruction ?? rhs as LlvmPhiInstruction;
        if (phi is null || !ValueDominatesPhi(phi == lhs ? rhs : lhs, phi, q.Tree)) return null;
        Count(q.Metrics, "simplify.binary.phi_thread_queries");
        LlvmValue? common = null;
        for (int i = 0; i < phi.IncomingCount; ++i)
        {
            Count(q.Metrics, "simplify.binary.phi_incoming");
            var incoming = phi.GetIncomingValue(i);
            if (incoming == phi) continue;
            var edge = q with { Context = phi.GetIncomingBlock(i).GetTerminator() };
            var value = phi == lhs ? IntegerBin(op, incoming, rhs, edge, depth - 1) :
                IntegerBin(op, lhs, incoming, edge, depth - 1);
            if (value is null || (common is not null && value != common)) return null;
            common = value;
        }
        return common;
    }

    private static LlvmValue? ThreadIntegerBin(LlvmOpcode op, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth) => ThreadIntegerSelect(op, lhs, rhs, q, depth) ??
        ThreadIntegerPhi(op, lhs, rhs, q, depth);
}
