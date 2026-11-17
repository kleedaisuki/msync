using System.Text;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal sealed record TypeOperationReceipt(int SourceCases, int Executions, int ArtifactExecutions,
    int ConversionErrors, int CheckedFiles);

internal static class TypeOperationJourney
{
    private sealed record Case(string Name, string Source, double Expected, bool Artifact = false);
    private static readonly Case[] Cases =
    [
        new("nullable-local", """
            let value?: Number = 1;
            value = null;
            let empty = if value is null { 10 } else { 0 };
            value = 4;
            if value is not null { empty + value } else { 0 }
            """, 14, true),
        new("nullable-returns", """
            fn choose(present: Bool) -> Number? { if present { 7 } else { null } };
            let left? = choose(true);
            let right? = choose(false);
            if left is not null { if right is null { left + 1 } else { 0 } } else { 0 }
            """, 8),
        new("union-conversions", """
            fn choose(flag: Bool) -> Number or String { if flag { 7 } else { "8" } };
            let value: Number or String = choose(true);
            let first = value as Number;
            value = choose(false);
            first + (value as Number)
            """, 15, true),
        new("union-range-snapshot", """
            let original: Range<Number> or String = [2, 3];
            fn identity(value: Range<Number> or String) { value };
            let saved = identity(original);
            original = "later";
            $std::ranges::size(saved)
            """, 2),
        new("nullable-capture", """
            let value?: Number = 2;
            let clear = fn { value = null; };
            clear();
            if value is null { 9 } else { 0 }
            """, 9),
        new("nullable-field", """
            let object = scope { value?: 1 };
            object::value = null;
            let cleared = object::value is null;
            object::value = 4;
            if cleared { object::value } else { 0 }
            """, 4),
        new("type-denotation-match", """
            let target = if 1 < 2 { Number } else { String };
            match target { Number => { "41" as Number }, _ => { 0 } }
            """, 41),
        new("connection-conversion", """
            let endpoint = "ssh://journey-no-auth.invalid" as Connection;
            if endpoint is Connection {
                if endpoint as String == "ssh://journey-no-auth.invalid" { 1 } else { 0 }
            } else { 0 }
            """, 1),
        new("heterogeneous-regex-match", """
            fn score(value: Number or String or Null) {
                match value { <a+> => { 4 }, Number => { value + 1 }, _ => { 0 } }
            };
            score("aaa") + score(2) + score(null)
            """, 7),
        new("path-regex-match", "match ./alpha.txt { <alpha[.]txt> => { 5 }, _ => { 0 } }", 5),
        new("nullable-ieee", """
            let value?: Number = -0;
            let negative = if value is not null { if 1 / value < 0 { 10 } else { 0 } } else { 0 };
            value = 0 / 0;
            if value is not null { if value != value { negative + 1 } else { 0 } } else { 0 }
            """, 11),
        new("structural-physical-layouts", """
            fn touch(row: Scope) { row::value = row::value + 1; row::value };
            fn select(flag: Bool) {
                let row = if flag { scope { padding: 99, value: 3 } }
                    else { scope { value: 4, padding: 88 } };
                touch(row)
            };
            select(true) * 10 + select(false)
            """, 45, true),
        new("static-scope-identity", """
            fn named(value: Number) { scope State { stored: value }; State };
            let first = named(1);
            first::stored = 9;
            let later = named(2);
            later::stored
            """, 9, true)
    ];
    private const string OperationSource = """
        ./input/<.+> -> ./selected/;
        let child = release /usr/bin/printf("process-ok");
        acquire child;
        child::stdout -> ./process.txt;
        acquire { "barrier-ok" -> ./barrier.txt; };
        acquire;
        null
        """;

