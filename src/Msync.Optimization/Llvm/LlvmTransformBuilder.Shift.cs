// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IRBuilder.h CreateShl/CreateLShr.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    public LlvmValue CreateShl(LlvmValue left, LlvmValue right, string name = "", bool nuw = false, bool nsw = false) =>
        CreateBinary(LlvmOpcode.Shl, left, right, name, nuw: nuw, nsw: nsw);

    public LlvmValue CreateLShr(LlvmValue left, LlvmValue right, string name = "", bool isExact = false) =>
        CreateBinary(LlvmOpcode.LShr, left, right, name, exact: isExact);
}
