// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: AsmWriter.cpp
// writeAPFloatInternal exact bit-string fallback and ConstantFP splat syntax.
using System.Globalization;

namespace Msync.Core.Llvm;

public static partial class LlvmIrWriter
{
    private sealed partial class Writer
    {
        private string Floating(LlvmConstantFP floating)
        {
            var value = floating.Value;
            if (floating.Type.IsVectorTy() && value.BitcastToAPInt().IsZero)
                return "zeroinitializer";
            uint width = LlvmAPFloat.SemanticsSizeInBits(value.Semantics);
            string bits = "f0x" + value.BitcastToAPInt().GetZExtValue()
                .ToString("X" + (width / 4).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            return floating.Type.IsVectorTy()
                ? "splat (" + Type(floating.Type.GetScalarType()) + " " + bits + ")" : bits;
        }
    }
}
