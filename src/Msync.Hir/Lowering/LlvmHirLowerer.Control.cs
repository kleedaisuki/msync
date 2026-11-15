using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public enum LlvmIteratorOperation : byte { Open, Next, Close }

public static partial class LlvmHirLowerer
{
    private sealed partial class Body
    {
        private readonly List<LoopState> loops = [];
        private readonly List<LlvmValue> iterators = [];
        private long forCount;
        private long iteratorCloseCount;
        private long loopCount;
        private long breakCount;
        private int loopDepth;

        private sealed class LoopState(HirLoopId id, LlvmBasicBlock exit, int iteratorDepth, int acquireDepth, bool producesValue = true)
        {
            internal HirLoopId Id { get; } = id;
            internal HirTypeId ResultType { get; init; }
            internal int IteratorDepth { get; } = iteratorDepth;
            internal int AcquireDepth { get; } = acquireDepth;
            internal bool ProducesValue { get; } = producesValue;
            internal LlvmBasicBlock Exit { get; } = exit;
            internal List<(LlvmValue Value, LlvmBasicBlock Block)> Incoming { get; } = [];
        }

        private LlvmValue Loop(HirNode node)
        {
            var header = new LlvmBasicBlock(owner.context, "loop.header", function);
            var exit = new LlvmBasicBlock(owner.context, "loop.exit", function);
            var state = new LoopState(new(node.Data), exit, iterators.Count, acquireRegions.Count) { ResultType = node.Type };
            loops.Add(state);
            loopCount++;
            loopDepth = Math.Max(loopDepth, loops.Count);
            Add(new LlvmUncondBrInstruction(header));
            block = header;
            Child(node, 0);
            current = node;
            if (!block.HasTerminator) Add(new LlvmUncondBrInstruction(header));
            loops.RemoveAt(loops.Count - 1);
            block = exit;
            return MergeLoopResult(state, node);
        }

        private LlvmValue Break(HirNode node)
        {
            var state = loops.Last(item => item.Id.Value == node.Data);
            var value = !state.ProducesValue || node.Children.Count == 0 ? owner.Null : Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            current = node;
            if (state.ProducesValue) value = Represent(value, state.ResultType);
            CloseIterators(state.IteratorDepth);
            CloseAcquire(state.AcquireDepth);
            state.Incoming.Add((value, block));
            breakCount++;
            Add(new LlvmUncondBrInstruction(state.Exit));
            return owner.Null;
        }

        private LlvmValue For(HirNode node)
        {
            var iteration = Hir.Fors[node.Data];
            var producer = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            current = node;
            var capability = IteratorCall(LlvmIteratorOperation.Open, owner.DataPointer, [producer]);
            bool capturedItem = owner.captured.Contains(iteration.Item);
            var itemType = Hir.Locations[iteration.Item.Value].StorageType;
            bool itemNullable = Hir.Locations[iteration.Item.Value].AdmitsNull;
            var itemCell = capturedItem ? StackCell(itemType, itemNullable) : Address(iteration.Item);
            var header = new LlvmBasicBlock(owner.context, "for.header", function);
            var body = new LlvmBasicBlock(owner.context, "for.body", function);
            var exhausted = new LlvmBasicBlock(owner.context, "for.exhausted", function);
            var exit = new LlvmBasicBlock(owner.context, "for.exit", function);
            var state = new LoopState(iteration.Loop, exit, iterators.Count, acquireRegions.Count, false);
            loops.Add(state);
            iterators.Add(capability);
            forCount++;
            loopDepth = Math.Max(loopDepth, loops.Count);
            Add(new LlvmUncondBrInstruction(header));
            block = header;
            var available = IteratorCall(LlvmIteratorOperation.Next, LlvmIntegerType.Get(owner.context, 1),
                [capability, itemCell]);
            available.LogicalType = owner.metadata.RuntimeType(TypeRef.Bool);
            Add(new LlvmCondBrInstruction(available, body, exhausted));
            block = body;
            if (capturedItem)
            {
                var address = FreshLocation(iteration.Item);
                var item = Add(new LlvmLoadInstruction(owner.Type(itemType, itemNullable), itemCell)
                    { LogicalType = owner.metadata.Type(itemType, itemNullable) });
                Add(new LlvmStoreInstruction(item, address));
            }
            Child(node, 1);
            current = node;
            if (!block.HasTerminator) Add(new LlvmUncondBrInstruction(header));
            block = exhausted;
            CloseIterators(state.IteratorDepth);
            Add(new LlvmUncondBrInstruction(exit));
            iterators.RemoveAt(iterators.Count - 1);
            loops.RemoveAt(loops.Count - 1);
            block = exit;
            return owner.Null;
        }

        private LlvmCallInstruction IteratorCall(LlvmIteratorOperation operation, LlvmType result, IReadOnlyList<LlvmValue> arguments)
        {
            string name = operation switch
            {
                LlvmIteratorOperation.Open => "msync.iterator.open",
                LlvmIteratorOperation.Next => "msync.iterator.next",
                _ => "msync.iterator.close"
            };
            var signature = LlvmFunctionType.Get(result, arguments.Select(argument => argument.Type).ToArray());
            var target = owner.module.GetFunction(System.Text.Encoding.UTF8.GetBytes(name)) ??
                LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage, name, owner.module);
            owner.iteratorCalls[target] = operation;
            return Add(new LlvmCallInstruction(signature, target, arguments));
        }

        private void CloseIterators(int depth)
        {
            for (int index = iterators.Count - 1; index >= depth; index--)
            {
                var close = IteratorCall(LlvmIteratorOperation.Close, owner.Null.Type, [iterators[index]]);
                close.LogicalType = owner.metadata.RuntimeType(TypeRef.Null);
                iteratorCloseCount++;
            }
        }

        private LlvmValue MergeLoopResult(LoopState state, HirNode node)
        {
            if (state.Incoming.Count == 0)
            {
                Add(new LlvmUnreachableInstruction(owner.context));
                return owner.Null;
            }
            if (state.Incoming.Count == 1) return state.Incoming[0].Value;
            var result = Add(new LlvmPhiInstruction(owner.Type(node.Type), state.Incoming.Count));
            foreach (var incoming in state.Incoming) result.AddIncoming(incoming.Value, incoming.Block);
            return result;
        }
    }
}
