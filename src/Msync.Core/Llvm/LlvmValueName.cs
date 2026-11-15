// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Value.h ValueName (StringMapEntry<Value *>).
namespace Msync.Core.Llvm;

public sealed class LlvmValueName
{
    public ReadOnlyMemory<byte> Key { get; }
    public LlvmValue Value { get; internal set; }

    internal LlvmValueName(ReadOnlySpan<byte> key, LlvmValue value)
    {
        Key = key.ToArray();
        Value = value;
    }
}
