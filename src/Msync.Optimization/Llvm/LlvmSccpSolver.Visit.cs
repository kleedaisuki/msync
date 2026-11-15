// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Utils/SCCPSolver.cpp instruction visitors, function-local subset.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmSccpSolver
{
    public void InitializeFunction()
    {
        MarkBlockExecutable(function.BasicBlocks[0]);
        // trackValueOfArgument without argument attributes initializes overdefined.
        foreach (var argument in function.Arguments) MarkOverdefined(argument);
    }
    private void Visit(LlvmInstruction instruction)
    {
        if (trace is null) { VisitCore(instruction); return; }
        var attributes = InstructionAttributes(instruction);
        attributes["lattice.before"] = StateSummary(instruction);
        trace.Run("optimizer.llvm-sccp.visit", (Self: this, Instruction: instruction, Attributes: attributes),
            static state =>
            {
                state.Self.VisitCore(state.Instruction);
                state.Attributes["lattice.after"] = state.Self.StateSummary(state.Instruction);
            }, attributes);
    }
    private void VisitCore(LlvmInstruction instruction)
    {
        Count("visit.instructions");
        if (metrics is not null) Count("visit.opcode." + instruction.Opcode);
        switch (instruction)
        {
            case LlvmPhiInstruction phi: VisitPhi(phi); break;
            case LlvmUncondBrInstruction or LlvmCondBrInstruction or LlvmSwitchInstruction: VisitTerminator(instruction); break;
            case LlvmReturnInstruction or LlvmUnreachableInstruction or LlvmStoreInstruction: break;
            case LlvmBinaryOperator binary: VisitBinary(binary); break;
            case LlvmUnaryOperator unary: VisitUnary(unary); break;
            case LlvmCmpInstruction compare: VisitCompare(compare); break;
            case LlvmCastInstruction cast: VisitCast(cast); break;
            case LlvmSelectInstruction select: VisitSelect(select); break;
            case LlvmFreezeInstruction freeze: VisitFreeze(freeze); break;
            case LlvmInsertValueInstruction insert: VisitInsert(insert); break;
            case LlvmExtractValueInstruction extract: VisitExtract(extract); break;
            case LlvmAllocaInstruction alloca:
                if (alloca.AddressSpace != 0) MarkOverdefined(alloca);
                else if (State(alloca).MarkNotConstant(LlvmConstantPointerNull.Get((LlvmPointerType)alloca.Type))) PushUsers(alloca);
                break;
            case LlvmLoadInstruction load: VisitLoad(load); break;
            case LlvmGetElementPtrInstruction gep: VisitGep(gep); break;
            case LlvmCallInstruction call: VisitCall(call); break;
            default: MarkOverdefined(instruction); Count("visit.generic_overdefined"); break;
        }
    }
    private void VisitTerminator(LlvmInstruction instruction)
    {
        var feasible = new bool[instruction.SuccessorCount];
        if (instruction is LlvmUncondBrInstruction) feasible[0] = true;
        else if (instruction is LlvmCondBrInstruction branch)
        {
            var condition = State(branch.Condition);
            if (Constant(condition, branch.Condition.Type) is LlvmConstantInt value) feasible[value.IsZero ? 1 : 0] = true;
            else if (!condition.IsUnknownOrUndef) Array.Fill(feasible, true);
        }
        else if (instruction is LlvmSwitchInstruction selection)
        {
            var condition = State(selection.Condition);
            if (selection.CaseCount == 0) feasible[0] = true;
            else if (Constant(condition, selection.Condition.Type) is LlvmConstantInt value)
                feasible[selection.FindCaseValue(value) + 1] = true;
            else if (condition.IsConstantRange(false))
            {
                var range = condition.GetConstantRange(); ulong cases = 0;
                for (int i = 0; i < selection.CaseCount; ++i)
                    if (range.Contains(selection.GetCaseValue(i).Value)) { feasible[i + 1] = true; ++cases; }
                feasible[0] = range.IsSizeLargerThan(cases);
            }
            else if (!condition.IsUnknownOrUndef) Array.Fill(feasible, true);
        }
        for (int i = 0; i < feasible.Length; ++i)
        {
            Count("reachability.successors_tested");
            if (feasible[i]) MarkEdgeExecutable(instruction.Parent!, instruction.GetSuccessor(i));
        }
    }
    private void VisitPhi(LlvmPhiInstruction phi)
    {
        if (phi.IncomingCount > 64) { MarkOverdefined(phi); Count("phi.high_degree_overdefined"); return; }
        if (FullyOverdefined(phi)) return;
        var feasible = new List<int>();
        for (int i = 0; i < phi.IncomingCount; ++i)
            if (IsEdgeFeasible(phi.GetIncomingBlock(i), phi.Parent!)) feasible.Add(i);
        Count("phi.feasible_inputs", (ulong)feasible.Count);
        foreach (int field in ElementIndices(phi))
        {
            var state = ElementState(phi, field);
            if (state.IsOverdefined) continue;
            var next = state.Clone();
            foreach (int i in feasible)
            { next.MergeIn(ElementState(phi.GetIncomingValue(i), field)); if (next.IsOverdefined) break; }
            Merge(state, phi, next, new LlvmLatticeMergeOptions().WithMaxWidenSteps((uint)feasible.Count + 1));
            state.NumRangeExtensions = Math.Max((uint)feasible.Count, state.NumRangeExtensions);
        }
    }
    private void VisitBinary(LlvmBinaryOperator binary)
    {
        var left = State(binary.Left); var right = State(binary.Right); var state = State(binary);
        if (state.IsOverdefined || left.IsUnknownOrUndef || right.IsUnknownOrUndef) return;
        if (left.IsOverdefined && right.IsOverdefined) { MarkOverdefined(binary); return; }
        if (left.IsConstant || right.IsConstant)
        {
            LlvmValue a = IsConstant(left) ? Constant(left, binary.Left.Type)! : binary.Left;
            LlvmValue b = IsConstant(right) ? Constant(right, binary.Right.Type)! : binary.Right;
            var simplified = binary.Type.GetScalarType().IsFloatingPointTy()
                ? LlvmInstructionSimplify.SimplifyBinOp(binary.Opcode, a, b, binary.FastMathFlags, layout, binary, metrics)
                : LlvmInstructionSimplify.SimplifyBinOp(binary.Opcode, a, b, layout, binary, metrics);
            if (simplified is LlvmConstant constant)
            {
                var next = new LlvmValueLatticeElement(); next.MarkConstant(constant, true);
                Merge(state, binary, next); Count("binary.constant_simplifications"); return;
            }
        }
        if (!binary.Type.IsIntOrIntVectorTy()) { MarkOverdefined(binary); return; }
        var range = left.AsConstantRange(binary.Type).BinaryOp(binary, right.AsConstantRange(binary.Type));
        Merge(state, binary, LlvmValueLatticeElement.GetRange(range)); Count("binary.range_transfers");
    }
    private void VisitUnary(LlvmUnaryOperator unary)
    {
        var operand = State(unary.Operand);
        if (State(unary).IsOverdefined || operand.IsUnknownOrUndef) return;
        if (IsConstant(operand) && LlvmConstantFolding.ConstantFoldUnaryOpOperand(unary.Opcode,
            Constant(operand, unary.Type)!, layout, metrics) is { } constant)
        { MarkConstant(unary, constant); return; }
        MarkOverdefined(unary);
    }
    private void VisitCompare(LlvmCmpInstruction compare)
    {
        if (State(compare).IsOverdefined) return;
        var left = State(compare.Left); var right = State(compare.Right);
        if (left.GetCompare(compare.Predicate, compare.Type, right, layout, metrics) is { } constant)
        { Merge(State(compare), compare, LlvmValueLatticeElement.Get(constant)); return; }
        if ((left.IsUnknownOrUndef || right.IsUnknownOrUndef) && !IsConstant(State(compare))) return;
        MarkOverdefined(compare);
    }
    private void VisitCast(LlvmCastInstruction cast)
    {
        if (State(cast).IsOverdefined) return;
        var operand = State(cast.Operand);
        if (operand.IsUnknownOrUndef) return;
        if (Constant(operand, cast.Operand.Type) is { } constant &&
            LlvmConstantFolding.ConstantFoldCastOperand(cast.Opcode, constant, cast.Type, layout, metrics) is { } result)
        { Merge(State(cast), cast, LlvmValueLatticeElement.Get(result)); return; }
        if (cast.Type.IsIntOrIntVectorTy() && cast.Operand.Type.IsIntOrIntVectorTy() && cast.Opcode != LlvmOpcode.BitCast)
        {
            var range = operand.AsConstantRange(cast.Operand.Type);
            var resultRange = cast.Opcode == LlvmOpcode.Trunc
                ? range.Truncate(cast.Type.GetScalarSizeInBits(),
                    (cast.HasNoUnsignedWrap ? LlvmConstantRange.NoUnsignedWrap : 0) | (cast.HasNoSignedWrap ? LlvmConstantRange.NoSignedWrap : 0))
                : range.CastOp(cast.Opcode, cast.Type.GetScalarSizeInBits());
            Merge(State(cast), cast, LlvmValueLatticeElement.GetRange(resultRange));
        }
        else MarkOverdefined(cast);
    }
    private void VisitSelect(LlvmSelectInstruction select)
    {
        if (FullyOverdefined(select)) return;
        var condition = State(select.Condition);
        if (condition.IsUnknownOrUndef) return;
        if (Constant(condition, select.Condition.Type) is LlvmConstantInt constant)
        {
            var selected = constant.IsZero ? select.FalseValue : select.TrueValue;
            foreach (int field in ElementIndices(select)) Merge(ElementState(select, field), select, ElementState(selected, field));
            return;
        }
        foreach (int field in ElementIndices(select))
        {
            var state = ElementState(select, field);
            bool changed = state.MergeIn(ElementState(select.TrueValue, field));
            changed |= state.MergeIn(ElementState(select.FalseValue, field));
            if (changed) PushUsers(select);
        }
    }
    private void VisitFreeze(LlvmFreezeInstruction freeze)
    {
        if (freeze.Type.IsStructTy()) { MarkOverdefined(freeze); return; }
        var operand = State(freeze.Operand);
        if (State(freeze).IsOverdefined || operand.IsUnknownOrUndef) return;
        if (IsConstant(operand) && Constant(operand, freeze.Type) is { } constant &&
            constant is not LlvmUndefValue && LlvmValueTracking.IsGuaranteedNotToBePoison(constant, metrics: metrics))
        { MarkConstant(freeze, constant); return; }
        MarkOverdefined(freeze);
    }
}
