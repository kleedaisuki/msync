// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombinePHI.cpp 130–355,779–857.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool FoldIntegerTypedPHI(LlvmPhiInstruction phi)
        {
            if (!phi.Type.IsIntegerTy() || !phi.HasOneUse || phi.Users.First() is not LlvmCastInstruction { Opcode: LlvmOpcode.IntToPtr } intToPtr) return false;
            bool hasPointerUse = intToPtr.Users.Any(user => user switch
            {
                LlvmLoadInstruction load => load.PointerOperand == intToPtr,
                LlvmStoreInstruction store => store.PointerOperand == intToPtr,
                LlvmGetElementPtrInstruction gep => gep.PointerOperand == intToPtr,
                _ => false
            });
            if (!hasPointerUse || DL.GetPointerSizeInBits(((LlvmPointerType)intToPtr.Type).AddressSpace) != DL.GetTypeSizeInBits(phi.Type).GetFixedValue()) return false;
            List<LlvmValue> available = [];
            for (int i = 0; i < phi.IncomingCount; ++i)
            {
                var value = phi.GetIncomingValue(i); var block = phi.GetIncomingBlock(i);
                Count("phi.integer_pointer.incoming_visited");
                if (value is not (LlvmInstruction or LlvmArgument)) return false;
                if (value is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } ptrToInt && ptrToInt.Operand.Type == intToPtr.Type)
                { available.Add(ptrToInt.Operand); continue; }
                var forward = value.Users.OfType<LlvmCastInstruction>().FirstOrDefault(cast => cast.Opcode == LlvmOpcode.IntToPtr &&
                    cast.Type == intToPtr.Type && (DT.Dominates(cast, block) || cast.Parent == block));
                if (forward is not null) { available.Add(forward); continue; }
                if (value is LlvmPhiInstruction) { available.Add(value); continue; }
                if (value is not LlvmLoadInstruction { HasOneUse: true }) return false;
                available.Add(value);
            }
            LlvmPhiInstruction? matching = null; uint count = 0;
            foreach (var candidate in PhiNodes(phi.Parent!))
            {
                if (count++ > CLOpts.MaxNumPhis) { Count("phi.integer_pointer.phi_budget"); return false; }
                Count("phi.integer_pointer.match_candidate");
                if (candidate == phi || candidate.Type != intToPtr.Type) continue;
                bool match = true;
                for (int i = 0; i < phi.IncomingCount; ++i)
                    if (candidate.GetIncomingValue(candidate.GetBasicBlockIndex(phi.GetIncomingBlock(i))) != available[i]) { match = false; break; }
                if (match) { matching = candidate; break; }
            }
            if (matching is not null)
            {
                ReplaceInstUsesWith(intToPtr, matching); EraseInstFromFunction(intToPtr); EraseInstFromFunction(phi);
                Count("phi.integer_pointer.existing_phi"); return true;
            }
            if (available.All(value => value.Type != intToPtr.Type || value is LlvmCastInstruction { Opcode: LlvmOpcode.IntToPtr }))
            { Count("phi.integer_pointer.all_operands_require_conversion"); return false; }
            if (available.Any(value => value.Type != intToPtr.Type && value is LlvmInstruction instruction &&
                (instruction.IsTerminator || instruction is LlvmPhiInstruction && PhiInsertionPoint(instruction.Parent!) is null))) return false;
            var result = new LlvmPhiInstruction(intToPtr.Type, phi.IncomingCount) { LogicalType = intToPtr.LogicalType };
            result.SetName(phi.Name + ".ptr"); InsertNewInstBefore(result, phi);
            Dictionary<LlvmValue,LlvmInstruction> casts = [];
            for (int i = 0; i < phi.IncomingCount; ++i)
            {
                var value = available[i]; var incoming = phi.GetIncomingBlock(i);
                if (value.Type == intToPtr.Type) { result.AddIncoming(value, incoming); continue; }
                if (!casts.TryGetValue(value, out var cast))
                {
                    cast = new LlvmCastInstruction(value.Type.IsPointerTy() ? LlvmOpcode.BitCast : LlvmOpcode.IntToPtr, value, intToPtr.Type)
                    { LogicalType = intToPtr.LogicalType };
                    cast.SetName(value.Name + ".ptr");
                    LlvmInstruction before = value is LlvmInstruction definition ?
                        (definition is LlvmPhiInstruction ? PhiInsertionPoint(definition.Parent!)! : definition.Next!) : PhiInsertionPoint(F.BasicBlocks[0])!;
                    InsertNewInstBefore(cast, before); casts.Add(value, cast);
                }
                result.AddIncoming(cast, incoming);
            }
            ReplaceInstUsesWith(intToPtr, result); EraseInstFromFunction(intToPtr); EraseInstFromFunction(phi);
            Count("phi.integer_pointer.new_phi"); return true;
        }
        private LlvmInstruction? FoldPHIArgIntToPtrToPHI(LlvmPhiInstruction phi)
        {
            if (!phi.Users.All(user => user is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt })) return null;
            bool changed = false;
            for (int i = 0; i < phi.IncomingCount; ++i)
                if (SimplifyIntToPtrRoundTripCast(phi.GetIncomingValue(i)) is { } result)
                { ReplaceOperand(phi, i, result); changed = true; }
            if (changed) Count("phi.roundtrip_cast");
            return changed ? phi : null;
        }
        private LlvmInstruction? FoldPHIArgZextsIntoPHI(LlvmPhiInstruction phi)
        {
            if (phi.Parent!.GetTerminator().IsEHPad || phi.IncomingCount < 3) return null;
            LlvmType? narrow = Enumerable.Range(0, phi.IncomingCount).Select(phi.GetIncomingValue)
                .OfType<LlvmCastInstruction>().FirstOrDefault(cast => cast.Opcode == LlvmOpcode.ZExt)?.Operand.Type;
            if (narrow is null) return null;
            List<LlvmValue> incoming = []; int zexts = 0, constants = 0;
            for (int i = 0; i < phi.IncomingCount; ++i)
            {
                var value = phi.GetIncomingValue(i);
                if (value is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } cast)
                {
                    if (cast.Operand.Type != narrow || !cast.HasOneUser) return null;
                    incoming.Add(cast.Operand); ++zexts;
                }
                else if (value is LlvmConstant constant)
                {
                    var truncated = GetLosslessUnsignedTrunc(constant, narrow);
                    if (truncated is null) return null;
                    incoming.Add(truncated); ++constants;
                }
                else return null;
            }
            if (constants == 0 || zexts < 2) return null;
            var result = new LlvmPhiInstruction(narrow, phi.IncomingCount); result.SetName(phi.Name + ".shrunk");
            for (int i = 0; i < phi.IncomingCount; ++i) result.AddIncoming(incoming[i], phi.GetIncomingBlock(i));
            InsertNewInstBefore(result, phi); Count("phi.zext_shrunk");
            return new LlvmCastInstruction(narrow == phi.Type ? LlvmOpcode.BitCast : LlvmOpcode.ZExt, result, phi.Type);
        }
    }
}
