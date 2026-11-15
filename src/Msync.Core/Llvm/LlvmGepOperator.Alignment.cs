// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/Operator.cpp GEPOperator::getMaxPreservedAlignment.
namespace Msync.Core.Llvm;

public readonly partial struct LlvmGepOperator
{
    public LlvmAlign GetMaxPreservedAlignment(LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        var result = new LlvmAlign(LlvmValue.MaximumAlignment);
        LlvmType current = SourceElementType;
        for (int index = 0; index < IndexCount; ++index)
        {
            if (metrics is not null)
            {
                const string key = "gep.preserved_alignment.indices";
                metrics[key] = metrics.GetValueOrDefault(key) + 1;
            }
            var constant = GetIndex(index) as LlvmConstantInt;
            var structure = index == 0 ? null : current as LlvmStructType;
            bool vectorElement = index != 0 && current is LlvmVectorType;
            LlvmType element = index == 0 ? current : LlvmGetElementPtrInstruction.GetTypeAtIndex(current, GetIndex(index))!;
            ulong offset;
            if (structure is not null)
                offset = layout.GetStructLayout(structure).GetElementOffset((uint)constant!.Value.GetWord(0)).GetFixedValue();
            else
            {
                ulong count = constant?.Value.GetLimitedValue() ?? 1;
                ulong stride = (vectorElement ? layout.GetTypeStoreSize(element) : layout.GetTypeAllocSize(element)).GetFixedValue();
                offset = unchecked(stride * count);
            }
            result = LlvmAlign.CommonAlignment(result, offset);
            current = element;
        }
        return result;
    }
}
