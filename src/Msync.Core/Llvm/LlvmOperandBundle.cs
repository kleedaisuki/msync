// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/InstrTypes.h
// OperandBundleDefT/OperandBundleUse/CallBase::BundleOpInfo.
using System.Collections;
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed class LlvmOperandBundleDef
{
    public LlvmOperandBundleDef(string tag, IReadOnlyList<LlvmValue> inputs)
    {
        Tag = tag;
        Inputs = Array.AsReadOnly(inputs.ToArray());
    }
    public LlvmOperandBundleDef(LlvmOperandBundleUse bundle) : this(bundle.Tag, bundle.Inputs) { }
    public string Tag { get; }
    public IReadOnlyList<LlvmValue> Inputs { get; }
}

public readonly struct LlvmOperandBundleUse
{
    private readonly LlvmCallBase owner;
    internal LlvmOperandBundleUse(LlvmCallBase owner, string tag, int begin, int end)
    {
        this.owner = owner;
        Tag = tag;
        Begin = begin;
        End = end;
        Inputs = new LlvmOperandBundleInputs(owner, begin, end - begin);
    }
    public string Tag { get; }
    internal int Begin { get; }
    internal int End { get; }
    public int InputCount => End - Begin;
    public LlvmOperandBundleInputs Inputs { get; }
    public LlvmValue GetInput(int index) => GetInputUse(index).Value!;
    public void SetInput(int index, LlvmValue value) => GetInputUse(index).Set(value);
    public LlvmUse GetInputUse(int index)
    {
        Debug.Assert((uint)index < (uint)InputCount);
        return owner.GetOperandUse(Begin + index);
    }
}

public readonly struct LlvmOperandBundleInputs : IReadOnlyList<LlvmValue>
{
    private readonly LlvmCallBase owner;
    private readonly int begin;
    internal LlvmOperandBundleInputs(LlvmCallBase owner, int begin, int count)
        => (this.owner, this.begin, Count) = (owner, begin, count);
    public int Count { get; }
    public LlvmValue this[int index]
    {
        get { Debug.Assert((uint)index < (uint)Count); return owner.GetOperand(begin + index)!; }
    }
    public IEnumerator<LlvmValue> GetEnumerator()
    {
        for (int index = 0; index < Count; ++index) yield return this[index];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
