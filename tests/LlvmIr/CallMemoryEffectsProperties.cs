using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class CallMemoryEffectsProperties
{
    internal static int Run()
    {
        int checks = 0;
        LlvmContext context = new();
        LlvmModule module = new("call-memory-effects", context);
        var pointer = LlvmPointerType.Get(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [pointer, pointer]),
            LlvmLinkage.InternalLinkage, "caller", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        var callee = LlvmFunction.Create(function.FunctionType, LlvmLinkage.ExternalLinkage, "effect", module);
        var first = new LlvmCallInstruction(callee.FunctionType, callee, function.Arguments);
        var second = new LlvmCallInstruction(callee.FunctionType, callee, function.Arguments);
        block.Append(first); block.Append(second); block.Append(new LlvmReturnInstruction(context));
        var aa = new LlvmAAResults(function);
        var location = new LlvmMemoryLocation(function.Arguments[0], 8UL);

        first.MemoryEffectsAttribute = new(3);
        first.AddParamAttr(0, LlvmAttributeKind.ReadOnly);
        first.AddParamAttr(1, LlvmAttributeKind.ReadNone);
        Require(aa.GetModRefInfo(first, location) == LlvmModRefInfo.Ref,
            "Argument-region effects must intersect the matching parameter's readonly attribute.");
        first.RemoveParamAttr(0, LlvmAttributeKind.ReadOnly);
        first.AddParamAttr(0, LlvmAttributeKind.WriteOnly);
        Require(aa.GetModRefInfo(first, location) == LlvmModRefInfo.Mod,
            "Writeonly parameter effects must not invent a read.");
        first.AddParamAttr(0, LlvmAttributeKind.ReadNone);
        Require(aa.GetModRefInfo(first, location) == LlvmModRefInfo.NoModRef,
            "A readnone argument cannot clobber a location merely because the address was passed.");
        first.RemoveParamAttr(0, LlvmAttributeKind.ReadNone);
        second.MemoryEffectsAttribute = new(1);
        Require(aa.GetModRefInfo(first, second) == LlvmModRefInfo.Mod,
            "The dependence on a readonly call retains only the preceding call's writes.");
        first.MemoryEffectsAttribute = new(1);
        second.MemoryEffectsAttribute = new(2);
        first.RemoveParamAttr(0, LlvmAttributeKind.WriteOnly);
        Require(aa.GetModRefInfo(first, second) == LlvmModRefInfo.Ref,
            "The dependence of a reader on a writer is Ref, not symmetric ModRef.");

        first.SetParamAttributes(0, default); first.SetParamAttributes(1, default);
        foreach (int firstRegion in new[] { 1, 4, 5 })
        foreach (int secondRegion in new[] { 1, 4, 5 })
        foreach (uint firstEffect in new uint[] { 1, 2, 3 })
        foreach (uint secondEffect in new uint[] { 1, 2, 3 })
        {
            first.MemoryEffectsAttribute = new(firstEffect << (firstRegion * 2));
            second.MemoryEffectsAttribute = new(secondEffect << (secondRegion * 2));
            var expected = firstRegion != secondRegion || firstEffect == 1 && secondEffect == 1
                ? LlvmModRefInfo.NoModRef : (LlvmModRefInfo)firstEffect;
            Require(aa.GetModRefInfo(first, second) == expected,
                $"Region dependence differs for {firstRegion}:{firstEffect} -> {secondRegion}:{secondEffect}.");
        }
        return checks;

        void Require(bool result, string message)
        {
            Program.Require(result, message);
            ++checks;
        }
    }
}
