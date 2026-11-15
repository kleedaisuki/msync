// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/GenericDomTree.h: DomTreeNodeBase::removeChild/setIDom/UpdateLevel.
using System.Diagnostics;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTreeNode
{
    public bool IsLeaf => FirstChild is null;

    internal uint RemoveChild(LlvmDominatorTreeNode child)
    {
        uint steps = 1;
        if (ReferenceEquals(FirstChild, child)) FirstChild = child.Sibling;
        else
        {
            var previous = FirstChild;
            while (!ReferenceEquals(previous!.Sibling, child))
            {
                Debug.Assert(previous.Sibling is not null, "Not in immediate dominator children list");
                previous = previous.Sibling;
                ++steps;
            }
            previous.Sibling = child.Sibling;
        }
        child.Sibling = null;
        return steps;
    }

    internal (uint UnlinkSteps, uint LevelUpdates) SetIDom(LlvmDominatorTreeNode newIDom)
    {
        Debug.Assert(IDom is not null, "No immediate dominator");
        if (ReferenceEquals(IDom, newIDom)) return (0, 0);
        uint unlinkSteps = IDom!.RemoveChild(this);
        IDom = newIDom;
        newIDom.AddChild(this);
        return (unlinkSteps, UpdateLevel());
    }

    internal uint UpdateLevel()
    {
        Debug.Assert(IDom is not null);
        if (Level == IDom!.Level + 1) return 0;
        List<LlvmDominatorTreeNode> work = [this];
        uint updates = 0;
        while (work.Count != 0)
        {
            var current = work[^1];
            work.RemoveAt(work.Count - 1);
            current.Level = current.IDom!.Level + 1;
            ++updates;
            foreach (var child in current.Children)
            {
                Debug.Assert(child.IDom is not null);
                if (child.Level != child.IDom!.Level + 1) work.Add(child);
            }
        }
        return updates;
    }
}
