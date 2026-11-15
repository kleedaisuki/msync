// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// scalar PatternMatch adapters; all analyses use the shared SimplifyQuery.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool CallsIsZero(LlvmValue value) => value is LlvmConstantInt { IsZero: true };
        private static bool CallsIsOne(LlvmValue value) => value is LlvmConstantInt { IsOne: true };
        private static LlvmConstantInt CallsInt(LlvmType type, ulong value) => LlvmConstantInt.Get(type.Context, new(type.GetScalarSizeInBits(), value));
        private static LlvmConstantInt CallsInt(LlvmType type, LlvmAPInt value) => LlvmConstantInt.Get(type.Context, value);
        private static bool CallsNeg(LlvmValue value, out LlvmValue operand)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { IsZero: true } } neg)
            { operand = neg.Right; return true; }
            operand = value; return false;
        }
        private static bool CallsNot(LlvmValue value, out LlvmValue operand)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt { IsMinusOne: true } } xor)
            { operand = xor.Left; return true; }
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Left: LlvmConstantInt { IsMinusOne: true } } reversed)
            { operand = reversed.Right; return true; }
            operand = value; return false;
        }
        private static bool CallsIntrinsic(LlvmValue value, LlvmIntrinsicKind kind, out LlvmCallBase call)
        {
            if (value is LlvmCallBase candidate && candidate.CalledFunction?.IntrinsicDescriptor?.Kind == kind)
            { call = candidate; return true; }
            call = null!; return false;
        }
        private static bool CallsNotAndDecrement(LlvmValue value, out LlvmValue operand)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.And } andOp)
            {
                if (CallsNot(andOp.Left, out var left) && andOp.Right is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsMinusOne: true } } decrement
                    && ReferenceEquals(left, decrement.Left)) { operand = left; return true; }
                if (CallsNot(andOp.Right, out var right) && andOp.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsMinusOne: true } } other
                    && ReferenceEquals(right, other.Left)) { operand = right; return true; }
            }
            operand = value; return false;
        }
        private LlvmKnownBits CallsKnownBits(LlvmValue value, LlvmInstruction context) => ComputeKnownBits(value, context);
        private bool CallsKnownNonZero(LlvmValue value, LlvmInstruction context) => IsKnownNonZero(value, context);
        private bool CallsPowerOfTwo(LlvmValue value, LlvmInstruction context, bool orZero) => IsKnownToBeAPowerOfTwo(value, orZero, context);
        private static void CallsCopyFunctionAttributes(LlvmCallBase original, LlvmCallBase replacement)
        {
            replacement.MemoryEffectsAttribute = original.MemoryEffectsAttribute;
            replacement.CallingConvention = original.CallingConvention;
            replacement.NoInline = original.NoInline; replacement.NoDuplicate = original.NoDuplicate;
            replacement.Convergent = original.Convergent; replacement.ReturnsTwice = original.ReturnsTwice;
            replacement.NoMerge = original.NoMerge; replacement.NoReturn = original.NoReturn;
            replacement.NoUnwind = original.NoUnwind; replacement.StrictFP = original.StrictFP;
            replacement.NoImplicitFloat = original.NoImplicitFloat; replacement.NoBuiltin = original.NoBuiltin;
            replacement.WillReturnAttribute = original.WillReturnAttribute; replacement.NoFree = original.NoFree;
            replacement.NoSync = original.NoSync; replacement.Speculatable = original.Speculatable;
        }
        private LlvmCallInstruction CallsNewIntrinsic(LlvmCallInstruction original, LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments)
        {
            LlvmFunction declaration = LlvmIntrinsic.GetDeclaration(F.Parent!, kind,
                original.Type, arguments[0].Type, Metrics);
            var replacement = new LlvmCallInstruction(declaration.FunctionType, declaration, arguments, original.GetOperandBundlesAsDefs())
                { LogicalType = original.LogicalType, Source = original.Source };
            Count("calls.intrinsic.recreated");
            return replacement;
        }
    }
}
