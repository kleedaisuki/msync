using System.Collections.Immutable;
using static Msync.Optimization.Execution.IndexedMethodPlan;

namespace Msync.Optimization.Execution;

internal sealed partial class IndexedMethodImporter
{
    private int NewSlot(NativeWorkerType? type = null)
    {
        int slot = types.Count;
        types.Add(type);
        parents.Add(slot);
        return slot;
    }

    private int Root(int slot)
    {
        while (parents[slot] != slot)
        {
            parents[slot] = parents[parents[slot]];
            slot = parents[slot];
        }
        return slot;
    }

    private bool Require(int slot, NativeWorkerType type)
    {
        int root = Root(slot);
        if (types[root] is { } actual) return actual == type;
        types[root] = type;
        return true;
    }

    private bool Unify(int left, int right)
    {
        int a = Root(left), b = Root(right);
        if (a == b) return true;
        var x = types[a];
        var y = types[b];
        if (x is not null && y is not null && x != y) return false;
        parents[b] = a;
        types[a] = x ?? y;
        return true;
    }

    private bool ResolveTypes(out ImmutableArray<NativeWorkerType> slots)
    {
        var resultTypes = ImmutableArray.CreateBuilder<NativeWorkerType>(types.Count);
        for (int slot = 0; slot < types.Count; slot++)
            resultTypes.Add(types[Root(slot)] ?? NativeWorkerType.Number);
        slots = resultTypes.MoveToImmutable();
        foreach (var (access, slot) in inputs)
            if (accesses[access] is { Kind: AccessKind.Slot, Operand: var formal }
                && !Compatible(SlotType(formal), slots[slot])) return false;
        if (returnsNull) return body.ReturnType == TypeRef.Null;
        return prefix ? guestResult < 0 || Compatible(body.ReturnType, slots[guestResult])
            : Compatible(body.ReturnType, slots[result]);
    }

    private static bool Compatible(TypeRef type, NativeWorkerType slot) =>
        (type.Kind & (slot == NativeWorkerType.Bool ? TypeKind.Bool : TypeKind.Number)) != 0;

    private bool FinalizeCode(ImmutableArray<NativeWorkerType> slots)
    {
        for (int i = 0; i < code.Count; i++)
        {
            var op = code[i];
            switch (op.Op)
            {
                case NativeWorkerOp.Jump or NativeWorkerOp.BranchIfTrue or NativeWorkerOp.BranchIfFalse:
                    int target;
                    if (op.Data < 0) target = ~op.Data;
                    else if (!entries.TryGetValue(op.Data, out target)) return false;
                    if ((uint)target >= (uint)code.Count) return false;
                    code[i] = op with { Data = target };
                    break;
                case NativeWorkerOp.NumberConstant when slots[op.Destination] == NativeWorkerType.Index:
                    if (!writeNumbers && BitConverter.DoubleToInt64Bits(op.Constant) != 0)
                        return false;
                    if (!(op.Constant >= 0 && op.Constant <= int.MaxValue && op.Constant == Math.Truncate(op.Constant))
                        || BitConverter.DoubleToInt64Bits(op.Constant) == long.MinValue) return false;
                    code[i] = op with { Op = NativeWorkerOp.IndexConstant, Data = (int)op.Constant };
                    break;
                case NativeWorkerOp.NumberConstant when slots[op.Destination] != NativeWorkerType.Number:
                    return false;
                case NativeWorkerOp.NumberAdd or NativeWorkerOp.NumberMultiply
                    when slots[op.Destination] != NativeWorkerType.Number:
                    if (slots[op.Destination] != NativeWorkerType.Index || !writeNumbers) return false;
                    if (op.Op == NativeWorkerOp.NumberAdd) code[i] = op with { Op = NativeWorkerOp.IndexAdd };
                    else if (IndexConstant(op.Left, i) == 2)
                        code[i] = op with { Op = NativeWorkerOp.IndexAdd, Left = op.Right };
                    else if (IndexConstant(op.Right, i) == 2)
                        code[i] = op with { Op = NativeWorkerOp.IndexAdd, Right = op.Left };
                    else return false;
                    break;
                case NativeWorkerOp.NumberEqual or NativeWorkerOp.NumberNotEqual
                    or NativeWorkerOp.NumberLess or NativeWorkerOp.NumberGreaterEqual:
                    if (slots[op.Left] == NativeWorkerType.Bool) return false;
                    if (slots[op.Left] == NativeWorkerType.Index)
                        code[i] = op with
                        {
                            Op = op.Op switch
                            {
                                NativeWorkerOp.NumberEqual => NativeWorkerOp.IndexEqual,
                                NativeWorkerOp.NumberNotEqual => NativeWorkerOp.IndexNotEqual,
                                NativeWorkerOp.NumberLess => NativeWorkerOp.IndexLess,
                                _ => NativeWorkerOp.IndexGreaterEqual
                            }
                        };
                    break;
            }
        }
        return true;
    }

    private int? IndexConstant(int slot, int before)
    {
        for (int pc = before - 1; pc >= 0; pc--)
            if (code[pc].Op == NativeWorkerOp.IndexConstant && code[pc].Destination == slot)
                return code[pc].Data;
        return null;
    }
}
