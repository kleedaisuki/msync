// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Constants.cpp
// constantIsDead(RemoveDeadUsers=false), hasNLiveUses and hasOneLiveUse.
namespace Msync.Core.Llvm;

public abstract partial class LlvmConstant
{
    public bool HasOneLiveUse => HasNLiveUses(1);
    public bool HasZeroLiveUses => HasNLiveUses(0);

    public bool HasNLiveUses(uint count)
    {
        uint uses = 0;
        foreach (LlvmUse use in Uses)
        {
            if (use.User is LlvmConstant constant && ConstantIsDead(constant)) continue;
            if (++uses > count) return false;
        }
        return uses == count;
    }

    private static bool ConstantIsDead(LlvmConstant constant)
    {
        if (constant is LlvmGlobalValue) return false;
        foreach (LlvmUser user in constant.Users)
            if (user is not LlvmConstant next || !ConstantIsDead(next)) return false;
        return true;
    }
}
