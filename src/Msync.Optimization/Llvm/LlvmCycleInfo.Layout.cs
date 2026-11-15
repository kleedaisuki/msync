// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// ADT/GenericCycleImpl.h GenericCycleInfoCompute::run/flatten.
using System.Collections.ObjectModel;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmCycleInfo
{
    private sealed partial class Discovery
    {
        private readonly List<LlvmBasicBlock> layout = [];
        private void BuildForest()
        {
            var build = new List<CycleBuild>(headerCount);
            int topHead = None;
            foreach (int number in preorder)
            {
                ref BlockInfo block = ref blocks[number];
                if (block.IsHeader)
                {
                    int index = build.Count;
                    block.LoopIndex = index;
                    int parent = block.LoopHeader == None ? None : blocks[block.LoopHeader].LoopIndex;
                    int head = parent == None ? topHead : build[parent].ChildHead;
                    build.Add(new CycleBuild { ChildHead = None, NextSibling = head, OwnCount = 1 });
                    if (parent == None) topHead = index;
                    else { CycleBuild node = build[parent]; node.ChildHead = index; build[parent] = node; }
                    Count("cycles.discovered");
                }
                else if (block.LoopHeader != None)
                {
                    block.LoopIndex = blocks[block.LoopHeader].LoopIndex;
                    CycleBuild cycle = build[block.LoopIndex]; ++cycle.OwnCount; build[block.LoopIndex] = cycle;
                }
                else block.LoopIndex = None;
            }
            Flatten(build, topHead);
        }
        private void Flatten(List<CycleBuild> build, int topHead)
        {
            result.data = new CycleData[build.Count];
            int[] flatIndex = new int[build.Count];
            var stack = new List<(int Flat, int Child)>();
            int cursor = 0, nextID = 0;
            void Enter(int cycle, int parent)
            {
                int id = nextID++;
                flatIndex[cycle] = id;
                ref CycleData flat = ref result.data[id];
                flat.Parent = parent; flat.Depth = parent == None ? 1 : result.data[parent].Depth + 1;
                flat.EntryBegin = cursor; flat.EntrySize = 1;
                cursor += build[cycle].OwnCount; flat.BlockBegin = cursor;
                stack.Add((id, build[cycle].ChildHead));
                Count("cycles.depth_sum", flat.Depth);
                if (parent == None) Count("cycles.top_level");
            }
            for (int top = topHead; top != None; top = build[top].NextSibling)
            {
                Enter(top, None);
                while (stack.Count != 0)
                {
                    var frame = stack[^1];
                    if (frame.Child != None)
                    {
                        stack[^1] = (frame.Flat, build[frame.Child].NextSibling);
                        Enter(frame.Child, frame.Flat);
                    }
                    else
                    {
                        result.data[frame.Flat].BlockEnd = cursor;
                        result.data[frame.Flat].Descendants = nextID - frame.Flat - 1;
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
            }
            for (int index = 0; index < cursor; ++index) layout.Add(null!);
            for (int index = preorder.Count - 1; index >= 0; --index)
            {
                int number = preorder[index], loop = blocks[number].LoopIndex;
                if (loop == None) continue;
                int flat = flatIndex[loop];
                result.blockMap[number] = flat;
                layout[--result.data[flat].BlockBegin] = blocks[number].Block!;
                Count("layout.blocks_written");
            }
        }
        private void AddReentries()
        {
            int[] rank = new int[blocks.Length];
            for (int index = 0; index < preorder.Count; ++index) rank[preorder[index]] = index;
            for (int index = 0; index < reentries.Count; ++index) reentries[index] = (reentries[index].Header, rank[reentries[index].Block]);
            reentries.Sort();
            int previousHeader = None;
            for (int index = 0; index < reentries.Count; ++index)
            {
                if (index != 0 && reentries[index] == reentries[index - 1]) { Count("reentry.duplicates"); continue; }
                var reentry = reentries[index];
                ref CycleData cycle = ref result.data[result.blockMap[reentry.Header]];
                if (reentry.Header != previousHeader)
                {
                    LlvmBasicBlock header = layout[cycle.EntryBegin];
                    cycle.EntryBegin = layout.Count; layout.Add(header);
                    previousHeader = reentry.Header; Count("cycles.irreducible");
                }
                layout.Add(blocks[preorder[reentry.Block]].Block!);
                cycle.EntrySize = layout.Count - cycle.EntryBegin;
                Count("reentry.unique_entries");
            }
        }
        private void PublishViews()
        {
            result.cycles = new LlvmCycle[result.data.Length];
            LlvmBasicBlock[] storage = layout.ToArray();
            for (int index = 0; index < result.cycles.Length; ++index)
            {
                CycleData data = result.data[index];
                result.cycles[index] = new LlvmCycle(result, index)
                {
                    Blocks = new ReadOnlyCollection<LlvmBasicBlock>(new ArraySegment<LlvmBasicBlock>(storage, data.BlockBegin, data.BlockEnd - data.BlockBegin)),
                    Entries = new ReadOnlyCollection<LlvmBasicBlock>(new ArraySegment<LlvmBasicBlock>(storage, data.EntryBegin, data.EntrySize))
                };
                Count("layout.inclusive_memberships", (ulong)(data.BlockEnd - data.BlockBegin));
            }
            result.Cycles = Array.AsReadOnly(result.cycles);
        }
    }
}
