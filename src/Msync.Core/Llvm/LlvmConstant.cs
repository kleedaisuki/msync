// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from llvm/IR/Constant.h and llvm/IR/Constants.h,
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
namespace Msync.Core.Llvm;

public abstract partial class LlvmConstant : LlvmUser
{
    protected const byte IsNullValueBit = 1 << 6;
    protected LlvmConstant(LlvmType type, byte subclassId, int operandCount) : base(type, subclassId, operandCount) { }
    protected LlvmConstant(LlvmType type, byte subclassId, IReadOnlyList<LlvmValue?> operands) : base(type, subclassId, operands) { }
    public bool IsNullValue => (SubclassOptionalData & IsNullValueBit) != 0;

    internal virtual void HandleOperandChange(LlvmValue from, LlvmValue to) =>
        throw new NotSupportedException("Operand replacement for this LLVM constant subclass has not been ported.");
}

public abstract class LlvmConstantData : LlvmConstant
{
    protected LlvmConstantData(LlvmType type, LlvmValueKind kind) : base(type, (byte)kind, 0) { }
}
