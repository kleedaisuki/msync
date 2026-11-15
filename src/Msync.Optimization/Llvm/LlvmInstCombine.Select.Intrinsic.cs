// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// isConstantBeneficialInIntrinsicArg/foldSelectIntoIntrinsicArg/foldSelectIntrinsic.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool IsConstantBeneficialInIntrinsicArg(LlvmIntrinsicKind kind, int argument) =>
            kind != LlvmIntrinsicKind.CopySign || argument == 1;
        private LlvmInstruction? FoldSelectIntoIntrinsicArg(LlvmSelectInstruction select, LlvmCallInstruction whenTrue, LlvmCallInstruction whenFalse)
        {
            if (!ReferenceEquals(whenTrue.CalledFunction, whenFalse.CalledFunction)) return null;
            int? difference = null;
            for (int index = 0; index < whenTrue.ArgumentCount; ++index)
            {
                if (ReferenceEquals(whenTrue.GetArgument(index), whenFalse.GetArgument(index))) continue;
                if (difference.HasValue) return null;
                difference = index;
            }
            if (!difference.HasValue || !LlvmLocal.CanReplaceOperandWithVariable(whenTrue, difference.Value)) return null;
            LlvmValue trueValue = whenTrue.GetArgument(difference.Value), falseValue = whenFalse.GetArgument(difference.Value);
            LlvmIntrinsicKind kind = whenTrue.CalledFunction!.IntrinsicDescriptor!.Kind;
            if ((trueValue is LlvmConstant || falseValue is LlvmConstant) && IsConstantBeneficialInIntrinsicArg(kind, difference.Value)) return null;
            LlvmValue[] arguments = whenTrue.Arguments.ToArray();
            arguments[difference.Value] = Builder.CreateSelect(select.Condition, trueValue, falseValue, select.Name + ".v");
            LlvmCallInstruction result = new(whenTrue.FunctionType, whenTrue.CalledFunction, arguments);
            result.CopyIRFlags(whenTrue); result.AndIRFlags(whenFalse);
            return result;
        }
        private LlvmInstruction? FoldSelectIntrinsic(LlvmSelectInstruction select)
        {
            if (select.TrueValue is not LlvmCallInstruction whenTrue || whenTrue.CalledFunction?.IntrinsicDescriptor is not { } descriptor ||
                select.FalseValue is not LlvmCallInstruction whenFalse || whenFalse.CalledFunction?.IntrinsicDescriptor?.Kind != descriptor.Kind ||
                !whenTrue.HasOneUse || !whenFalse.HasOneUse) return null;
            LlvmIntrinsicKind kind = descriptor.Kind;
            if (kind == LlvmIntrinsicKind.Abs || kind == LlvmIntrinsicKind.CTTZ || kind == LlvmIntrinsicKind.CTLZ)
            {
                LlvmValue newSelect = Builder.CreateSelect(select.Condition, whenTrue.GetArgument(0), whenFalse.GetArgument(0));
                LlvmValue newPoisonFlag = Builder.CreateAnd(whenTrue.GetArgument(1), whenFalse.GetArgument(1));
                return ReplaceInstUsesWith(select, Builder.CreateBinaryIntrinsic(kind, newSelect, newPoisonFlag));
            }
            return LlvmIntrinsic.IsTriviallyVectorizable(kind) ? FoldSelectIntoIntrinsicArg(select, whenTrue, whenFalse) : null;
        }
    }
}
