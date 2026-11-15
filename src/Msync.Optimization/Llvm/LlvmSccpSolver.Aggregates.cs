// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Utils/SCCPSolver.cpp aggregate, GEP and load visitors.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmSccpSolver
{
    private void VisitExtract(LlvmExtractValueInstruction extract)
    {
        if (extract.Type.IsStructTy() || State(extract).IsOverdefined || extract.IndexCount != 1)
        { MarkOverdefined(extract); return; }
        if (extract.AggregateOperand.Type is LlvmStructType)
            Merge(State(extract), extract, FieldState(extract.AggregateOperand, (int)extract.Indices[0]));
        else MarkOverdefined(extract);
    }
    private void VisitInsert(LlvmInsertValueInstruction insert)
    {
        if (insert.Type is not LlvmStructType structure || insert.IndexCount != 1)
        { MarkOverdefined(insert); return; }
        for (int i = 0; i < structure.NumElements; ++i)
        {
            var state = FieldState(insert, i);
            if ((uint)i != insert.Indices[0]) Merge(state, insert, FieldState(insert.AggregateOperand, i));
            else if (insert.InsertedValueOperand.Type.IsStructTy())
            { if (state.MarkOverdefined()) PushUsers(insert); }
            else Merge(state, insert, State(insert.InsertedValueOperand));
        }
    }
    private void VisitGep(LlvmGetElementPtrInstruction gep)
    {
        if (State(gep).IsOverdefined) return;
        var pointer = State(gep.PointerOperand);
        if (pointer.IsUnknownOrUndef) return;
        if (pointer.IsNotConstant && pointer.GetNotConstant().IsNullValue)
        {
            if (gep.NoWrapFlags.HasNoUnsignedWrap || gep.NoWrapFlags.IsInBounds &&
                ((LlvmPointerType)gep.Type.GetScalarType()).AddressSpace == 0)
            {
                if (State(gep).MarkNotConstant(LlvmConstantPointerNull.Get(gep.Type))) PushUsers(gep);
            }
            else MarkOverdefined(gep);
            return;
        }
        var operands = new List<LlvmConstant>();
        for (int i = 0; i < gep.OperandCount; ++i)
        {
            var operand = gep.GetOperand(i)!;
            var state = State(operand);
            if (state.IsUnknownOrUndef) return;
            if (Constant(state, operand.Type) is not { } constant) { MarkOverdefined(gep); return; }
            operands.Add(constant);
        }
        if (LlvmConstantFolding.ConstantFoldInstOperands(gep, operands, layout, metrics: metrics) is { } result)
        {
            Merge(State(gep), gep, LlvmValueLatticeElement.Get(result));
            if (pointer.MayHaveDifferentProvenance) State(gep).MayHaveDifferentProvenance = true;
        }
        else MarkOverdefined(gep);
    }
    private void VisitLoad(LlvmLoadInstruction load)
    {
        if (load.Type.IsStructTy() || load.IsVolatile || State(load).IsOverdefined)
        { MarkOverdefined(load); return; }
        var pointer = State(load.PointerOperand);
        if (pointer.IsUnknownOrUndef) return;
        if (IsConstant(pointer))
        {
            var constant = Constant(pointer, load.PointerOperand.Type)!;
            if (constant is LlvmConstantPointerNull)
            {
                if (((LlvmPointerType)load.PointerOperand.Type).AddressSpace != 0) MarkOverdefined(load);
                return;
            }
            if (LlvmConstantFolding.ConstantFoldLoadFromConstPtr(constant, load.Type, layout, metrics) is { } loaded)
            { MarkConstant(load, loaded); return; }
        }
        // getValueFromMetadata without metadata returns overdefined; no global tracking in this solver.
        Merge(State(load), load, LlvmValueLatticeElement.GetOverdefined());
    }
}
