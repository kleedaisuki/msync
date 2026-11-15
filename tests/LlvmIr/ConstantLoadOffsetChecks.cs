using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class ConstantLoadOffsetChecks
{
    public static void Run()
    {
        LlvmContext context = new();
        LlvmIntegerType i16 = LlvmType.GetInt16Ty(context), i32 = LlvmType.GetInt32Ty(context);
        LlvmConstant value = LlvmConstantInt.Get(i32, 0x44332211);
        LlvmDataLayout little = new("e"), big = new("E");
        RequireBits(Fold(value, i16, 0, little), 0x2211, "little-endian leading halfword");
        RequireBits(Fold(value, i16, 0, big), 0x4433, "big-endian leading halfword");
        RequireBits(Fold(value, i16, 1, little), 0x3322, "little-endian interior offset");
        RequireBits(Fold(value, i16, 1, big), 0x3322, "big-endian interior offset");
        RequireBits(Fold(value, i16, -1, little), 0x1100, "little-endian partial negative offset");
        RequireBits(Fold(value, i16, -1, big), 0x44, "big-endian partial negative offset");
        Require(Fold(value, i16, -2, little) is LlvmPoisonValue, "wholly negative load");
        Require(Fold(value, i16, 4, little) is LlvmPoisonValue, "wholly out-of-bounds load");
        LlvmConstant zero = LlvmConstantInt.Get(i32, 0);
        Require(Fold(zero, i16, 4, little) is LlvmPoisonValue, "bounds precede uniform zero fold");
        LlvmConstant second = LlvmConstantInt.Get(i32, 0x88776655);
        LlvmConstant array = LlvmConstantArray.Get(LlvmArrayType.Get(i32, 2), [value, second]);
        Require(ReferenceEquals(Fold(array, i32, 4, little), second), "exact array element offset");
        RequireBits(Fold(array, i32, 2, little), 0x66554433, "little-endian cross-element reinterpret load");
        RequireBits(Fold(array, i32, 2, big), 0x22118877, "big-endian cross-element reinterpret load");

        LlvmModule module = new("load-offset-facts", context);
        LlvmGlobalVariable global = new(module, i32, true, LlvmLinkage.ExternalLinkage, value, "constant");
        Require(global.HasDefinitiveInitializer, "ordinary external initializer");
        RequireBits(LlvmConstantFolding.ConstantFoldLoadFromConstPtr(global, i16, new LlvmAPInt(64, 1), little), 0x3322,
            "constant global offset overload");
        module.SemanticInterposition = true;
        Require(!global.HasDefinitiveInitializer, "semantic interposition changes definitive initializer");
        Require(LlvmConstantFolding.ConstantFoldLoadFromConstPtr(global, i16, little) is null, "interposable global does not fold");
        global.IsDSOLocal = true;
        Require(global.HasDefinitiveInitializer, "DSO-local definition excludes semantic interposition");
        global.Linkage = LlvmLinkage.WeakAnyLinkage;
        Require(!global.HasDefinitiveInitializer, "weak-any initializer is not definitive");
        global.Linkage = LlvmLinkage.WeakODRLinkage;
        Require(global.HasDefinitiveInitializer, "weak-ODR initializer remains definitive");
        global.IsExternallyInitialized = true;
        Require(!global.HasDefinitiveInitializer, "externally initialized definition");
    }

    private static LlvmConstant? Fold(LlvmConstant value, LlvmType type, long offset, LlvmDataLayout layout) =>
        LlvmConstantFolding.ConstantFoldLoadFromConst(value, type, new LlvmAPInt(64, unchecked((ulong)offset), isSigned: true), layout);
    private static void RequireBits(LlvmConstant? value, ulong expected, string cause) =>
        Require(value is LlvmConstantInt integer && integer.Value.GetZExtValue() == expected, cause);
    private static void Require(bool condition, string cause)
    {
        if (!condition) throw new InvalidOperationException($"Constant load offset: {cause}");
    }
}
