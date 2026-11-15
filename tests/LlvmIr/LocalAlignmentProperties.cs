using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class LocalAlignmentProperties
{
    internal static int Run()
    {
        int checks = 0;
        var context = new LlvmContext();
        var i8 = LlvmType.GetInt8Ty(context);
        var i32 = LlvmType.GetInt32Ty(context);
        var i64 = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        var layout = new LlvmDataLayout("S128-Fn64");
        var allocation = new LlvmAllocaInstruction(i32, LlvmConstantInt.Get(i64, 1), alignment: new(4));
        Require(LlvmLocal.TryEnforceAlignment(allocation, new(8), layout).Value == 8 && allocation.Alignment.Value == 8,
            "The source helper mutates an alloca within natural stack alignment.");
        Require(LlvmLocal.TryEnforceAlignment(allocation, new(32), layout).Value == 8 && allocation.Alignment.Value == 8,
            "Natural stack alignment avoids introducing dynamic realignment.");
        Require(LlvmLocal.GetOrEnforceKnownAlignment(allocation, new(16), layout).Value == 16 && allocation.Alignment.Value == 16,
            "Known alignment and enforced storage alignment use the same explicit DataLayout.");
        var module = new LlvmModule("alignment", context) { DataLayout = layout, TargetTriple = new("x86_64-unknown-linux-gnu") };
        var global = new LlvmGlobalVariable(module, i32, false, LlvmLinkage.InternalLinkage, LlvmConstantInt.Get(i32, 0));
        Require(global.CanIncreaseAlignment() && LlvmLocal.TryEnforceAlignment(global, new(32), layout).Value == 32,
            "A local strong ELF definition permits an actual alignment increase.");
        Require(LlvmValueTracking.GetPointerAlignment(global, layout).Value == 32,
            "Explicit global alignment is authoritative, including when larger than preferred type alignment.");
        global.Section = ".packed";
        Require(!global.CanIncreaseAlignment() && LlvmLocal.TryEnforceAlignment(global, new(64), layout).Value == 32,
            "Explicit section packing plus explicit alignment retains its existing layout.");
        var exported = new LlvmGlobalVariable(module, i32, false, LlvmLinkage.ExternalLinkage, LlvmConstantInt.Get(i32, 0));
        Require(!exported.CanIncreaseAlignment(), "The source ELF COPY-relocation case does not acquire an unsupported alignment promise.");
        module.TargetTriple = new("x86_64-pc-windows-msvc");
        Require(exported.CanIncreaseAlignment(), "The source exported-variable limitation is ELF-specific.");
        module.TargetTriple = new("powerpc64-ibm-aix");
        exported.AddAttribute("toc-data");
        Require(!exported.CanIncreaseAlignment(), "XCOFF toc-data placement does not gain padding.");
        exported.RemoveAttribute("toc-data");
        Require(exported.CanIncreaseAlignment(), "Removing the source toc-data attribute restores ordinary XCOFF placement.");
        global.Section = "";
        global.ThreadLocalMode = LlvmThreadLocalMode.GeneralDynamicTLSModel;
        module.MaxTLSAlignment = 256;
        Require(LlvmLocal.TryEnforceAlignment(global, new(64), layout).Value == 32 && global.Alignment?.Value == 32,
            "The MaxTLSAlign flag is measured in bits and clamps the source requested alignment.");
        var function = LlvmFunction.Create(LlvmFunctionType.Get(pointer, [pointer]), LlvmLinkage.InternalLinkage, parent: module);
        function.Alignment = new(32);
        Require(LlvmValueTracking.GetPointerAlignment(function, layout).Value == 32
            && LlvmValueTracking.GetPointerAlignment(function, new("Fi64")).Value == 8,
            "Function-pointer alignment follows the independent versus multiple-of-function source mode.");
        function.SetParamAttributes(0, new() { Alignment = new(64) });
        Require(LlvmLocal.GetOrEnforceKnownAlignment(function.Arguments[0], null, layout).Value == 64,
            "KnownBits imports the actual pointer argument alignment without mutating the argument.");
        var narrow = new LlvmDataLayout("p:32:32");
        Require(LlvmKnownBitsAnalysis.Compute(function.Arguments[0], layout: narrow).BitWidth == 32,
            "An explicitly supplied layout overrides the enclosing module pointer width.");
        var zeroPointer = LlvmConstantPointerNull.Get(pointer);
        Require(LlvmLocal.GetOrEnforceKnownAlignment(zeroPointer, null, narrow).Value == 1UL << 31
            && LlvmLocal.GetOrEnforceKnownAlignment(zeroPointer, null, layout).Value == LlvmValue.MaximumAlignment,
            "Null-pointer trailing zeros are clamped by source value maximum and pointer bit width.");
        var tli = new LlvmTargetLibraryInfo(module);
        function.CallingConvention = 66;
        Require(tli.IsCallingConvCCompatible(function), "ARM_APCS has source numeric convention 66 and supports pointer signatures.");
        module.TargetTriple = new("arm64-apple-ios17");
        Require(!tli.IsCallingConvCCompatible(function), "The source iOS ABI exception applies before ARM signature checks.");
        module.TargetTriple = new("armv7-unknown-linux-gnueabi");
        function.CallingConvention = 0;
        Require(tli.IsCallingConvCCompatible(function), "C calling convention remains compatible independently of disabled library availability.");
        foreach ((string triple, LlvmObjectFormat format) in new[]
        {
            ("x86_64-unknown-linux-gnu", LlvmObjectFormat.ELF), ("x86_64-apple-macosx14", LlvmObjectFormat.MachO),
            ("powerpc64-ibm-aix", LlvmObjectFormat.XCOFF), ("s390x-ibm-zos", LlvmObjectFormat.GOFF),
            ("wasm32-unknown-wasi", LlvmObjectFormat.Wasm), ("spirv64-unknown-vulkan", LlvmObjectFormat.SPIRV),
            ("dxil-unknown-shadermodel", LlvmObjectFormat.DXContainer), ("x86_64-unknown-linux-gnuxcoff", LlvmObjectFormat.XCOFF),
            ("aarch64_be-apple-macos", LlvmObjectFormat.ELF), ("mipsel-unknown-windows", LlvmObjectFormat.COFF),
            ("mips64el-unknown-windows", LlvmObjectFormat.ELF), ("armv7eb-apple-macos", LlvmObjectFormat.ELF)
        }) Require(new LlvmTriple(triple).GetObjectFormat() == format, "Object format follows the pinned target parser, not the executing host.");
        Require(new LlvmDataLayout("S128-Fn64").Equals(new LlvmDataLayout("Fn64-S128"))
            && !layout.Equals(new LlvmDataLayout("S256-Fn64")), "Stack and function-pointer alignment specifications participate in layout identity.");
        return checks;

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }
}
