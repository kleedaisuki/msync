// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/DerivedTypes.h Type::getWithNewType and getWithNewBitWidth.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public abstract partial class LlvmType
{
    public LlvmType GetWithNewType(LlvmType element) =>
        this is LlvmVectorType vector ? LlvmVectorType.Get(element, vector.ElementCount) : element;

    public LlvmType GetWithNewBitWidth(uint width)
    {
        Debug.Assert(IsIntOrIntVectorTy());
        return GetWithNewType(GetIntNTy(Context, width));
    }
}