    internal static async Task<TypeOperationReceipt> RunAsync(string? output)
    {
        string root = Path.Combine(output ?? Path.GetFullPath(".temp/llvm-ir"), "types-operations");
        int executions = 0, artifacts = 0, errors = 0, checkedFiles = 0;
        foreach (bool optimize in new[] { false, true })
        {
            string mode = optimize ? "mem2reg-dce" : "unoptimized";
            foreach (Case test in Cases)
            {
                string work = Path.Combine(root, mode, test.Name);
                NativeProgram program = Prepare(test.Source, work, optimize);
                NativeValue result = await Execute(program, work);
                Program.Require(result.AsNumber() == test.Expected,
                    $"{work}: expected {test.Expected}, actual {result.AsNumber()}.");
                executions++;
                if (!optimize || !test.Artifact) continue;
                using MemoryStream bytes = new();
                NativeArtifact.Write(program, bytes);
                File.WriteAllBytes(Path.Combine(work, "program.native"), bytes.ToArray());
                bytes.Position = 0;
                NativeProgram detached = NativeArtifact.Read(bytes);
                Program.Require(detached.Backend is null && detached.Functions.All(function => function.Storage is not null),
                    "Native2 detached type/object program must keep storage plans and defer executable selection.");
                NativeValue decoded = await Execute(detached, work);
                Program.Require(decoded.AsNumber() == test.Expected, $"{work}: detached Native2 result differs.");
                executions++;
                artifacts++;
            }
            string operationWork = Path.Combine(root, mode, "file-process-barrier");
            Directory.CreateDirectory(Path.Combine(operationWork, "input"));
            File.WriteAllText(Path.Combine(operationWork, "input", "item.txt"), "selected-file", new UTF8Encoding(false));
            await Execute(Prepare(OperationSource, operationWork, optimize), operationWork);
            executions++;
            CheckText(operationWork, Path.Combine("selected", "item.txt"), "selected-file");
            CheckText(operationWork, "process.txt", "process-ok");
            CheckText(operationWork, "barrier.txt", "barrier-ok");
            checkedFiles += 3;

            string errorWork = Path.Combine(root, mode, "conversion-error");
            NativeProgram failing = Prepare("\"not-a-number\" as Number", errorWork, optimize);
            try
            {
                await Execute(failing, errorWork);
                throw new InvalidOperationException("Invalid numeric text conversion unexpectedly succeeded.");
            }
            catch (MsyncException error)
            {
                Program.Require(error.Span is { Length: > 0 } span && span.Source == Path.Combine(errorWork, "source.msync"),
                    "Value-dependent conversion failure lost its actual producing source span.");
                File.WriteAllText(Path.Combine(errorWork, "diagnostic.txt"), error.ToString());
                errors++;
            }
        }
        return new(Cases.Length + 1, executions, artifacts, errors, checkedFiles);
    }

    private static NativeProgram Prepare(string source, string work, bool optimize)
    {
        Directory.CreateDirectory(work);
        string sourceFile = Path.Combine(work, "source.msync");
        File.WriteAllText(sourceFile, source);
        HirModule hir = Compiler.Analyze(source, sourceFile);
        File.WriteAllText(Path.Combine(work, "typed-hir.txt"), HirPrinter.Print(hir));
        LlvmHirLoweringResult graph = LlvmHirLowerer.Lower(hir);
        File.WriteAllText(Path.Combine(work, "before.ll"), LlvmIrWriter.Write(graph.Module));
        if (optimize)
        foreach (LlvmFunction function in graph.Module.Functions.Where(function => function.BasicBlocks.Count > 0))
        {
            LlvmPromoteMemoryToRegister.Run(function, new LlvmDominatorTree(function));
            LlvmDeadCodeElimination.Run(function);
        }
        File.WriteAllText(Path.Combine(work, "after.ll"), LlvmIrWriter.Write(graph.Module));
        return LlvmNativeLowerer.Lower(graph);
    }

    private static async Task<NativeValue> Execute(NativeProgram program, string work)
    {
        await using NativeInterpreter interpreter = new(program,
            new ExecutionOptions(WorkingDirectory: work, SourceFile: Path.Combine(work, "source.msync")));
        return await interpreter.ExecuteAsync();
    }
    private static void CheckText(string work, string file, string expected) =>
        Program.Require(File.ReadAllBytes(Path.Combine(work, file)).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)),
            $"{work}/{file}: actual operation output differs.");
}
