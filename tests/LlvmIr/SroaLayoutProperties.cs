using Msync.Core.Llvm;

internal static class SroaLayoutProperties
{
    internal static int Run()
    {
        int checks = 0;
        var context = new LlvmContext();
        var layout = new LlvmDataLayout();
        var i8 = LlvmType.GetInt8Ty(context);
        var i32 = LlvmType.GetInt32Ty(context);
        var i64 = LlvmType.GetInt64Ty(context);
        var structure = LlvmStructType.Get(context, [i8, i32, LlvmArrayType.Get(i8, 3)]);
        Require(layout.GetIndexedOffsetInType(structure, [LlvmConstantInt.Get(i64, 0), LlvmConstantInt.Get(i32, 1)]) == 4,
            "Struct GEP offsets come from DataLayout's padded field layout.");
        Require(layout.GetIndexedOffsetInType(structure,
            [LlvmConstantInt.Get(i64, 0), LlvmConstantInt.Get(i32, 2), LlvmConstantInt.Get(i64, 2)]) == 10,
            "Nested array GEP offsets include the actual struct field offset.");
        Require(layout.GetIndexedOffsetInType(i32, [LlvmConstantInt.GetSigned(i64, -3)]) == -12,
            "Sequential indices retain signed negative offsets.");
        Require(!layout.TypeSizeEqualsStoreSize(LlvmType.GetInt1Ty(context)) && layout.TypeSizeEqualsStoreSize(i32),
            "Integer bit padding is distinguished from its memory footprint.");
        var vector = LlvmFixedVectorType.Get(i8, 8);
        Require(layout.GetIndexedOffsetInType(vector, [LlvmConstantInt.Get(i64, 1), LlvmConstantInt.Get(i64, 3)]) == 11,
            "Vector GEP stepping uses the aggregate stride and byte-sized packed element stride.");
        Require(ReferenceEquals(layout.GetIntPtrType(LlvmFixedVectorType.Get(LlvmPointerType.Get(context), 2)),
            LlvmFixedVectorType.Get(i64, 2)), "Pointer-vector integer types preserve the vector element count.");
        var resized = LlvmVectorType.GetWithSizeAndScalar(LlvmFixedVectorType.Get(i32, 4), i64);
        Require(resized is LlvmFixedVectorType { NumElements: 2 } && ReferenceEquals(resized.ElementType, i64),
            "Vector shape conversion preserves total primitive size.");
        Require(LlvmVectorType.GetWithSizeAndScalar(LlvmFixedVectorType.Get(i8, 3), i32) is null,
            "A nondivisible vector size does not produce an invented rounded shape.");
        foreach ((ulong alignment, ulong offset, ulong expected) in new[]
            { (16UL, 0UL, 16UL), (16UL, 24UL, 8UL), (8UL, 3UL, 1UL), (8UL, 12UL, 4UL) })
            Require(LlvmAlign.CommonAlignment(new(alignment), offset).Value == expected, "Offset alignment retains the source minimum power-of-two factor.");
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i64, [i64]), LlvmLinkage.InternalLinkage);
        var scalar = new LlvmAllocaInstruction(i32, LlvmConstantInt.Get(i64, 1));
        Require(!scalar.IsArrayAllocation && scalar.GetAllocationSize(layout) == LlvmTypeSize.Fixed(4),
            "A single element uses its allocation base size without requiring an array multiply.");
        var array = new LlvmAllocaInstruction(i32, LlvmConstantInt.Get(i64, 3));
        Require(array.IsArrayAllocation && array.GetAllocationSize(layout) == LlvmTypeSize.Fixed(12)
            && array.GetAllocationSizeInBits(layout) == LlvmTypeSize.Fixed(96), "Constant arrays expose exact byte and bit allocation sizes.");
        Require(new LlvmAllocaInstruction(i32, function.Arguments[0]).GetAllocationSize(layout) is null,
            "Dynamic array sizes remain unknown to the constant allocation-size query.");
        Require(new LlvmAllocaInstruction(LlvmStructType.Get(context), function.Arguments[0]).GetAllocationSize(layout) == LlvmTypeSize.Fixed(0),
            "Zero-sized allocated types return zero before querying an unknown array count.");
        Require(new LlvmAllocaInstruction(i64, LlvmConstantInt.Get(i64, ulong.MaxValue)).GetAllocationSize(layout) is null,
            "Allocation size multiplication preserves the source unsigned-overflow result.");
        Require(new LlvmAllocaInstruction(i8, LlvmConstantInt.Get(context, LlvmAPInt.GetAllOnes(128))).GetAllocationSize(layout) is null,
            "A count wider than uint64 is not narrowed before multiplying.");
        var nonIntegral = new LlvmDataLayout("ni:1:4");
        Require(nonIntegral.IsNonIntegralAddressSpace(1) && nonIntegral.IsNonIntegralPointerType(LlvmPointerType.Get(context, 4))
            && !nonIntegral.IsNonIntegralAddressSpace(0) && !nonIntegral.IsNonIntegralAddressSpace(2),
            "Explicit non-integral address spaces do not change other address-space defaults.");
        Require(nonIntegral.HasUnstableRepresentation(1) && nonIntegral.Equals(new LlvmDataLayout("ni:4:1:4"))
            && !nonIntegral.Equals(layout), "Legacy ni specifications mark unstable pointer representation and participate in layout equality.");
        var external = new LlvmDataLayout("e-p:64:64:64:64-pe1:64:64:64:64-A1-G1");
        var managedPointer = LlvmPointerType.Get(context, 1);
        Require(external.HasExternalState(managedPointer) && !external.HasUnstableRepresentation(managedPointer)
            && external.IsNonIntegralPointerType(managedPointer) && external.MustNotIntroduceIntToPtr(managedPointer)
            && !external.MustNotIntroducePtrToInt(managedPointer), "External-state pointers retain the source distinction from unstable pointers.");
        Require(external.GetAllocaAddressSpace() == 1 && external.GetDefaultGlobalsAddressSpace() == 1
            && external.GetProgramAddressSpace() == 0 && !external.HasExternalState(0), "Managed-data layout does not change the code-pointer address space.");
        var fat = new LlvmDataLayout("p:32:32-po3(cap):128:128:256:64");
        Require(fat.GetPointerSizeInBits(99) == 32 && fat.GetPointerABIAlignment(99).Value == 4
            && fat.GetPointerSizeInBits(3) == 128 && fat.GetIndexSizeInBits(3) == 64 && fat.GetPointerPrefAlignment(3).Value == 32
            && fat.IsNonIntegralAddressSpace(3) && !fat.MustNotIntroduceIntToPtr(3) && fat.GetNullPtrValue(3).IsAllOnes
            && fat.GetAddressSpaceName(3) == "cap", "Pointer specs preserve independent size, index width, alignment, symbolic name and null representation.");
        Require(new LlvmDataLayout("ni:1-pe1:64:64").Equals(new LlvmDataLayout("pe1:64:64-ni:1"))
            && new LlvmDataLayout("ni:1-pe1:64:64").HasUnstableRepresentation(1)
            && !new LlvmDataLayout("ni:1-pe1:64:64").HasExternalState(1), "Legacy ni is applied after pointer specs, independently of textual ordering.");
        var namedDefault = new LlvmDataLayout("p(default):32:32-ni:7");
        Require(namedDefault.GetAddressSpaceName(99) == "default" && namedDefault.GetAddressSpaceName(7) == "default"
            && namedDefault.GetPointerSizeInBits(7) == 32 && namedDefault.HasUnstableRepresentation(7),
            "The source pointer-spec fallback includes its symbolic name, including a deferred legacy-ni override.");
        var reordered = new LlvmDataLayout("p3:128:128:256:64-p:32:32");
        var canonical = new LlvmDataLayout("p:32:32-p3:128:128:256:64");
        Require(reordered.Equals(canonical) && reordered.GetHashCode() == canonical.GetHashCode(),
            "Pointer-spec insertion order does not change layout equality or hashing.");
        return checks;

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }
}
