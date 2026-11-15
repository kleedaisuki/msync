// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Argument.h; Function.cpp Argument constructor.
namespace Msync.Core.Llvm;

public sealed class LlvmArgument : LlvmValue
{
    public LlvmFunction? Parent { get; internal set; }
    public uint ArgNo { get; }
    public LlvmArgument(LlvmType type, string name = "", LlvmFunction? parent = null, uint argNo = 0)
        : base(type, (byte)LlvmValueKind.ArgumentVal)
    {
        Parent = parent;
        ArgNo = argNo;
        SetName(name);
    }
}
