using Msync.Core;
using Msync.Core.Llvm;

internal static class ExtensionUses
{
    internal static void Run()
    {
        Program.Require(Enum.GetValues<LlvmOpcode>().Where(opcode => (byte)opcode >= 128)
            .SequenceEqual([LlvmOpcode.HeapAlloc, LlvmOpcode.FileIn, LlvmOpcode.FileOut]),
            "Project operation extensions must be exactly HeapAlloc/FileIn/FileOut.");
        Program.Require((byte)LlvmOpcode.HeapAlloc == 128 && (byte)LlvmOpcode.FileIn == 129
            && (byte)LlvmOpcode.FileOut == 130, "Project extension opcode identities changed.");
        LlvmContext context = new();
        LlvmType pointer = LlvmPointerType.Get(context);
        LlvmIntegerType sizeType = LlvmType.GetInt64Ty(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context),
            [pointer, pointer, sizeType]), LlvmLinkage.InternalLinkage, "extensions");
        LlvmArgument source = function.Arguments[0], destination = function.Arguments[1], size = function.Arguments[2];
        SourceSpan origin = new("llvm-extensions.msync", 3, 9);
        LlvmHeapAllocInst heap = new(size, new(16), addressSpace: 3) { Source = origin };
        LlvmHeapAllocInst heapCopy = (LlvmHeapAllocInst)heap.Clone();
        Program.Require(heapCopy.Parent is null && heapCopy.AddressSpace == 3 && heapCopy.Alignment.Value == 16
            && ReferenceEquals(heapCopy.ByteSize, size) && Equals(heapCopy.Source, origin),
            "HeapAlloc cloning lost its address-space/alignment/size/source attributes.");
        Program.Require(!ReferenceEquals(heap.GetOperandUse(0), heapCopy.GetOperandUse(0))
            && size.GetNumUses() == 2, "HeapAlloc clones must own independent operand uses.");
        heap.ByteSize = LlvmConstantInt.Get(sizeType, 32);
        Program.Require(ReferenceEquals(heapCopy.ByteSize, size) && size.HasOneUse,
            "Updating a heap size operand changed its clone or left a stale use.");
        size.ReplaceAllUsesWith(LlvmConstantInt.Get(sizeType, 64));
        Program.Require(size.UseEmpty && heapCopy.ByteSize is LlvmConstantInt integer && integer.EqualsInt(64),
            "HeapAlloc's size is not participating in ordinary replace-all-uses.");
        heap.DropAllReferences();
        heapCopy.DropAllReferences();

        foreach (LlvmFileInFormat format in Enum.GetValues<LlvmFileInFormat>())
            ReadAndWrite(context, source, destination, pointer, format, origin);
        Program.Require(source.UseEmpty && destination.UseEmpty,
            "Disposed test instructions retained file operand uses.");
    }

    private static void ReadAndWrite(LlvmContext context, LlvmArgument source, LlvmArgument destination,
        LlvmType resultType, LlvmFileInFormat format, SourceSpan origin)
    {
        LlvmFileInInst read = new(resultType, source, format) { Source = origin };
        LlvmFileInInst readCopy = (LlvmFileInInst)read.Clone();
        Program.Require(readCopy.Parent is null && readCopy.Format == format
            && ReferenceEquals(readCopy.GetOperand(0), source) && Equals(readCopy.Source, origin),
            "FileIn cloning lost operands, format or source identity.");
        Program.Require(!ReferenceEquals(read.GetOperandUse(0), readCopy.GetOperandUse(0)) && source.GetNumUses() == 2,
            "FileIn clones must own independent source uses.");
        List<LlvmInstruction> consumers = [];
        foreach (LlvmFileOutMode mode in Enum.GetValues<LlvmFileOutMode>())
        {
            LlvmFileOutInst write = new(LlvmType.GetVoidTy(context), read, destination, mode) { Source = origin };
            LlvmFileOutInst writeCopy = (LlvmFileOutInst)write.Clone();
            Program.Require(writeCopy.Parent is null && writeCopy.Mode == mode && Equals(writeCopy.Source, origin)
                && ReferenceEquals(writeCopy.Value, read) && ReferenceEquals(writeCopy.Destination, destination),
                "FileOut cloning lost value/destination/mode/source identity.");
            Program.Require(!ReferenceEquals(write.GetOperandUse(0), writeCopy.GetOperandUse(0))
                && !ReferenceEquals(write.GetOperandUse(1), writeCopy.GetOperandUse(1)),
                "FileOut clones must not share operand-use nodes.");
            consumers.Add(write);
            consumers.Add(writeCopy);
        }
        Program.Require(read.GetNumUses() == consumers.Count, "FileOut value uses are missing from the FileIn result.");
        read.ReplaceAllUsesWith(readCopy);
        Program.Require(read.UseEmpty && readCopy.GetNumUses() == consumers.Count
            && consumers.All(user => ReferenceEquals(user.GetOperand(0), readCopy)),
            "FileOut values did not follow ordinary SSA use replacement.");
        source.ReplaceAllUsesWith(destination);
        Program.Require(source.UseEmpty && ReferenceEquals(read.GetOperand(0), destination)
            && ReferenceEquals(readCopy.GetOperand(0), destination), "FileIn path operands did not follow SSA use replacement.");
        foreach (LlvmInstruction consumer in consumers) consumer.DropAllReferences();
        read.DropAllReferences();
        readCopy.DropAllReferences();
        Program.Require(readCopy.UseEmpty && destination.UseEmpty,
            "Dropping file instruction operands left stale use-list links.");
    }
}
