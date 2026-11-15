using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class IntegerSimplifyProperties
{
    internal static int Run()
    {
        int checkedCases = 0;
        foreach (uint width in new uint[] { 1, 8, 32, 65, 129 })
        {
            LlvmContext context = new();
            LlvmModule module = new("integer-simplify", context);
            LlvmIntegerType type = LlvmIntegerType.Get(context, width);
            LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type, type, LlvmType.GetInt1Ty(context)]),
                LlvmLinkage.InternalLinkage, "identities", module);
            LlvmBasicBlock entry = new(context, "entry", function);
            LlvmValue x = function.Arguments[0], y = function.Arguments[1];
            var zero = LlvmConstantInt.Get(type, 0);
            var one = LlvmConstantInt.Get(type, 1);
            var all = LlvmConstantInt.Get(context, LlvmAPInt.GetAllOnes(width));
            LlvmValue? Fold(LlvmOpcode op, LlvmValue a, LlvmValue b) =>
                LlvmInstructionSimplify.SimplifyBinOp(op, a, b, module.DataLayout);
            Program.Require(Fold(LlvmOpcode.Add, x, new LlvmBinaryOperator(LlvmOpcode.Sub, y, x)) == y,
                "X+(Y-X) must simplify to the existing Y, across integer widths.");
            Program.Require(Fold(LlvmOpcode.Add, x, new LlvmBinaryOperator(LlvmOpcode.Xor, x, all)) == all,
                "X+~X must simplify to all ones.");
            Program.Require(Fold(LlvmOpcode.Sub, new LlvmBinaryOperator(LlvmOpcode.Add, x, y), y) == x,
                "Source subtract reassociation must cancel the existing Y.");
            Program.Require(Fold(LlvmOpcode.Mul, x, one) == x && Fold(LlvmOpcode.Mul, x, zero) == zero,
                "Multiply identities must retain existing SSA values.");
            Program.Require(Fold(LlvmOpcode.UDiv, x, x) == one && Fold(LlvmOpcode.URem, x, x) == zero,
                "Division/remainder self-identities differ.");
            Program.Require(Fold(LlvmOpcode.Shl, x, LlvmUndefValue.Get(type)) is LlvmPoisonValue &&
                Fold(LlvmOpcode.UDiv, x, zero) is LlvmPoisonValue,
                "LLVM undefined shift/division operands must yield poison.");
            var noWrapShift = new LlvmBinaryOperator(LlvmOpcode.Shl, all, y) { HasNoUnsignedWrap = true };
            entry.Append(noWrapShift);
            Program.Require(LlvmInstructionSimplify.SimplifyInstruction(noWrapShift) == all,
                "NUW shifting a negative constant can only preserve that existing constant.");
            var odd = new LlvmBinaryOperator(LlvmOpcode.Or, x, one);
            var exactShift = new LlvmBinaryOperator(LlvmOpcode.LShr, odd, y) { IsExact = true };
            entry.Append(odd); entry.Append(exactShift);
            Program.Require(LlvmInstructionSimplify.SimplifyInstruction(exactShift) == odd,
                "An exact right shift cannot shift out a known set low bit.");
            var signedDivision = new LlvmBinaryOperator(LlvmOpcode.SDiv, x, y) { IsExact = true };
            var unsignedDivision = new LlvmBinaryOperator(LlvmOpcode.UDiv, x, y) { IsExact = true };
            var select = new LlvmSelectInstruction(function.Arguments[2], signedDivision, unsignedDivision);
            Program.Require(Fold(LlvmOpcode.Mul, select, y) == x,
                "Binary select threading must expose exact-division cancellation on both arms.");
            LlvmBasicBlock signedBlock = new(context, "signed", function);
            LlvmBasicBlock unsignedBlock = new(context, "unsigned", function);
            LlvmBasicBlock merge = new(context, "merge", function);
            entry.Append(new LlvmCondBrInstruction(function.Arguments[2], signedBlock, unsignedBlock));
            signedBlock.Append(signedDivision); signedBlock.Append(new LlvmUncondBrInstruction(merge));
            unsignedBlock.Append(unsignedDivision); unsignedBlock.Append(new LlvmUncondBrInstruction(merge));
            var phi = new LlvmPhiInstruction(type);
            phi.AddIncoming(signedDivision, signedBlock); phi.AddIncoming(unsignedDivision, unsignedBlock);
            merge.Append(phi); merge.Append(new LlvmReturnInstruction(context, phi));
            Program.Require(Fold(LlvmOpcode.Mul, phi, y) == x,
                "Binary PHI threading must evaluate each incoming edge and agree on X.");
            checkedCases += 10;
        }
        return checkedCases;
    }
}
