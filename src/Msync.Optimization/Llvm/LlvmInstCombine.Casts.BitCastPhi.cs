// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp 3288–3470.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool HasStoreUsersOnly(LlvmCastInstruction instruction) => instruction.Users.All(user => user is LlvmStoreInstruction);
        private LlvmInstruction? OptimizeBitCastFromPhi(LlvmCastInstruction instruction, LlvmPhiInstruction phi)
        {
            Count("casts.bitcast_phi.queries");
            if (HasStoreUsersOnly(instruction)) return null;
            LlvmType sourceType = instruction.Operand.Type, destinationType = instruction.Type;
            var work = new Stack<LlvmPhiInstruction>();
            var seen = new HashSet<LlvmPhiInstruction>();
            List<LlvmPhiInstruction> oldPhis = [];
            seen.Add(phi); oldPhis.Add(phi); work.Push(phi);
            while (work.TryPop(out var oldPhi))
                foreach (var incoming in oldPhi.Operands)
                {
                    Count("casts.bitcast_phi.incoming_scanned");
                    if (incoming is LlvmConstant) continue;
                    if (incoming is LlvmLoadInstruction load)
                    {
                        if (load.PointerOperand == instruction || load.PointerOperand is LlvmLoadInstruction ||
                            destinationType.IsX86_AMXTy() || !load.HasOneUse || !load.IsSimple) return null;
                        continue;
                    }
                    if (incoming is LlvmPhiInstruction previous)
                    {
                        if (seen.Add(previous)) { oldPhis.Add(previous); work.Push(previous); }
                        continue;
                    }
                    if (incoming is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast ||
                        cast.Operand.Type != destinationType || cast.Type != sourceType) return null;
                }
            foreach (var oldPhi in oldPhis)
                foreach (var user in oldPhi.Users)
                {
                    Count("casts.bitcast_phi.users_scanned");
                    if (user is LlvmStoreInstruction store)
                    { if (!store.IsSimple || store.ValueOperand != oldPhi) return null; }
                    else if (user is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast)
                    { if (cast.Type != destinationType || cast.Operand.Type != sourceType) return null; }
                    else if (user is not LlvmPhiInstruction peer || !seen.Contains(peer)) return null;
                }
            var replacements = new Dictionary<LlvmPhiInstruction, LlvmPhiInstruction>();
            foreach (var oldPhi in oldPhis)
            {
                Builder.SetInsertPoint(oldPhi);
                replacements.Add(oldPhi, Builder.CreatePHI(destinationType, oldPhi.IncomingCount));
            }
            foreach (var oldPhi in oldPhis)
            {
                var replacement = replacements[oldPhi];
                for (int index = 0; index < oldPhi.IncomingCount; index++)
                {
                    var incoming = oldPhi.GetIncomingValue(index);
                    LlvmValue rewritten;
                    if (incoming is LlvmConstant constant)
                        rewritten = LlvmConstantExpr.GetBitCast(constant, destinationType);
                    else if (incoming is LlvmLoadInstruction load)
                    {
                        Builder.SetInsertPoint(load);
                        rewritten = CombineLoadToNewType(load, destinationType);
                        ReplaceInstUsesWith(load, LlvmPoisonValue.Get(load.Type));
                        EraseInstFromFunction(load);
                    }
                    else if (incoming is LlvmCastInstruction cast) rewritten = cast.Operand;
                    else rewritten = replacements[(LlvmPhiInstruction)incoming];
                    replacement.AddIncoming(rewritten, oldPhi.GetIncomingBlock(index));
                }
            }
            LlvmInstruction? result = null;
            foreach (var oldPhi in oldPhis)
            {
                var replacement = replacements[oldPhi];
                foreach (var user in oldPhi.Users.ToArray())
                {
                    if (user is LlvmStoreInstruction store)
                    {
                        Builder.SetInsertPoint(store);
                        store.ValueOperand = Builder.CreateBitCast(replacement, sourceType);
                        Worklist.Push(store);
                    }
                    else if (user is LlvmCastInstruction cast)
                    {
                        var rewritten = ReplaceInstUsesWith(cast, replacement);
                        if (cast == instruction) result = rewritten;
                    }
                }
            }
            Count("casts.bitcast_phi.rewrites");
            return result;
        }
    }
}
