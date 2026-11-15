// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombinePHI.cpp 959–1011,1397–1609.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool PHIsEqualValue(LlvmPhiInstruction phi, ref LlvmValue? nonPhi, HashSet<LlvmPhiInstruction> visited)
        {
            if (!visited.Add(phi)) return true;
            if (visited.Count >= 16) return false;
            for (int i = 0; i < phi.IncomingCount; ++i)
            {
                var value = phi.GetIncomingValue(i);
                if (value is LlvmPhiInstruction next)
                {
                    if (PHIsEqualValue(next, ref nonPhi, visited)) continue;
                    if (nonPhi is not null) return false;
                    nonPhi = next;
                }
                else if (value != nonPhi) return false;
            }
            return true;
        }
        private static LlvmConstantInt GetAnyNonZeroConstInt(LlvmPhiInstruction phi)
        {
            for (int i = 0; i < phi.IncomingCount; ++i)
                if (phi.GetIncomingValue(i) is LlvmConstantInt { IsZero: false } value) return value;
            return LlvmConstantInt.Get((LlvmIntegerType)phi.Type, 1);
        }
        private LlvmInstruction? VisitPHINode(LlvmPhiInstruction phi)
        {
            Count("phi.visited");
            if (LlvmInstructionSimplify.SimplifyInstruction(phi, Query(phi)) is { } simplified)
                return ReplaceInstUsesWith(phi, simplified);
            if (FoldPHIArgZextsIntoPHI(phi) is { } zext) return zext;
            if (FoldPHIArgIntToPtrToPHI(phi) is { } pointer) return pointer;
            if (phi.GetIncomingValue(0) is LlvmInstruction first && phi.GetIncomingValue(1) is LlvmInstruction second &&
                first.Opcode == second.Opcode && first.HasOneUser && FoldPHIArgOpIntoPHI(phi) is { } operation) return operation;
            if (phi.Type.IsPointerTy() && PhiInsertionPoint(phi.Parent!) is not null)
            {
                var value = phi.GetIncomingValue(0); var stripped = LlvmValueTracking.StripPointerCasts(value, Metrics);
                HashSet<LlvmValue> checkedValues = [value];
                if (value != stripped && Enumerable.Range(0, phi.IncomingCount).All(index =>
                    !checkedValues.Add(phi.GetIncomingValue(index)) || stripped == LlvmValueTracking.StripPointerCasts(phi.GetIncomingValue(index), Metrics)))
                {
                    Count("phi.pointer_cast_merged");
                    return new LlvmCastInstruction(((LlvmPointerType)stripped.Type).AddressSpace == ((LlvmPointerType)phi.Type).AddressSpace ?
                        LlvmOpcode.BitCast : LlvmOpcode.AddrSpaceCast, stripped, phi.Type);
                }
            }
            if (FoldDeadPhiWeb(phi)) return null;
            if (phi.HasOneUse)
            {
                if (FoldIntegerTypedPHI(phi)) return null;
                var user = (LlvmInstruction)phi.Users.First();
                if (user.HasOneUse && user is LlvmBinaryOperator or LlvmUnaryOperator or LlvmGetElementPtrInstruction && user.Users.First() == phi)
                { Count("phi.unused_induction_cycle"); return ReplaceInstUsesWith(phi, LlvmPoisonValue.Get(phi.Type)); }
            }
            if (!phi.HasNUsesOrMore(3))
            {
                List<LlvmInstruction> dropFlags = [];
                bool allCompare = phi.Users.All(user =>
                {
                    var compare = user as LlvmICmpInstruction;
                    if (compare is null && user is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true } binary &&
                        (binary.Left == phi || binary.Right == phi))
                    { dropFlags.Add(binary); compare = binary.Users.First() as LlvmICmpInstruction; }
                    return compare is not null && phi.Type is LlvmIntegerType &&
                        compare.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe && compare.Right is LlvmConstantInt { IsZero: true };
                });
                if (allCompare)
                {
                    LlvmConstantInt? constant = null; bool changed = false;
                    for (int i = 0; i < phi.IncomingCount; ++i)
                    {
                        var value = phi.GetIncomingValue(i);
                        if (!LlvmKnownBitsAnalysis.IsKnownNonZero(value, context: phi.GetIncomingBlock(i).GetTerminator(), tree: DT,
                            metrics: Metrics, useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals)) continue;
                        constant ??= GetAnyNonZeroConstInt(phi);
                        if (constant == value) continue;
                        ReplaceOperand(phi, i, constant);
                        foreach (var instruction in dropFlags) instruction.DropPoisonGeneratingFlags();
                        changed = true; Count("phi.nonzero_input_replaced");
                    }
                    if (changed) return phi;
                }
            }
            int incomingIndex = 0;
            while (incomingIndex != phi.IncomingCount && phi.GetIncomingValue(incomingIndex) is LlvmPhiInstruction) ++incomingIndex;
            LlvmValue? nonPhi = incomingIndex != phi.IncomingCount ? phi.GetIncomingValue(incomingIndex) : null;
            if (nonPhi is not null)
                for (++incomingIndex; incomingIndex != phi.IncomingCount; ++incomingIndex)
                    if (phi.GetIncomingValue(incomingIndex) != nonPhi && phi.GetIncomingValue(incomingIndex) is not LlvmPhiInstruction) break;
            if (incomingIndex == phi.IncomingCount && PHIsEqualValue(phi, ref nonPhi, []))
            { Count("phi.equal_cycle"); return ReplaceInstUsesWith(phi, nonPhi!); }
            if (PredOrder.TryGetValue(phi.Parent!, out var predecessors))
            {
                for (int i = 0; i < phi.IncomingCount; ++i)
                {
                    var a = phi.GetIncomingBlock(i); var b = predecessors[i]; if (a == b) continue;
                    int j = phi.GetBasicBlockIndex(b);
                    var va = phi.GetIncomingValue(i); var vb = phi.GetIncomingValue(j);
                    int la = phi.GetOperandUse(i).LogicalType, lb = phi.GetOperandUse(j).LogicalType;
                    phi.SetIncomingBlock(i, b); phi.SetIncomingValue(i, vb); phi.GetOperandUse(i).LogicalType = lb;
                    phi.SetIncomingBlock(j, a); phi.SetIncomingValue(j, va); phi.GetOperandUse(j).LogicalType = la;
                    Count("phi.predecessor_reordered");
                }
            }
            else PredOrder.Add(phi.Parent!, Enumerable.Range(0, phi.IncomingCount).Select(phi.GetIncomingBlock).ToList());
            foreach (var candidate in PhiNodes(phi.Parent!))
            {
                if (candidate == phi || !phi.IsIdenticalToWhenDefined(candidate)) continue;
                Count("phi.cse"); return ReplaceInstUsesWith(phi, candidate);
            }
            if (phi.Type is LlvmIntegerType integer && !DL.IsLegalInteger(integer.BitWidth) && SliceUpIllegalIntegerPHI(phi) is { } sliced) return sliced;
            if (SimplifyPhiUsingControlFlow(phi) is { } condition) return ReplaceInstUsesWith(phi, condition);
            if (FoldDependentIVs(phi) is { } recurrence) return ReplaceInstUsesWith(phi, recurrence);
            return null;
        }
    }
}
