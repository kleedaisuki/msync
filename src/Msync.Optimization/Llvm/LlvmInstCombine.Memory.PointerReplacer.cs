// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineLoadStoreAlloca.cpp PointerReplacer. Ordered sets retain source
// collection/traversal order; dictionaries carry the source replacement map.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private sealed class MemoryPointerReplacer(Work combiner, LlvmInstruction root, uint fromAddressSpace)
        {
            private readonly HashSet<LlvmInstruction> usersToReplace = [];
            private readonly Dictionary<LlvmValue, LlvmValue> replacements = [];
            private bool IsAvailable(LlvmInstruction instruction) => instruction == root || usersToReplace.Contains(instruction);
            private LlvmValue? GetReplacement(LlvmValue value) => replacements.GetValueOrDefault(value);
            private bool IsEqualOrValidAddressSpaceCast(LlvmInstruction instruction, uint addressSpace) =>
                instruction is LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast } cast &&
                (addressSpace == MemoryAddressSpace(cast) || combiner.IsValidAddrSpaceCast(addressSpace, MemoryAddressSpace(cast)));

            internal bool CollectUsers()
            {
                var pending = new Stack<LlvmInstruction>();
                var revisit = new HashSet<LlvmInstruction>();
                void PushUsers(LlvmInstruction instruction)
                {
                    foreach (var user in instruction.Users)
                        if (user is LlvmInstruction candidate && !IsAvailable(candidate) && !revisit.Contains(candidate)) pending.Push(candidate);
                }
                bool TryPushOperand(LlvmInstruction instruction)
                {
                    if (usersToReplace.Contains(instruction)) return true;
                    if (!revisit.Add(instruction)) return false;
                    pending.Push(instruction);
                    return true;
                }
                PushUsers(root);
                while (pending.Count != 0)
                {
                    var instruction = pending.Pop();
                    combiner.Count("instcombine.memory.pointer_replacer.collect");
                    if (instruction is LlvmLoadInstruction load)
                    {
                        if (load.IsVolatile) return false;
                        usersToReplace.Add(load);
                    }
                    else if (instruction is LlvmPhiInstruction phi)
                    {
                        bool replaceable = Enumerable.Range(0, phi.IncomingCount).All(i => phi.GetIncomingValue(i) is LlvmInstruction);
                        if (replaceable && Enumerable.Range(0, phi.IncomingCount).All(i => IsAvailable((LlvmInstruction)phi.GetIncomingValue(i))))
                        { usersToReplace.Add(phi); PushUsers(phi); continue; }
                        if (!replaceable || !revisit.Add(phi)) return false;
                        pending.Push(phi);
                        for (int i = 0; i < phi.IncomingCount; ++i)
                            if (!TryPushOperand((LlvmInstruction)phi.GetIncomingValue(i))) return false;
                    }
                    else if (instruction is LlvmSelectInstruction select)
                    {
                        if (select.TrueValue is not LlvmInstruction whenTrue || select.FalseValue is not LlvmInstruction whenFalse) return false;
                        if (IsAvailable(whenTrue) && IsAvailable(whenFalse))
                        { usersToReplace.Add(select); PushUsers(select); continue; }
                        pending.Push(select);
                        if (!TryPushOperand(whenTrue) || !TryPushOperand(whenFalse)) return false;
                    }
                    else if (instruction is LlvmGetElementPtrInstruction gep)
                    {
                        if (gep.PointerOperand is not LlvmInstruction pointer) return false;
                        if (IsAvailable(pointer)) { usersToReplace.Add(gep); PushUsers(gep); continue; }
                        pending.Push(gep);
                        if (!TryPushOperand(pointer)) return false;
                    }
                    else if (instruction is LlvmCallBase call && MemoryIsTransfer(call))
                    {
                        if (MemoryIsVolatile(call)) return false;
                        usersToReplace.Add(call);
                    }
                    else if (IsEqualOrValidAddressSpaceCast(instruction, fromAddressSpace))
                    { usersToReplace.Add(instruction); PushUsers(instruction); }
                    else if (!MemoryIsLifetime(instruction)) return false;
                }
                return true;
            }

            internal void ReplacePointer(LlvmValue value)
            {
                replacements[root] = value;
                var pending = new List<LlvmInstruction> { root };
                var postorder = new List<LlvmInstruction>();
                var postorderSet = new HashSet<LlvmInstruction>();
                var visited = new HashSet<LlvmInstruction>();
                while (pending.Count != 0)
                {
                    var instruction = pending[^1];
                    if (visited.Add(instruction))
                    {
                        foreach (var user in instruction.Users)
                        {
                            var candidate = (LlvmInstruction)user;
                            if (usersToReplace.Contains(candidate) && !visited.Contains(candidate)) pending.Add(candidate);
                        }
                    }
                    else
                    {
                        if (postorderSet.Add(instruction)) postorder.Add(instruction);
                        pending.RemoveAt(pending.Count - 1);
                    }
                }
                for (int i = postorder.Count - 1; i >= 0; --i) Replace(postorder[i]);
            }

            private void Replace(LlvmInstruction instruction)
            {
                if (GetReplacement(instruction) is not null) return;
                combiner.Count("instcombine.memory.pointer_replacer.rewrites");
                switch (instruction)
                {
                    case LlvmLoadInstruction load:
                    {
                        var replacement = new LlvmLoadInstruction(load.Type, GetReplacement(load.PointerOperand)!, load.Alignment,
                            load.IsVolatile, load.Ordering, load.SyncScopeId)
                            { IsElementwise = load.IsElementwise, LogicalType = load.LogicalType };
                        replacement.GetOperandUse(0).LogicalType = load.GetOperandUse(0).LogicalType;
                        replacement.TakeName(load);
                        combiner.InsertNewInstWith(replacement, load);
                        combiner.ReplaceInstUsesWith(load, replacement);
                        replacements[replacement] = replacement;
                        break;
                    }
                    case LlvmPhiInstruction phi:
                    {
                        var firstIncoming = phi.GetIncomingValue(0);
                        var type = GetReplacement(firstIncoming)?.Type ?? firstIncoming.Type;
                        if (phi.Type == type)
                        {
                            for (int i = 0; i < phi.IncomingCount; ++i)
                                phi.SetIncomingValue(i, GetReplacement(phi.GetIncomingValue(i)) ?? phi.GetIncomingValue(i));
                            replacements[phi] = phi;
                            break;
                        }
                        var replacement = new LlvmPhiInstruction(type, phi.IncomingCount) { LogicalType = phi.LogicalType };
                        combiner.InsertNewInstWith(replacement, phi);
                        replacement.TakeName(phi);
                        replacements[phi] = replacement;
                        for (int i = 0; i < phi.IncomingCount; ++i)
                        {
                            replacement.AddIncoming(GetReplacement(phi.GetIncomingValue(i))!, phi.GetIncomingBlock(i));
                            replacement.GetOperandUse(i).LogicalType = phi.GetOperandUse(i).LogicalType;
                        }
                        break;
                    }
                    case LlvmGetElementPtrInstruction gep:
                    {
                        var replacement = new LlvmGetElementPtrInstruction(gep.SourceElementType,
                            GetReplacement(gep.PointerOperand)!, gep.Indices.ToArray(), gep.NoWrapFlags) { LogicalType = gep.LogicalType };
                        for (int i = 0; i < gep.OperandCount; ++i)
                            replacement.GetOperandUse(i).LogicalType = gep.GetOperandUse(i).LogicalType;
                        combiner.InsertNewInstWith(replacement, gep);
                        replacement.TakeName(gep);
                        replacements[gep] = replacement;
                        break;
                    }
                    case LlvmSelectInstruction select:
                    {
                        var whenTrue = GetReplacement(select.TrueValue) ?? select.TrueValue;
                        var whenFalse = GetReplacement(select.FalseValue) ?? select.FalseValue;
                        var replacement = new LlvmSelectInstruction(select.Condition, whenTrue, whenFalse) { LogicalType = select.LogicalType };
                        for (int i = 0; i < select.OperandCount; ++i)
                            replacement.GetOperandUse(i).LogicalType = select.GetOperandUse(i).LogicalType;
                        combiner.InsertNewInstWith(replacement, select);
                        replacement.TakeName(select);
                        replacements[select] = replacement;
                        break;
                    }
                    case LlvmCallBase call when MemoryIsTransfer(call):
                    {
                        var destination = GetReplacement(call.GetArgument(0)) ?? call.GetArgument(0);
                        var source = GetReplacement(call.GetArgument(1)) ?? call.GetArgument(1);
                        combiner.Builder.SetInsertPoint(call);
                        var replacement = LlvmIntrinsic.CreateMemoryIntrinsic(call.CalledFunction!.IntrinsicDescriptor!.Kind,
                            [destination, source, call.GetArgument(2), call.GetArgument(3)], combiner.F.Parent!, combiner.Metrics);
                        replacement.SetParamAlign(0, call.GetParamAlign(0));
                        replacement.SetParamAlign(1, call.GetParamAlign(1));
                        combiner.Builder.Insert(replacement);
                        combiner.EraseInstFromFunction(call);
                        replacements[call] = replacement;
                        break;
                    }
                    case LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast } cast:
                    {
                        var pointer = GetReplacement(cast.Operand)!;
                        if (MemoryAddressSpace(pointer) != MemoryAddressSpace(cast))
                        {
                            var replacement = new LlvmCastInstruction(LlvmOpcode.AddrSpaceCast, pointer, cast.Type) { LogicalType = cast.LogicalType };
                            replacement.GetOperandUse(0).LogicalType = cast.GetOperandUse(0).LogicalType;
                            replacement.TakeName(cast);
                            combiner.InsertNewInstWith(replacement, cast);
                            replacements[cast] = replacement;
                        }
                        else replacements[cast] = pointer;
                        break;
                    }
                    default:
                        throw new InvalidOperationException("PointerReplacer reached an instruction outside its collected source cases.");
                }
            }
        }
    }
}
