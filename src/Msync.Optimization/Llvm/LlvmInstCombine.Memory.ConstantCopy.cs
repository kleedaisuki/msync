// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineLoadStoreAlloca.cpp isOnlyCopiedFromConstantMemory and
// isDereferenceableForAllocaSize. Memory intrinsic identity comes from the
// source registry, never an ordinary runtime function name.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool MemoryIsTransfer(LlvmCallBase call)
        {
            var kind = call.CalledFunction?.IntrinsicDescriptor?.Kind;
            return kind == LlvmIntrinsicKind.MemCpy || kind == LlvmIntrinsicKind.MemMove || kind == LlvmIntrinsicKind.MemCpyInline;
        }
        private static bool MemoryIsLifetime(LlvmInstruction instruction) =>
            instruction is LlvmCallBase { CalledFunction.IntrinsicDescriptor.IsLifetimeStartOrEnd: true };
        private static bool MemoryIsVolatile(LlvmCallBase call) => ((LlvmConstantInt)call.GetArgument(3)).IsOne;

        private LlvmCallBase? IsOnlyCopiedFromConstantMemory(LlvmAllocaInstruction alloca, List<LlvmInstruction> toDelete)
        {
            LlvmCallBase? copy = null;
            var pending = new Stack<(LlvmValue Value, bool IsOffset)>();
            var visited = new HashSet<(LlvmValue Value, bool IsOffset)>();
            pending.Push((alloca, false));
            while (pending.Count != 0)
            {
                var item = pending.Pop();
                if (!visited.Add(item)) continue;
                if ((uint)visited.Count > CLOpts.MaxCopiedFromConstantUsers)
                { Count("instcombine.memory.constant_copy.user_limit"); return null; }
                foreach (var use in item.Value.Uses)
                {
                    Count("instcombine.memory.constant_copy.uses");
                    var instruction = (LlvmInstruction)use.User;
                    if (instruction is LlvmLoadInstruction load)
                    { if (!load.IsSimple) return null; continue; }
                    if (instruction is LlvmPhiInstruction or LlvmSelectInstruction)
                    { pending.Push((instruction, true)); continue; }
                    if (instruction is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast })
                    { pending.Push((instruction, item.IsOffset)); continue; }
                    if (instruction is LlvmGetElementPtrInstruction gep)
                    { pending.Push((instruction, item.IsOffset || !gep.HasAllZeroIndices)); continue; }
                    if (instruction is LlvmCallBase call)
                    {
                        if (call.IsCallee(use)) continue;
                        int dataOperand = call.GetDataOperandNo(use);
                        if (call.IsArgOperand(use) && call.IsInAllocaArgument(dataOperand)) return null;
                        if (call.DoesNotCapture(dataOperand) && (call.OnlyReadsMemory() || call.OnlyReadsMemory(dataOperand))) continue;
                    }
                    if (MemoryIsLifetime(instruction)) { toDelete.Add(instruction); continue; }
                    if (instruction is not LlvmCallBase transfer || !MemoryIsTransfer(transfer) || MemoryIsVolatile(transfer)) return null;
                    if (use.OperandNo == 1) continue;
                    if (copy is not null || item.IsOffset || use.OperandNo != 0) return null;
                    if (AA.GetModRefInfoMask(LlvmMemoryLocation.GetBeforeOrAfter(LlvmValueTracking.StripPointerCasts(transfer.GetArgument(1)))).IsModSet()) return null;
                    copy = transfer;
                }
            }
            return copy;
        }

        private bool IsDereferenceableForAllocaSize(LlvmValue value, LlvmAllocaInstruction alloca)
        {
            var size = LlvmValueTracking.GetAllocationSize(alloca, DL);
            if (size is null || size.Value.IsScalable) return false;
            return LlvmValueTracking.IsDereferenceableAndAlignedPointer(value, alloca.Alignment,
                new LlvmAPInt(64, size.Value.GetFixedValue()), DL, metrics: Metrics);
        }
    }
}
