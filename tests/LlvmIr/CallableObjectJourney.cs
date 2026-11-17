using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal sealed record CallableReceipt(int SourceCases, int Executions, int ArtifactExecutions);

internal static class CallableObjectJourney
{
    private sealed record Case(string Name, string Source, double Expected, bool Artifact = false);
    private static readonly Case[] Cases =
    [
        new("typed-call", "fn add(left: Number, right: Number) -> Number { left + right }; add(3, 4)", 7),
        new("escaped-location", """
            fn make(value: Number) { fn { value = value + 1; value } };
            let counter = make(1);
            let first = counter();
            let second = counter();
            first * 10 + second
            """, 23, true),
        new("transitive-capture", """
            fn make(value: Number) { fn middle() { fn { value } }; middle() };
            let read = make(9);
            read()
            """, 9),
        new("recursion", """
            fn sum(n: Number, acc: Number) -> Number {
                if n <= 0 { acc } else { sum(n - 1, acc + n) }
            };
            sum(20, 0)
            """, 210),
        new("generic-identity", """
            fn identity(value) { value };
            let alias = identity;
            let number = alias(6);
            let flag = identity(true);
            if flag { number } else { 0 }
            """, 6, true),
        new("dynamic-call", """
            let selected = if 1 < 2 { fn(value: Number) { value + 1 } }
                else { fn(value: Number) { value + 2 } };
            selected(4)
            """, 5),
        new("iteration-locations", """
            let first = fn { 0 };
            let second = fn { 0 };
            let third = fn { 0 };
            for item in [1, 2, 3] {
                if item == 1 { first = fn { item }; } else {
                    if item == 2 { second = fn { item }; } else { third = fn { item }; };
                };
            };
            first() + second() * 10 + third() * 100
            """, 321, true),
        new("range-function-objects", """
            let effects = 0;
            let items = [fn { effects = effects + 1; 4 }, fn { effects = effects + 1; 5 }];
            let count = 0;
            for item in items { count = count + 1; };
            effects * 10 + count
            """, 2, true),
        new("range-eager-order", """
            let order = 0;
            fn record(value: Number) { order = order * 10 + value; value };
            let items = [record(1), record(2), record(3)];
            let atConstruction = order;
            let total = 0;
            for item in items { total = total + item; };
            atConstruction * 10 + total
            """, 1236),
        new("range-scope-alias", """
            let original = scope { value: 1 };
            let items = [original, original];
            for item in items { item::value = item::value + 1; };
            original::value
            """, 3),
        new("native-math-range", "$std::math::sqrt(9) + $std::ranges::size([1, 2, 3])", 6),
        new("generic-native-callback", """
            fn twice(value) { value * 2 };
            let items = $std::ranges::transform([1, 2, 3], twice);
            let total = 0;
            for item in items { total = total + item; };
            total
            """, 12)
    ];

    public static async Task<CallableReceipt> RunAsync(string? output)
    {
        BundleUses();
        string root = Path.Combine(output ?? Path.GetFullPath(".temp/llvm-ir"), "callables");
        int executions = 0, artifacts = 0;
        foreach (bool optimize in new[] { false, true })
        foreach (Case test in Cases)
        {
            string work = Path.Combine(root, optimize ? "mem2reg-dce" : "unoptimized", test.Name);
            Directory.CreateDirectory(work);
            string sourceFile = Path.Combine(work, "source.msync");
            File.WriteAllText(sourceFile, test.Source);
            HirModule hir = Compiler.Analyze(test.Source, sourceFile);
            File.WriteAllText(Path.Combine(work, "typed-hir.txt"), HirPrinter.Print(hir));
            LlvmHirLoweringResult graph = LlvmHirLowerer.Lower(hir);
            if (test.Name == "generic-identity") CheckTemplate(graph);
            if (test.Name.StartsWith("range-", StringComparison.Ordinal)) CheckRangeObject(graph);
            File.WriteAllText(Path.Combine(work, "before.ll"), LlvmIrWriter.Write(graph.Module));
            if (optimize)
            foreach (LlvmFunction function in graph.Module.Functions.Where(function => function.BasicBlocks.Count > 0))
            {
                LlvmPromoteMemoryToRegister.Run(function, new LlvmDominatorTree(function));
                LlvmDeadCodeElimination.Run(function);
            }
            File.WriteAllText(Path.Combine(work, "after.ll"), LlvmIrWriter.Write(graph.Module));
            NativeProgram program = LlvmNativeLowerer.Lower(graph);
            if (test.Name == "generic-identity")
                Program.Require(program.Functions.Count(function => function.IsTemplate) == 1,
                    "Number/Bool specializations must retain one shared Native template identity.");
            await Expect(program, work, test.Expected);
            executions++;
            if (!optimize || !test.Artifact) continue;
            using MemoryStream bytes = new();
            NativeArtifact.Write(program, bytes);
            File.WriteAllBytes(Path.Combine(work, "program.native"), bytes.ToArray());
            bytes.Position = 0;
            NativeProgram detached = NativeArtifact.Read(bytes);
            Program.Require(detached.Backend is null && detached.Functions.All(function => function.Storage is not null),
                "Callable Native2 detached object must keep storage plans and defer executable selection.");
            await Expect(detached, work, test.Expected);
            executions++;
            artifacts++;
        }
        return new(Cases.Length, executions, artifacts);
    }

