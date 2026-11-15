using System.Text.Json;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Hir;
using Msync.Optimization.Llvm;

internal sealed record DceReceipt(int DiscardedArithmeticRemoved, int LazyReadersRemoved,
    int EagerReadersRetained, int OutputsRetained, int CascadingInstructionsRemoved, int TraceRecords);

internal static class DceChecks
{
    private const string ScalarSource = "1 + 2; 4 + 5";
    private const string IoSource = """
        $std::io::read_lines(./not-created-lines);
        $std::io::read_bytes(./not-created-bytes);
        $std::io::read_text(./not-created-text);
        $std::io::write_text(./not-created-output, "text");
        $std::io::append_text(./not-created-append, "text");
        $std::io::write_bytes(./not-created-byte-output, $std::io::read_bytes(./not-created-active-bytes));
        $std::io::append_bytes(./not-created-byte-append, $std::io::read_bytes(./not-created-active-append));
        "text" -> ./not-created-transfer;
        """;

    internal static DceReceipt Run(string? output)
    {
        IntrusiveList();
        List<CompilationObservation> records = [];
        CompilationTrace trace = new(records.Add, includeOptimizerPasses: true);
        LlvmModule scalar = Lower(ScalarSource, "llvm-dce-scalar.msync");
        string scalarBefore = LlvmIrWriter.Write(scalar);
        LlvmBinaryOperator[] beforeArithmetic = Code(scalar).OfType<LlvmBinaryOperator>().ToArray();
        LlvmReturnInstruction scalarReturn = Code(scalar).OfType<LlvmReturnInstruction>().Single();
        LlvmValue? live = scalarReturn.ReturnValue;
        Program.Require(beforeArithmetic.Length == 2 && live is LlvmBinaryOperator,
            "The source DCE fixture must contain both discarded and returned arithmetic before transformation.");
        Run(scalar, trace);
        LlvmBinaryOperator[] afterArithmetic = Code(scalar).OfType<LlvmBinaryOperator>().ToArray();
        Program.Require(afterArithmetic.Length == 1 && ReferenceEquals(afterArithmetic[0], live)
            && ReferenceEquals(scalarReturn.ReturnValue, live),
            "DCE must remove source discarded arithmetic and retain the actual returned result.");

        LlvmModule io = Lower(IoSource, "llvm-dce-io.msync");
        string ioBefore = LlvmIrWriter.Write(io);
        LlvmFileInInst[] reads = Code(io).OfType<LlvmFileInInst>().ToArray();
        LlvmFileInInst[] deadReaders = reads.Where(read => read.UseEmpty
            && read.Format is LlvmFileInFormat.Lines or LlvmFileInFormat.Bytes).ToArray();
        LlvmFileInInst text = reads.Single(read => read.Format == LlvmFileInFormat.Text);
        LlvmFileOutInst[] writes = Code(io).OfType<LlvmFileOutInst>().ToArray();
        Program.Require(deadReaders.Length == 2 && text.UseEmpty && writes.Length == 5,
            "The source IO fixture must distinguish unused lazy descriptors, eager text input and all output modes.");
        Run(io, trace);
        LlvmInstruction[] retainedIo = Code(io);
        Program.Require(deadReaders.All(read => read.Parent is null)
            && retainedIo.Contains(text) && writes.All(retainedIo.Contains),
            "DCE must remove unused lazy readers, retain eager read_text, and retain every explicit file output.");
        int cascade = Cascade(records, trace);
        CompilationObservation[] passes = records.Where(record => record.Name == "optimizer.pass.llvm-dce").ToArray();
        Program.Require(passes.Any(record => (record.Metrics?.GetValueOrDefault("predicate.checked") ?? 0) > 0)
            && passes.Sum(record => (long)(record.Metrics?.GetValueOrDefault("instructions.erased") ?? 0)) >= 6
            && passes.Any(record => (record.Metrics?.GetValueOrDefault("worklist.popped") ?? 0) > 0),
            "DCE telemetry must expose actual checked/erased/worklist work.");
        Program.Require(records.Any(record => record.Name == "optimizer.llvm-dce.erase"
            && record.Attributes is { } attributes && attributes.GetValueOrDefault("source.name") == "llvm-dce-scalar.msync"
            && attributes.ContainsKey("source.start") && attributes.ContainsKey("source.length")),
            "DCE erase observations lost their producing source span.");
        if (output is not null)
        {
            File.WriteAllText(Path.Combine(output, "dce-scalar.msync"), ScalarSource);
            File.WriteAllText(Path.Combine(output, "dce-io.msync"), IoSource);
            File.WriteAllText(Path.Combine(output, "dce-scalar.before.ll"), scalarBefore);
            File.WriteAllText(Path.Combine(output, "dce-scalar.after.ll"), LlvmIrWriter.Write(scalar));
            File.WriteAllText(Path.Combine(output, "dce-io.before.ll"), ioBefore);
            File.WriteAllText(Path.Combine(output, "dce-io.after.ll"), LlvmIrWriter.Write(io));
            File.WriteAllLines(Path.Combine(output, "dce.trace.jsonl"), records.Select(record => JsonSerializer.Serialize(record)));
        }
        return new(beforeArithmetic.Length - afterArithmetic.Length, deadReaders.Length, 1, writes.Length, cascade, records.Count);
    }

