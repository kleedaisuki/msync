// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Type.cpp getFltSemantics.
namespace Msync.Core.Llvm;

public static class LlvmFloatingTypeExtensions
{
    public static LlvmAPFloatSemantics GetFltSemantics(this LlvmType type) => type.Id switch
    {
        LlvmTypeId.HalfTyID => LlvmAPFloatSemantics.IEEEhalf,
        LlvmTypeId.BFloatTyID => LlvmAPFloatSemantics.BFloat,
        LlvmTypeId.FloatTyID => LlvmAPFloatSemantics.IEEEsingle,
        LlvmTypeId.DoubleTyID => LlvmAPFloatSemantics.IEEEdouble,
        _ => throw new NotSupportedException($"Floating semantics for {type.Id} need a multiword APFloat carrier.")
    };
}
