// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Instruction.h,
// lib/IR/Instruction.cpp; license in vendor/llvm-reference/llvm/LICENSE.TXT.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public abstract partial class LlvmInstruction : LlvmUser
{
    protected LlvmInstruction(LlvmType type, LlvmOpcode opcode, int operandCount)
        : base(type, (byte)((byte)LlvmValueKind.InstructionVal + (byte)opcode), operandCount) => Opcode = opcode;

    protected LlvmInstruction(LlvmType type, LlvmOpcode opcode, IReadOnlyList<LlvmValue?> operands)
        : base(type, (byte)((byte)LlvmValueKind.InstructionVal + (byte)opcode), operands) => Opcode = opcode;

    public LlvmOpcode Opcode { get; }
    public LlvmBasicBlock? Parent { get; internal set; }
    public LlvmInstruction? Previous { get; internal set; }
    public LlvmInstruction? Next { get; internal set; }
    public SourceSpan? Source { get; set; }
    public bool IsTerminator => Opcode is >= LlvmOpcode.Ret and <= LlvmOpcode.CallBr;
    public bool IsBinaryOperator => Opcode is >= LlvmOpcode.Add and <= LlvmOpcode.Xor;
    public bool IsCast => Opcode is >= LlvmOpcode.Trunc and <= LlvmOpcode.AddrSpaceCast;
    public bool IsUnaryOperator => Opcode == LlvmOpcode.FNeg;
    public virtual int SuccessorCount => 0;
    public virtual LlvmBasicBlock GetSuccessor(int index) => throw new InvalidOperationException("Instruction has no successors.");
    public virtual void SetSuccessor(int index, LlvmBasicBlock successor) => throw new InvalidOperationException("Instruction has no successors.");

    public IEnumerable<LlvmBasicBlock> Successors
    {
        get { for (int index = 0; index < SuccessorCount; index++) yield return GetSuccessor(index); }
    }

    public void ReplaceSuccessorWith(LlvmBasicBlock previous, LlvmBasicBlock replacement)
    {
        Debug.Assert(IsTerminator);
        for (int index = 0; index < SuccessorCount; index++)
            if (ReferenceEquals(GetSuccessor(index), previous)) SetSuccessor(index, replacement);
    }

    public LlvmInstruction Clone()
    {
        LlvmInstruction clone = CloneImpl();
        clone.Source = Source;
        clone.LogicalType = LogicalType;
        clone.SubclassOptionalData = SubclassOptionalData;
        for (int i = 0; i < OperandCount; ++i)
        {
            clone.GetOperandUse(i).LogicalType = GetOperandUse(i).LogicalType;
            clone.GetOperandUse(i).TargetStorageType = GetOperandUse(i).TargetStorageType;
        }
        return clone;
    }

    protected abstract LlvmInstruction CloneImpl();
    public void RemoveFromParent() => Parent!.Remove(this);
    public void EraseFromParent()
    {
        Debug.Assert(UseEmpty);
        RemoveFromParent();
        DeleteValue();
    }

    internal static LlvmType VoidType(LlvmContext context) => LlvmType.GetVoidTy(context);
    internal static LlvmType ComparisonType(LlvmType operand)
    {
        LlvmIntegerType boolean = LlvmIntegerType.Get(operand.Context, 1);
        return operand is LlvmVectorType vector ? LlvmVectorType.Get(boolean, vector.ElementCount) : boolean;
    }
}

[Flags]
public enum LlvmFastMathFlags : byte
{
    None = 0,
    AllowReassoc = 1 << 0,
    NoNaNs = 1 << 1,
    NoInfs = 1 << 2,
    NoSignedZeros = 1 << 3,
    AllowReciprocal = 1 << 4,
    AllowContract = 1 << 5,
    ApproxFunc = 1 << 6,
    Fast = (1 << 7) - 1
}

public interface ILlvmFastMathInstruction
{
    LlvmFastMathFlags FastMathFlags { get; set; }
}