    private static int Cascade(List<CompilationObservation> records, CompilationTrace trace)
    {
        LlvmContext context = new();
        LlvmIntegerType type = LlvmType.GetInt64Ty(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [type]),
            LlvmLinkage.InternalLinkage, "cascade");
        LlvmBasicBlock block = new(context, "entry", function);
        LlvmBinaryOperator first = new(LlvmOpcode.Add, function.Arguments[0], LlvmConstantInt.Get(type, 1))
            { Source = new("llvm-dce-cascade.msync", 0, 1) };
        LlvmBinaryOperator second = new(LlvmOpcode.Mul, first, LlvmConstantInt.Get(type, 2))
            { Source = new("llvm-dce-cascade.msync", 2, 1) };
        LlvmBinaryOperator third = new(LlvmOpcode.Sub, second, LlvmConstantInt.Get(type, 3))
            { Source = new("llvm-dce-cascade.msync", 4, 1) };
        first.SetName("first"); second.SetName("second"); third.SetName("third");
        block.Append(first); block.Append(second); block.Append(third);
        LlvmReturnInstruction terminator = new(context);
        block.Append(terminator);
        int start = records.Count;
        Program.Require(LlvmDeadCodeElimination.Run(function, trace), "The unused dependency chain was not removed.");
        Program.Require(block.Instructions.Count == 1 && ReferenceEquals(block.FirstInstruction, terminator)
            && ReferenceEquals(block.LastInstruction, terminator) && terminator.Previous is null && terminator.Next is null
            && first.Parent is null && second.Parent is null && third.Parent is null
            && first.UseEmpty && second.UseEmpty && function.Arguments[0].UseEmpty,
            "Cascading DCE did not update instruction links and operand use lists together.");
        string[] erased = records.Skip(start).Where(record => record.Name == "optimizer.llvm-dce.erase")
            .Select(record => record.Attributes!["instruction.name"]).ToArray();
        Program.Require(erased.SequenceEqual(["third", "second", "first"]),
            "DCE's worklist did not cascade from the dead consumer to its newly unused producers.");
        return 3;
    }

    private static void IntrusiveList()
    {
        LlvmContext context = new();
        LlvmIntegerType type = LlvmType.GetInt64Ty(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [type]),
            LlvmLinkage.InternalLinkage, "instruction-links");
        LlvmBasicBlock block = new(context, "entry", function);
        LlvmInstruction first = Add(), middle = Add(), last = Add(), head = Add(), tail = Add();
        block.Append(first); block.Append(last); block.InsertBefore(middle, last);
        block.InsertBefore(head, first); block.Append(tail);
        Links(block, [head, first, middle, last, tail]);
        block.Remove(middle); Links(block, [head, first, last, tail]);
        block.Remove(head); Links(block, [first, last, tail]);
        block.Remove(tail); Links(block, [first, last]);
        block.Remove(first); Links(block, [last]);
        block.Remove(last); Links(block, []);
        foreach (LlvmInstruction instruction in new[] { first, middle, last, head, tail })
        {
            Program.Require(instruction.Parent is null && instruction.Previous is null && instruction.Next is null,
                "Removing an instruction must detach both neighboring list links.");
            instruction.DropAllReferences();
        }
        Program.Require(function.Arguments[0].UseEmpty, "Detached property instructions retained obsolete operand references.");
        LlvmInstruction Add() => new LlvmBinaryOperator(LlvmOpcode.Add, function.Arguments[0], LlvmConstantInt.Get(type, 1));
    }

    private static void Links(LlvmBasicBlock block, IReadOnlyList<LlvmInstruction> expected)
    {
        Program.Require(block.Instructions.Count == expected.Count && block.Instructions.SequenceEqual(expected)
            && ReferenceEquals(block.FirstInstruction, expected.Count == 0 ? null : expected[0])
            && ReferenceEquals(block.LastInstruction, expected.Count == 0 ? null : expected[^1]),
            "Intrusive block iteration/head/tail/count disagree.");
        for (int index = 0; index < expected.Count; index++)
            Program.Require(ReferenceEquals(expected[index].Parent, block)
                && ReferenceEquals(expected[index].Previous, index == 0 ? null : expected[index - 1])
                && ReferenceEquals(expected[index].Next, index + 1 == expected.Count ? null : expected[index + 1]),
                "Intrusive block previous/next links disagree with program order.");
    }

    private static void Run(LlvmModule module, CompilationTrace trace)
    {
        foreach (LlvmFunction function in module.Functions.Where(function => !function.IsDeclaration))
            LlvmDeadCodeElimination.Run(function, trace);
    }
    private static LlvmModule Lower(string source, string name) => LlvmHirLowerer.Lower(Compiler.Analyze(source, name)).Module;
    private static LlvmInstruction[] Code(LlvmModule module) => module.Functions.SelectMany(function => function.BasicBlocks)
        .SelectMany(block => block.Instructions).ToArray();
}
