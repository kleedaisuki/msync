// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/PHITransAddr.h/.cpp.
// Non-inserting translation and select-side translation; see vendor/llvm-reference/llvm/LICENSE.TXT.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmPhiTransAddr
{
    private readonly LlvmDataLayout layout;
    private readonly Dictionary<string, ulong>? metrics;
    private readonly List<LlvmInstruction> inputs = [];
    public LlvmValue? Address { get; private set; }
    public LlvmPhiTransAddr(LlvmValue address, LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        this.layout = layout;
        this.metrics = metrics;
        Address = AddAsInput(address);
    }

    private LlvmPhiTransAddr(LlvmPhiTransAddr original)
    {
        layout = original.layout;
        metrics = original.metrics;
        Address = original.Address;
        inputs.AddRange(original.inputs);
    }

    public LlvmPhiTransAddr Clone() => new(this);

    public bool NeedsPHITranslationFromBlock(LlvmBasicBlock block) => inputs.Any(input => input.Parent == block);
    public bool IsPotentiallyPHITranslatable() => Address is not LlvmInstruction instruction || CanPHITrans(instruction);
    public LlvmSelectInstruction? GetSelect() => inputs.OfType<LlvmSelectInstruction>().FirstOrDefault();

    public LlvmValue? TranslateValue(LlvmBasicBlock current, LlvmBasicBlock predecessor, LlvmDominatorTree? tree, bool mustDominate)
    {
        Debug.Assert(tree is not null || !mustDominate);
        Count("queries");
        if (tree is not null && tree.IsReachableFromEntry(predecessor))
            Address = TranslateSubExpr(Address!, current, predecessor, tree);
        else { Address = null; Count("unreachable_or_missing_tree"); }
        if (mustDominate && Address is LlvmInstruction instruction && !tree!.Dominates(instruction.Parent, predecessor))
        { Address = null; Count("not_dominating_predecessor"); }
        Count(Address is null ? "failed" : "translated");
        return Address;
    }

    public (LlvmValue? TrueAddress, LlvmValue? FalseAddress) TranslateValue(LlvmBasicBlock current,
        LlvmBasicBlock predecessor, LlvmDominatorTree? tree, LlvmValue condition)
    {
        Count("select_queries");
        if (tree is null || !tree.IsReachableFromEntry(predecessor)) return (null, null);
        LlvmPhiTransAddr whenTrue = new(this), whenFalse = new(this);
        return (whenTrue.TranslateSubExpr(whenTrue.Address!, current, predecessor, tree, condition, true),
            whenFalse.TranslateSubExpr(whenFalse.Address!, current, predecessor, tree, condition, false));
    }

    private static bool CanPHITrans(LlvmInstruction instruction) =>
        instruction is LlvmPhiInstruction or LlvmGetElementPtrInstruction or LlvmCastInstruction ||
        instruction is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt };

    private LlvmValue? TranslateSubExpr(LlvmValue value, LlvmBasicBlock current, LlvmBasicBlock predecessor,
        LlvmDominatorTree? tree, LlvmValue? condition = null, bool conditionValue = false)
    {
        Count("subexpressions");
        if (value is not LlvmInstruction instruction) return value;
        int inputIndex = inputs.IndexOf(instruction);
        if (inputIndex >= 0)
        {
            if (instruction.Parent != current) return instruction;
            inputs.RemoveAt(inputIndex);
            if (instruction is LlvmPhiInstruction phi) return TranslatePhi(phi, predecessor, condition, conditionValue);
            if (!CanPHITrans(instruction)) { Count("unsupported_input_opcode"); return null; }
            foreach (LlvmValue? operand in instruction.Operands) AddAsInput(operand!);
        }
        return instruction switch
        {
            LlvmCastInstruction cast => TranslateCast(cast, current, predecessor, tree, condition, conditionValue),
            LlvmGetElementPtrInstruction gep => TranslateGep(gep, current, predecessor, tree, condition, conditionValue),
            LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt } add =>
                TranslateAdd(add, current, predecessor, tree, condition, conditionValue),
            _ => null
        };
    }

    private LlvmValue TranslatePhi(LlvmPhiInstruction phi, LlvmBasicBlock predecessor, LlvmValue? condition, bool conditionValue)
    {
        Count("phi_incoming");
        LlvmValue incoming = phi.GetIncomingValue(phi.GetBasicBlockIndex(predecessor));
        if (condition is not null && incoming is LlvmSelectInstruction select && select.Condition == condition)
        { Count("phi_incoming_select_side"); return AddAsInput(conditionValue ? select.TrueValue : select.FalseValue); }
        return AddAsInput(incoming);
    }

    private LlvmValue? TranslateCast(LlvmCastInstruction cast, LlvmBasicBlock current, LlvmBasicBlock predecessor,
        LlvmDominatorTree? tree, LlvmValue? condition, bool conditionValue)
    {
        LlvmValue? operand = TranslateSubExpr(cast.Operand, current, predecessor, tree, condition, conditionValue);
        if (operand is null) return null;
        if (operand == cast.Operand) return cast;
        if (LlvmInstructionSimplify.PhiTransSimplifyCast(cast, operand, layout, tree, metrics) is { } simplified)
        {
            RemoveInstInputs(operand);
            Count("cast_simplified");
            return AddAsInput(simplified);
        }
        if (operand is not LlvmConstant)
            foreach (LlvmUser user in operand.Users)
                if (user is LlvmCastInstruction available && available.Opcode == cast.Opcode && available.Type == cast.Type &&
                    (tree is null || tree.Dominates(available.Parent, predecessor)))
                { Count("cast_available"); return available; }
        Count("cast_unavailable");
        return null;
    }

    private LlvmValue? TranslateGep(LlvmGetElementPtrInstruction gep, LlvmBasicBlock current, LlvmBasicBlock predecessor,
        LlvmDominatorTree? tree, LlvmValue? condition, bool conditionValue)
    {
        List<LlvmValue> operands = [];
        bool changed = false;
        foreach (LlvmValue? operand in gep.Operands)
        {
            LlvmValue? translated = TranslateSubExpr(operand!, current, predecessor, tree, condition, conditionValue);
            if (translated is null) return null;
            changed |= translated != operand;
            operands.Add(translated);
        }
        if (!changed) return gep;
        if (LlvmInstructionSimplify.PhiTransSimplifyGep(gep, operands[0], operands.Skip(1).ToArray(), layout, tree, metrics) is { } simplified)
        {
            foreach (LlvmValue operand in operands) RemoveInstInputs(operand);
            Count("gep_simplified");
            return AddAsInput(simplified);
        }
        LlvmValue pointer = operands[0];
        if (pointer is LlvmConstantData) { Count("gep_constant_data_base"); return null; }
        foreach (LlvmUser user in pointer.Users)
            if (user is LlvmGetElementPtrInstruction available && available.Type == gep.Type &&
                available.SourceElementType == gep.SourceElementType && available.OperandCount == operands.Count &&
                available.Parent!.Parent == current.Parent && (tree is null || tree.Dominates(available.Parent, predecessor)) &&
                available.Operands.SequenceEqual(operands))
            { Count("gep_available"); return available; }
        Count("gep_unavailable");
        return null;
    }

    private LlvmValue? TranslateAdd(LlvmBinaryOperator add, LlvmBasicBlock current, LlvmBasicBlock predecessor,
        LlvmDominatorTree? tree, LlvmValue? condition, bool conditionValue)
    {
        LlvmConstantInt right = (LlvmConstantInt)add.Right;
        bool nsw = add.HasNoSignedWrap, nuw = add.HasNoUnsignedWrap;
        LlvmValue? left = TranslateSubExpr(add.Left, current, predecessor, tree, condition, conditionValue);
        if (left is null) return null;
        if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt innerConstant } inner)
        {
            left = inner.Left;
            right = LlvmConstantInt.Get(right.Context, right.Value + innerConstant.Value);
            nsw = nuw = false;
            if (inputs.Contains(inner)) { RemoveInstInputs(inner); AddAsInput(left); }
            Count("add_combined_immediates");
        }
        if (LlvmInstructionSimplify.PhiTransSimplifyAdd(left, right, nsw, nuw, layout, tree, metrics) is { } simplified)
        {
            RemoveInstInputs(left);
            Count("add_simplified");
            return AddAsInput(simplified);
        }
        if (left == add.Left && right == add.Right) return add;
        foreach (LlvmUser user in left.Users)
            if (user is LlvmBinaryOperator available && available.Opcode == LlvmOpcode.Add &&
                available.Left == left && available.Right == right && available.Parent!.Parent == current.Parent &&
                (tree is null || tree.Dominates(available.Parent, predecessor)))
            { Count("add_available"); return available; }
        Count("add_unavailable");
        return null;
    }

    private LlvmValue AddAsInput(LlvmValue value)
    {
        if (value is LlvmInstruction instruction) inputs.Add(instruction);
        return value;
    }

    private void RemoveInstInputs(LlvmValue value)
    {
        if (value is not LlvmInstruction instruction) return;
        int index = inputs.IndexOf(instruction);
        if (index >= 0) { inputs.RemoveAt(index); return; }
        Debug.Assert(instruction is not LlvmPhiInstruction);
        foreach (LlvmValue? operand in instruction.Operands) RemoveInstInputs(operand!);
    }

    private void Count(string outcome)
    {
        if (metrics is not null)
        {
            string key = "phi_translate." + outcome;
            metrics[key] = metrics.GetValueOrDefault(key) + 1;
        }
    }
}
