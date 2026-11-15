using Msync.Core.Llvm;

internal static class AggregateUses
{
    internal static void Run()
    {
        LlvmContext context = new();
        LlvmType pointer = LlvmPointerType.Get(context);
        LlvmIntegerType integer = LlvmType.GetInt64Ty(context);
        LlvmStructType structure = LlvmStructType.Get(context, [pointer, integer]);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context),
            [structure, pointer, integer, structure, pointer]), LlvmLinkage.InternalLinkage, "aggregate-operands");
        LlvmArgument aggregate = function.Arguments[0], replacement = function.Arguments[3];
        LlvmArgument value = function.Arguments[1], replacementValue = function.Arguments[4];
        uint[] indices = [0];
        LlvmInsertValueInstruction insert = new(aggregate, value, indices);
        LlvmInsertValueInstruction insertCopy = (LlvmInsertValueInstruction)insert.Clone();
        indices[0] = 1;
        Program.Require(insert.OperandCount == 2 && insert.Indices.SequenceEqual([0u])
            && insertCopy.Indices.SequenceEqual([0u]) && insertCopy.Parent is null,
            "InsertValue indices are immutable attributes, not SSA operands or shared mutable caller storage.");
        Program.Require(!ReferenceEquals(insert.GetOperandUse(0), insertCopy.GetOperandUse(0))
            && !ReferenceEquals(insert.GetOperandUse(1), insertCopy.GetOperandUse(1))
            && aggregate.GetNumUses() == 2 && value.GetNumUses() == 2,
            "InsertValue clones must own independent ordinary operand uses.");
        LlvmExtractValueInstruction extract = new(insert, [0]);
        LlvmExtractValueInstruction extractCopy = (LlvmExtractValueInstruction)extract.Clone();
        Program.Require(extract.OperandCount == 1 && extractCopy.OperandCount == 1
            && ReferenceEquals(extract.Type, pointer) && extract.Indices.SequenceEqual([0u]),
            "ExtractValue did not preserve its selected field type and attribute indices.");
        insert.ReplaceAllUsesWith(insertCopy);
        aggregate.ReplaceAllUsesWith(replacement);
        value.ReplaceAllUsesWith(replacementValue);
        Program.Require(insert.UseEmpty && aggregate.UseEmpty && value.UseEmpty
            && ReferenceEquals(extract.AggregateOperand, insertCopy)
            && ReferenceEquals(extractCopy.AggregateOperand, insertCopy)
            && ReferenceEquals(insert.AggregateOperand, replacement)
            && ReferenceEquals(insertCopy.InsertedValueOperand, replacementValue),
            "Aggregate instructions did not follow ordinary use-list replacement.");
        LlvmArrayType pair = LlvmArrayType.Get(integer, 2);
        LlvmStructType nested = LlvmStructType.Get(context, [pair, pointer]);
        Program.Require(ReferenceEquals(LlvmExtractValueInstruction.GetIndexedType(nested, [0, 1]), integer),
            "Nested aggregate field lookup lost its actual selected type.");
        extract.DropAllReferences();
        extractCopy.DropAllReferences();
        insert.DropAllReferences();
        insertCopy.DropAllReferences();
        Program.Require(insertCopy.UseEmpty && replacement.UseEmpty && replacementValue.UseEmpty,
            "Dropping aggregate operand references left stale users.");
    }
}
