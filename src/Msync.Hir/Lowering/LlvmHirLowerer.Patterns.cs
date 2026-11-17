using System.Globalization;
using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Body
    {
        private sealed record PatternCursor(LlvmValue Handle, LlvmValue Producer, HirTypeId Element,
            LlvmValue Open, LlvmValue Done, LlvmValue[] Ready, LlvmValue[] Items);

        private readonly Dictionary<int, PatternCursor> patternCursors = [];

        private static int PatternIndex(HirNode node) => int.Parse(node.Text!, CultureInfo.InvariantCulture);

        private LlvmValue PatternRegion(HirNode node)
        {
            if (node.Children.Count != 3)
                throw new LlvmHirLoweringException(node.Source, "unspecialized pattern cursor");
            var producer = Child(node, 0);
            if (block.HasTerminator) return owner.Null;
            current = node;
            var element = new HirTypeId(Hir.Node(Hir.Children(node)[2]).Data);
            int width = PatternIndex(node);
            var handle = IteratorCall(LlvmIteratorOperation.Create, owner.DataPointer, []);
            var cursor = new PatternCursor(handle, producer, element, Flag(), Flag(),
                new LlvmValue[width], new LlvmValue[width]);
            for (int i = 0; i < width; i++)
            {
                cursor.Ready[i] = Flag();
                cursor.Items[i] = StackCell(element);
            }
            owner.trace?.Detail?.Metric("pattern.cursor_cells", width);
            int depth = iterators.Count;
            iterators.Add(handle);
            patternCursors.Add(node.Data, cursor);
            var result = Child(node, 1);
            if (!block.HasTerminator)
            {
                current = node;
                CloseIterators(depth);
            }
            patternCursors.Remove(node.Data);
            iterators.RemoveAt(depth);
            return result;

            LlvmValue Flag()
            {
                var cell = StackCell(LlvmIntegerType.Get(owner.context, 1), owner.metadata.RuntimeType(TypeRef.Bool));
                Add(new LlvmStoreInstruction(PatternBoolean(false), cell));
                return cell;
            }
        }

        private LlvmValue ReadPatternItem(HirNode node)
        {
            var cursor = patternCursors[node.Data];
            return Add(new LlvmLoadInstruction(owner.Type(cursor.Element), cursor.Items[PatternIndex(node)])
                { LogicalType = owner.metadata.Type(cursor.Element) });
        }

        private LlvmValue PatternDemand(HirNode node)
        {
            var cursor = patternCursors[node.Data];
            int requested = PatternIndex(node);
            var check = new LlvmBasicBlock(owner.context, "prefix.check", function);
            var start = new LlvmBasicBlock(owner.context, "prefix.open-check", function);
            var open = new LlvmBasicBlock(owner.context, "prefix.open", function);
            var next = new LlvmBasicBlock(owner.context, "prefix.items", function);
            var noMore = new LlvmBasicBlock(owner.context, "prefix.exhausted", function);
            var yes = new LlvmBasicBlock(owner.context, "prefix.available", function);
            var no = new LlvmBasicBlock(owner.context, "prefix.missing", function);
            var done = new LlvmBasicBlock(owner.context, "prefix.end", function);
            Test(cursor.Ready[requested - 1], yes, check);
            block = check;
            Test(cursor.Done, no, start);
            block = start;
            Test(cursor.Open, next, open);
            block = open;
            IteratorCall(LlvmIteratorOperation.OpenAt, owner.Null.Type, [cursor.Handle, cursor.Producer]);
            Add(new LlvmStoreInstruction(PatternBoolean(true), cursor.Open));
            Add(new LlvmUncondBrInstruction(next));
            block = next;
            for (int item = 0; item < requested; item++)
            {
                var read = new LlvmBasicBlock(owner.context, "prefix.read", function);
                var retained = new LlvmBasicBlock(owner.context, "prefix.retain", function);
                var following = item == requested - 1 ? yes : new LlvmBasicBlock(owner.context, "prefix.following", function);
                Test(cursor.Ready[item], following, read);
                block = read;
                var available = IteratorCall(LlvmIteratorOperation.Next, LlvmIntegerType.Get(owner.context, 1),
                    [cursor.Handle, cursor.Items[item]]);
                available.LogicalType = owner.metadata.RuntimeType(TypeRef.Bool);
                Add(new LlvmCondBrInstruction(available, retained, noMore));
                block = retained;
                Add(new LlvmStoreInstruction(PatternBoolean(true), cursor.Ready[item]));
                Add(new LlvmUncondBrInstruction(following));
                block = following;
            }
            block = noMore;
            Add(new LlvmStoreInstruction(PatternBoolean(true), cursor.Done));
            Add(new LlvmUncondBrInstruction(no));
            block = yes;
            Add(new LlvmUncondBrInstruction(done));
            block = no;
            Add(new LlvmUncondBrInstruction(done));
            block = done;
            owner.trace?.Detail?.Metric("pattern.cursor_demand_sites", 1);
            var result = Add(new LlvmPhiInstruction(LlvmIntegerType.Get(owner.context, 1), 2)
                { LogicalType = owner.metadata.RuntimeType(TypeRef.Bool) });
            result.AddIncoming(PatternBoolean(true), yes);
            result.AddIncoming(PatternBoolean(false), no);
            return result;

            void Test(LlvmValue cell, LlvmBasicBlock positive, LlvmBasicBlock negative)
            {
                var flag = Add(new LlvmLoadInstruction(LlvmIntegerType.Get(owner.context, 1), cell)
                    { LogicalType = owner.metadata.RuntimeType(TypeRef.Bool) });
                Add(new LlvmCondBrInstruction(flag, positive, negative));
            }
        }

        private LlvmConstantInt PatternBoolean(bool result) =>
            LlvmConstantInt.Get(LlvmIntegerType.Get(owner.context, 1), result ? 1UL : 0UL);
    }
}