    private static void CheckRangeObject(LlvmHirLoweringResult graph)
    {
        LlvmFunction constructor = graph.Constructors.Single(entry => entry.Value == NativeKind.Range).Key;
        LlvmCallInstruction call = graph.Module.Functions.SelectMany(function => function.BasicBlocks)
            .SelectMany(block => block.Instructions).OfType<LlvmCallInstruction>()
            .Single(instruction => ReferenceEquals(instruction.CalledFunction, constructor));
        Program.Require(call.Type.IsPointerTy() && call.ArgumentCount > 0,
            "A Range literal must be an ordinary builtin constructor returning an object pointer.");
    }

    private static void CheckTemplate(LlvmHirLoweringResult graph)
    {
        Program.Require(graph.TemplateFunctions.Count == 1,
            "Aliased Number/Bool calls must retain one source template declaration.");
        LlvmFunction template = graph.TemplateFunctions.Keys.Single();
        LlvmInsertValueInstruction[] identities = graph.Module.Functions.SelectMany(function => function.BasicBlocks)
            .SelectMany(block => block.Instructions).OfType<LlvmInsertValueInstruction>()
            .Where(instruction => instruction.Indices.SequenceEqual([0u])
                && ReferenceEquals(instruction.InsertedValueOperand, template)).ToArray();
        Program.Require(identities.Length > 0 && template.Type.IsPointerTy(),
            "The actual closure descriptor must reference its template through an ordinary code-pointer operand.");
        TypeKind[] results = graph.SourceFunctions.Values.Select(info => graph.Types[info.ResultType].Kind).ToArray();
        Program.Require(results.Contains(TypeKind.Number) && results.Contains(TypeKind.Bool),
            "The generic identity source must produce both Number and Bool specializations.");
    }

    private static async Task Expect(NativeProgram program, string work, double expected)
    {
        await using NativeInterpreter interpreter = new(program,
            new ExecutionOptions(WorkingDirectory: work, SourceFile: Path.Combine(work, "source.msync")));
        NativeValue result = await interpreter.ExecuteAsync();
        Program.Require(result.AsNumber() == expected, $"{work}: expected {expected}, actual {result.AsNumber()}.");
    }

    private static void BundleUses()
    {
        LlvmContext context = new();
        LlvmType integer = LlvmType.GetInt64Ty(context);
        LlvmFunctionType voidCall = LlvmFunctionType.Get(LlvmType.GetVoidTy(context), []);
        LlvmFunction target = LlvmFunction.Create(voidCall, LlvmLinkage.ExternalLinkage, "bundle-target");
        LlvmFunction owner = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [integer, integer]),
            LlvmLinkage.InternalLinkage, "bundle-owner");
        LlvmBasicBlock block = new(context, "entry", owner);
        LlvmBinaryOperator descriptor = new(LlvmOpcode.Add, owner.Arguments[0], LlvmConstantInt.Get((LlvmIntegerType)integer, 1));
        LlvmCallInstruction call = new(voidCall, target, [], [new LlvmOperandBundleDef("msync.callee", [descriptor])]);
        block.Append(descriptor);
        block.Append(call);
        block.Append(new LlvmReturnInstruction(context));
        LlvmCallInstruction copy = (LlvmCallInstruction)call.Clone();
        LlvmOperandBundleUse originalBundle = call.GetOperandBundle("msync.callee")
            ?? throw new InvalidOperationException("Original callee bundle missing.");
        LlvmOperandBundleUse copyBundle = copy.GetOperandBundle("msync.callee")
            ?? throw new InvalidOperationException("Cloned callee bundle missing.");
        Program.Require(call.ArgumentCount == 0 && call.OperandCount == 2
            && !ReferenceEquals(originalBundle.GetInputUse(0), copyBundle.GetInputUse(0)),
            "Call bundle descriptors must be ordinary independent uses, not extra positional arguments.");
        LlvmDeadCodeElimination.Run(owner);
        Program.Require(ReferenceEquals(descriptor.Parent, block), "DCE removed a descriptor used only by a call bundle.");
        descriptor.ReplaceAllUsesWith(owner.Arguments[1]);
        Program.Require(ReferenceEquals(originalBundle.GetInput(0), owner.Arguments[1])
            && ReferenceEquals(copyBundle.GetInput(0), owner.Arguments[1]) && descriptor.UseEmpty,
            "Callee bundle views did not follow ordinary RAUW.");
        copy.DropAllReferences();
        call.DropAllReferences();
        Program.Require(owner.Arguments[1].UseEmpty, "Dropping bundle references left stale descriptor users.");
    }
}
