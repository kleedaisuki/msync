// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Value.cpp
// dropDroppableUses, dropDroppableUsesIn, dropDroppableUse.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public abstract partial class LlvmValue
{
    public bool HasNUndroppableUses(uint count)
    {
        uint seen = 0;
        foreach (LlvmUse use in Uses)
            if (!use.User.IsDroppable() && ++seen > count) return false;
        return seen == count;
    }
    public bool HasNUndroppableUsesOrMore(uint count)
    {
        if (count == 0) return true;
        uint seen = 0;
        foreach (LlvmUse use in Uses)
            if (!use.User.IsDroppable() && ++seen == count) return true;
        return false;
    }
    public void DropDroppableUses(Func<LlvmUse, bool>? shouldDrop = null)
    {
        List<LlvmUse> edit = [];
        foreach (LlvmUse use in Uses)
            if (use.User.IsDroppable() && (shouldDrop is null || shouldDrop(use))) edit.Add(use);
        foreach (LlvmUse use in edit) DropDroppableUse(use);
    }

    public void DropDroppableUsesIn(LlvmUser user)
    {
        Debug.Assert(user.IsDroppable());
        foreach (LlvmUse use in user.OperandUses)
            if (ReferenceEquals(use.Value, this)) DropDroppableUse(use);
    }

    public static void DropDroppableUse(LlvmUse use)
    {
        if (use.User is not LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.IsAssume: true } assume)
            throw new InvalidOperationException("Unknown droppable use.");
        if (use.OperandNo == 0) use.Set(LlvmConstantInt.GetTrue(assume.Context));
        else
        {
            use.Set(LlvmPoisonValue.Get(use.Value!.Type));
            assume.SetBundleTagForOperand(use.OperandNo, "ignore");
        }
    }
}
