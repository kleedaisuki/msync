// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SROA.cpp intrinsic/PHI/select rewriting.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class AllocaSliceRewriter
    {
        private bool RewriteIntrinsic(LlvmCallInstruction call)
        {
            if (LlvmMemIntrinsic.TryGet(call, out var memory))
                return memory.IsMemSet ? RewriteMemSet(memory) : RewriteMemTransfer(memory);
            var descriptor = call.CalledFunction!.IntrinsicDescriptor!;
            Debug.Assert(descriptor.IsLifetimeStartOrEnd || call.IsDroppable());
            pass.AddDead(call);
            if (call.IsDroppable())
            {
                oldPointer.DropDroppableUsesIn(call);
                Count("rewriter.assume.dropped");
                return true;
            }
            Debug.Assert(call.GetArgument(0) == oldPointer);
            LlvmValue pointer = SlicePointer(builder, oldPointer.Type);
            if (descriptor.Kind == LlvmIntrinsicKind.LifetimeStart) builder.CreateLifetimeStart(pointer);
            else builder.CreateLifetimeEnd(pointer);
            Count("rewriter.lifetime.rewritten");
            return true;
        }

        private void FixLoadStoreAlignment(LlvmInstruction root)
        {
            HashSet<LlvmInstruction> visited = [root];
            List<LlvmInstruction> users = [root];
            do
            {
                var instruction = users[^1]; users.RemoveAt(users.Count - 1);
                Count("rewriter.alignment.users");
                if (instruction is LlvmLoadInstruction load)
                { if (SliceAlignment() < load.Alignment) load.Alignment = SliceAlignment(); continue; }
                if (instruction is LlvmStoreInstruction store)
                { if (SliceAlignment() < store.Alignment) store.Alignment = SliceAlignment(); continue; }
                Debug.Assert(instruction is LlvmPhiInstruction or LlvmSelectInstruction or LlvmGetElementPtrInstruction or
                    LlvmCastInstruction { Opcode: LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast });
                foreach (var user in instruction.Users)
                    if (visited.Add((LlvmInstruction)user)) users.Add((LlvmInstruction)user);
            } while (users.Count != 0);
        }

        private bool RewritePhi(LlvmPhiInstruction phi)
        {
            Debug.Assert(begin >= allocaBegin && end <= allocaEnd);
            using var guard = new LlvmTransformBuilder.InsertPointGuard(builder);
            if (oldPointer is LlvmPhiInstruction)
            {
                var first = oldPointer.Parent!.FirstInstruction;
                while (first is LlvmPhiInstruction) first = first.Next;
                builder.SetInsertPoint(oldPointer.Parent!, first);
            }
            else builder.SetInsertPoint(oldPointer);
            builder.CurrentSource = oldPointer.Source;
            var pointer = SlicePointer(builder, oldPointer.Type);
            for (int index = 0; index < phi.IncomingCount; ++index)
                if (phi.GetIncomingValue(index) == oldPointer) phi.SetIncomingValue(index, pointer);
            DeleteIfTriviallyDead(oldPointer);
            FixLoadStoreAlignment(phi);
            phiUsers.Add(phi);
            Count("rewriter.phis.rewritten");
            return true;
        }

        private bool RewriteSelect(LlvmSelectInstruction select)
        {
            Debug.Assert(select.TrueValue == oldPointer || select.FalseValue == oldPointer);
            Debug.Assert(begin >= allocaBegin && end <= allocaEnd);
            var pointer = SlicePointer(builder, oldPointer.Type);
            if (select.TrueValue == oldPointer) select.TrueValue = pointer;
            if (select.FalseValue == oldPointer) select.FalseValue = pointer;
            DeleteIfTriviallyDead(oldPointer); FixLoadStoreAlignment(select);
            selectUsers.Add(select);
            Count("rewriter.selects.rewritten");
            return true;
        }
    }
}
