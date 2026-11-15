// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstVisitor.h and InstCombineInternal.h visitation dispatch.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? Visit(LlvmInstruction instruction) => instruction switch
        {
            LlvmUnaryOperator { Opcode: LlvmOpcode.FNeg } unary => VisitFNeg(unary),
            LlvmBinaryOperator binary => binary.Opcode switch
            {
                LlvmOpcode.Add => VisitAdd(binary), LlvmOpcode.FAdd => VisitFAdd(binary),
                LlvmOpcode.Sub => VisitSub(binary), LlvmOpcode.FSub => VisitFSub(binary),
                LlvmOpcode.Mul => VisitMul(binary), LlvmOpcode.FMul => VisitFMul(binary),
                LlvmOpcode.UDiv => VisitUDiv(binary), LlvmOpcode.SDiv => VisitSDiv(binary), LlvmOpcode.FDiv => VisitFDiv(binary),
                LlvmOpcode.URem => VisitURem(binary), LlvmOpcode.SRem => VisitSRem(binary), LlvmOpcode.FRem => VisitFRem(binary),
                LlvmOpcode.Shl => VisitShl(binary), LlvmOpcode.LShr => VisitLShr(binary), LlvmOpcode.AShr => VisitAShr(binary),
                LlvmOpcode.And => VisitAnd(binary), LlvmOpcode.Or => VisitOr(binary), LlvmOpcode.Xor => VisitXor(binary),
                _ => null
            },
            LlvmICmpInstruction compare => VisitICmp(compare),
            LlvmFCmpInstruction compare => VisitFCmpInst(compare),
            LlvmCastInstruction cast => cast.Opcode switch
            {
                LlvmOpcode.Trunc => VisitTrunc(cast), LlvmOpcode.ZExt => VisitZExt(cast), LlvmOpcode.SExt => VisitSExt(cast),
                LlvmOpcode.FPTrunc => VisitFPTrunc(cast), LlvmOpcode.FPExt => VisitFPExt(cast),
                LlvmOpcode.UIToFP => VisitUIToFP(cast), LlvmOpcode.SIToFP => VisitSIToFP(cast),
                LlvmOpcode.FPToUI => VisitFPToUI(cast), LlvmOpcode.FPToSI => VisitFPToSI(cast),
                LlvmOpcode.PtrToInt => VisitPtrToInt(cast), LlvmOpcode.PtrToAddr => VisitPtrToAddr(cast),
                LlvmOpcode.IntToPtr => VisitIntToPtr(cast), LlvmOpcode.BitCast => VisitBitCast(cast),
                LlvmOpcode.AddrSpaceCast => VisitAddrSpaceCast(cast), _ => null
            },
            LlvmPhiInstruction phi => VisitPHINode(phi),
            LlvmSelectInstruction select => VisitSelectInst(select),
            LlvmGetElementPtrInstruction gep => VisitGetElementPtrInst(gep),
            LlvmAllocaInstruction alloca => VisitAllocaInst(alloca),
            LlvmLoadInstruction load => VisitLoadInst(load),
            LlvmStoreInstruction store => VisitStoreInst(store),
            LlvmCallInstruction call => VisitCallInst(call),
            LlvmReturnInstruction ret => VisitReturnInst(ret),
            LlvmUnreachableInstruction unreachable => VisitUnreachableInst(unreachable),
            LlvmUncondBrInstruction branch => VisitUncondBrInst(branch),
            LlvmCondBrInstruction branch => VisitCondBrInst(branch),
            LlvmSwitchInstruction @switch => VisitSwitchInst(@switch),
            LlvmExtractValueInstruction extract => VisitExtractValueInst(extract),
            LlvmInsertValueInstruction insert => VisitInsertValueInst(insert),
            LlvmFreezeInstruction freeze => VisitFreeze(freeze),
            // These three extension opcodes have no upstream combine visitor.
            // They still take part in the real worklist, DCE and effect rules.
            LlvmHeapAllocInst or LlvmFileInInst or LlvmFileOutInst => null,
            _ => null
        };
    }
}
