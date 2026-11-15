// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IRBuilder.h CreateExtractVector/CreateInsertVector.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    public LlvmValue CreateExtractVector(LlvmType destination, LlvmValue vector, LlvmValue index,
        string name = "") => CreateIntrinsic(LlvmIntrinsicKind.VectorExtract, [vector, index], destination, name);
    public LlvmValue CreateExtractVector(LlvmType destination, LlvmValue vector, ulong index,
        string name = "") => CreateExtractVector(destination, vector,
            LlvmConstantInt.Get(LlvmType.GetInt64Ty(vector.Context), index), name);
    public LlvmValue CreateInsertVector(LlvmType destination, LlvmValue vector, LlvmValue subvector,
        LlvmValue index, string name = "") =>
        CreateIntrinsic(LlvmIntrinsicKind.VectorInsert, [vector, subvector, index], destination, name);
    public LlvmValue CreateInsertVector(LlvmType destination, LlvmValue vector, LlvmValue subvector,
        ulong index, string name = "") => CreateInsertVector(destination, vector, subvector,
            LlvmConstantInt.Get(LlvmType.GetInt64Ty(vector.Context), index), name);
}
