// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombinePHI.cpp 1013–1245.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private readonly record struct PHIUsageRecord(int PhiId, uint Shift, LlvmInstruction Instruction);
        private LlvmInstruction? SliceUpIllegalIntegerPHI(LlvmPhiInstruction first)
        {
            List<PHIUsageRecord> users = []; List<LlvmPhiInstruction> phis = [first]; HashSet<LlvmPhiInstruction> inspected = [first];
            for (int id = 0; id != phis.Count; ++id)
            {
                foreach (var user in phis[id].Users.Cast<LlvmInstruction>())
                {
                    Count("phi.slice.user_inspected");
                    if (user is LlvmPhiInstruction next)
                    { if (inspected.Add(next)) phis.Add(next); continue; }
                    if (user is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc }) { users.Add(new(id, 0, user)); continue; }
                    if (user is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, HasOneUse: true, Right: LlvmConstantInt amount } shift ||
                        shift.Users.First() is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc ||
                        amount.Value.Uge(new LlvmAPInt(amount.Value.BitWidth, ((LlvmIntegerType)user.Type).BitWidth))) return null;
                    users.Add(new(id, (uint)amount.Value.GetZExtValue(), trunc));
                }
            }
            foreach (var phi in phis)
            {
                for (int i = 0; i < phi.IncomingCount; ++i)
                {
                    if (phi.GetIncomingValue(i) is LlvmInstruction { Opcode: LlvmOpcode.Invoke } invoke && invoke.Parent == phi.GetIncomingBlock(i)) return null;
                    if (PhiInsertionPoint(phi.GetIncomingBlock(i)) is null) return null;
                }
            }
            if (users.Count == 0) return ReplaceInstUsesWith(first, LlvmPoisonValue.Get(first.Type));
            users.Sort((a,b) =>
            {
                int order = a.PhiId.CompareTo(b.PhiId); if (order != 0) return order;
                order = a.Shift.CompareTo(b.Shift); if (order != 0) return order;
                return ((LlvmIntegerType)a.Instruction.Type).BitWidth.CompareTo(((LlvmIntegerType)b.Instruction.Type).BitWidth);
            });
            Dictionary<LlvmBasicBlock,LlvmValue> predecessorValues = [];
            Dictionary<(LlvmPhiInstruction Phi,uint Shift,uint Width),LlvmPhiInstruction> extracted = [];
            for (int i = 0; i < users.Count; ++i)
            {
                var record = users[i]; var phi = phis[record.PhiId]; uint offset = record.Shift;
                var type = (LlvmIntegerType)record.Instruction.Type; var key = (phi,offset,type.BitWidth);
                if (!extracted.TryGetValue(key, out var piece))
                {
                    piece = new LlvmPhiInstruction(type, phi.IncomingCount) { LogicalType = record.Instruction.LogicalType };
                    piece.SetName(phi.Name + ".off" + offset); InsertNewInstBefore(piece, phi);
                    for (int incoming = 0; incoming < phi.IncomingCount; ++incoming)
                    {
                        var predecessor = phi.GetIncomingBlock(incoming); var value = phi.GetIncomingValue(incoming);
                        if (predecessorValues.TryGetValue(predecessor, out var previous)) { piece.AddIncoming(previous, predecessor); continue; }
                        if (value == phi) { predecessorValues[predecessor] = piece; piece.AddIncoming(piece, predecessor); continue; }
                        // Keep the upstream lookup key (current PN, not incoming PN).
                        if (extracted.TryGetValue(key, out var lowered))
                        { predecessorValues[predecessor] = lowered; piece.AddIncoming(lowered, predecessor); continue; }
                        Builder.SetInsertPoint(predecessor.GetTerminator());
                        LlvmValue result = value;
                        if (offset != 0) result = Builder.CreateBinOp(LlvmOpcode.LShr, result, LlvmConstantInt.Get((LlvmIntegerType)value.Type, offset), "extract");
                        result = Builder.CreateTrunc(result, type, "extract.t");
                        predecessorValues[predecessor] = result; piece.AddIncoming(result, predecessor);
                        if (value is LlvmPhiInstruction inputPhi && inspected.Contains(inputPhi))
                            users.Add(new(phis.IndexOf(inputPhi), offset, (LlvmInstruction)result));
                    }
                    predecessorValues.Clear(); extracted.Add(key, piece); Count("phi.slice.piece_created");
                }
                ReplaceInstUsesWith(record.Instruction, piece); Count("phi.slice.extract_replaced");
            }
            var poison = LlvmPoisonValue.Get(first.Type);
            foreach (var phi in phis.Skip(1)) ReplaceInstUsesWith(phi, poison);
            return ReplaceInstUsesWith(first, poison);
        }
    }
}
