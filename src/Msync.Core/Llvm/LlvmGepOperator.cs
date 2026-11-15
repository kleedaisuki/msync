// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Operator.h
// GEPOperator view over a GEP instruction or constant expression.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmGepOperator
{
    private readonly LlvmUser? user;
    private LlvmGepOperator(LlvmUser user) => this.user = user;
    public LlvmUser User => user!;
    public LlvmType Type => User.Type;
    public string Name => User.Name;
    public LlvmContext Context => User.Context;
    public LlvmType SourceElementType => User is LlvmGetElementPtrInstruction instruction
        ? instruction.SourceElementType : ((LlvmConstantExpr)User).SourceElementType!;
    public LlvmType ResultElementType => User is LlvmGetElementPtrInstruction instruction
        ? instruction.ResultElementType : ((LlvmConstantExpr)User).ResultElementType!;
    public LlvmValue PointerOperand => User.GetOperand(0)!;
    public int IndexCount => User.OperandCount - 1;
    public LlvmValue GetIndex(int index) => User.GetOperand(index + 1)!;
    public IEnumerable<LlvmValue> Indices => Enumerable.Range(0, IndexCount).Select(GetIndex);
    public LlvmGepNoWrapFlags NoWrapFlags => User is LlvmGetElementPtrInstruction instruction
        ? instruction.NoWrapFlags : ((LlvmConstantExpr)User).NoWrapFlags;
    public bool IsInBounds => NoWrapFlags.IsInBounds;
    public bool HasNoUnsignedWrap => NoWrapFlags.HasNoUnsignedWrap;
    public bool HasNoUnsignedSignedWrap => NoWrapFlags.HasNoUnsignedSignedWrap;
    public bool HasOneUse => User.HasOneUse;
    public bool HasAllConstantIndices => Indices.All(value => value is LlvmConstantInt);
    public bool HasAllZeroIndices => Indices.All(value => value is LlvmConstantInt { IsZero: true });

    public static bool TryGet(LlvmValue value, out LlvmGepOperator result)
    {
        if (value is LlvmGetElementPtrInstruction || value is LlvmConstantExpr { Opcode: LlvmOpcode.GetElementPtr })
        {
            result = new((LlvmUser)value);
            return true;
        }
        result = default;
        return false;
    }

    public static LlvmGepOperator Get(LlvmValue value)
    {
        bool found = TryGet(value, out var result);
        Debug.Assert(found);
        return result;
    }
}
