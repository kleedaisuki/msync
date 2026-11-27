using System.Text;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal sealed record ExecutionReceipt(int Executions, int ArtifactExecutions, int PromotedAllocas,
    int LoopPhis, int CheckedFiles, int SourceErrors);

internal static class OptimizedExecutionJourney
{
    private const string LoopSource = """
        let index = 0;
        let total = 0;
        let result = loop {
            if index >= 5 { break total; };
            if index < 3 { total = total + index; } else { total = total + 2 * index; };
            index = index + 1;
        };
        result
        """;
    private const string ScopeSource = """
        let original = object { left: 1, right: 2 };
        let alias = original;
        alias::left = 4;
        original::left + original::right
        """;
    private const string IoSource = """
        let text = $std::io::read_text(./input.txt);
        $std::io::write_text(./text-out.txt, text);
        $std::io::append_text(./text-out.txt, "END");
        let bytes = $std::io::read_bytes(./input.bin);
        $std::io::write_bytes(./byte-out.bin, bytes);
        $std::io::append_bytes(./byte-out.bin, $std::io::read_bytes(./input.bin));
        $std::io::write_text(./joined.txt, "");
        for line in $std::io::read_lines(./input.txt) {
            $std::io::append_text(./joined.txt, line);
            $std::io::append_text(./joined.txt, "|");
        };
        for line in $std::io::read_lines(./input.txt) {
            $std::io::write_text(./first.txt, line);
            break;
        };
        ./copy-source.bin -> ./copy-target.bin;
        null
        """;
    private static readonly byte[] InputBytes = [0, 1, 127, 128, 255];
    private static readonly byte[] TransferBytes = [9, 0, 8, 255];

    public static async Task<ExecutionReceipt> RunAsync(string? output)
    {
        string root = Path.Combine(output ?? Path.GetFullPath(".temp/llvm-ir"), "execution");
        Directory.CreateDirectory(root);
        int executions = 0, artifacts = 0, promoted = 0, phis = 0, files = 0, errors = 0;
        foreach (bool optimize in new[] { false, true })
        {
            string mode = optimize ? "mem2reg-dce" : "unoptimized";
            foreach ((string name, string source, double expected) in new[]
            {
                ("loop", LoopSource, 17d), ("scope-alias", ScopeSource, 6d)
            })
            {
                string work = Path.Combine(root, mode, name);
                Directory.CreateDirectory(work);
                LlvmHirLoweringResult lowering = Prepare(source, work, optimize, out int removed, out int loopPhis);
                promoted += removed;
                if (name == "loop" && optimize)
                {
                    Program.Require(removed > 0 && loopPhis >= 2,
                        "The actual loop must promote its carried locals into SSA PHIs.");
                    phis = loopPhis;
                }
                NativeProgram program = LlvmNativeLowerer.Lower(lowering);
                await ExpectNumber(program, work, expected);
                executions++;
                if (optimize)
                {
                    NativeProgram decoded = RoundTrip(program, work);
                    await ExpectNumber(decoded, work, expected);
                    executions++;
                    artifacts++;
                }
            }
            string ioWork = Path.Combine(root, mode, "io");
            Directory.CreateDirectory(ioWork);
            File.WriteAllText(Path.Combine(ioWork, "input.txt"), "alpha\nbeta\n", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(ioWork, "input.bin"), InputBytes);
            File.WriteAllBytes(Path.Combine(ioWork, "copy-source.bin"), TransferBytes);
            LlvmHirLoweringResult io = Prepare(IoSource, ioWork, optimize, out _, out _);
            await Execute(LlvmNativeLowerer.Lower(io), ioWork);
            executions++;
            CheckBytes(ioWork, "text-out.txt", Encoding.UTF8.GetBytes("alpha\nbeta\nEND"));
            CheckBytes(ioWork, "byte-out.bin", [.. InputBytes, .. InputBytes]);
            CheckBytes(ioWork, "joined.txt", Encoding.UTF8.GetBytes("alpha|beta|"));
            CheckBytes(ioWork, "first.txt", Encoding.UTF8.GetBytes("alpha"));
            CheckBytes(ioWork, "copy-target.bin", TransferBytes);
            files += 5;

            string missingWork = Path.Combine(root, mode, "missing");
            Directory.CreateDirectory(missingWork);
            string missing = Path.Combine(missingWork, "absent.txt");
            if (File.Exists(missing)) File.Delete(missing);
            string dependent = Path.Combine(missingWork, "must-not-publish.txt");
            if (File.Exists(dependent)) File.Delete(dependent);
            LlvmHirLoweringResult failure = Prepare(
                "$std::io::write_text(./must-not-publish.txt, $std::io::read_text(./absent.txt)); null", missingWork,
                optimize, out _, out _);
            NativeProgram failingProgram = LlvmNativeLowerer.Lower(failure);
            try
            {
                await Execute(failingProgram, missingWork);
                throw new InvalidOperationException("An output consuming the missing actual file succeeded.");
            }
            catch (MsyncException error)
            {
                Program.Require(error.Span is { Length: > 0 } span && span.Source == Path.Combine(missingWork, "source.msync")
                    && error.ToString().Contains("absent.txt", StringComparison.Ordinal),
                    "Missing-file failure lost its concrete filename or actual source span.");
                Program.Require(!File.Exists(dependent), "A failed read dependency published its dependent output.");
                File.WriteAllText(Path.Combine(missingWork, "diagnostic.txt"), error.ToString());
                errors++;
            }
        }
        return new(executions, artifacts, promoted, phis, files, errors);
    }

    private static LlvmHirLoweringResult Prepare(string source, string work, bool optimize,
        out int promoted, out int phis)
    {
        string sourceFile = Path.Combine(work, "source.msync");
        File.WriteAllText(sourceFile, source);
        LlvmHirLoweringResult result = LlvmHirLowerer.Lower(Compiler.Analyze(source, sourceFile));
        File.WriteAllText(Path.Combine(work, "before.ll"), LlvmIrWriter.Write(result.Module));
        int before = Instructions(result).OfType<LlvmAllocaInstruction>().Count();
        foreach (LlvmFunction function in result.Module.Functions.Where(function => function.BasicBlocks.Count > 0))
        {
            LlvmDominatorTree dom = new(function);
            LlvmLoopInfo loops = new(function, dom);
            foreach (LlvmLoop loop in loops.TopLevelLoops)
                Program.Require(loop.Blocks.All(block => dom.Dominates(loop.Header, block)),
                    "A real source loop header must dominate its natural-loop members.");
            if (source == LoopSource)
                Program.Require(loops.TopLevelLoops.Count == 1, "The scalar source loop did not produce one natural loop.");
            if (!optimize) continue;
            LlvmPromoteMemoryToRegister.Run(function, dom);
            LlvmDeadCodeElimination.Run(function);
        }
        promoted = before - Instructions(result).OfType<LlvmAllocaInstruction>().Count();
        phis = Instructions(result).Count(instruction => instruction.Opcode == LlvmOpcode.PHI);
        File.WriteAllText(Path.Combine(work, "after.ll"), LlvmIrWriter.Write(result.Module));
        return result;
    }

    private static IEnumerable<LlvmInstruction> Instructions(LlvmHirLoweringResult result) => result.Module.Functions
        .SelectMany(function => function.BasicBlocks).SelectMany(block => block.Instructions);

    private static NativeProgram RoundTrip(NativeProgram program, string work)
    {
        Program.Require(program.Backend is null && program.Functions.All(function => function.Storage is not null),
            "The adapter must supply storage plans without selecting an executable image.");
        using MemoryStream stream = new();
        NativeArtifact.Write(program, stream);
        File.WriteAllBytes(Path.Combine(work, "program.native"), stream.ToArray());
        stream.Position = 0;
        NativeProgram decoded = NativeArtifact.Read(stream);
        Program.Require(decoded.Backend is null && decoded.Functions.All(function => function.Storage is not null),
            "Native2 object roundtrip must keep storage plans and defer executable selection.");
        return decoded;
    }

    private static async Task ExpectNumber(NativeProgram program, string work, double expected)
    {
        NativeValue actual = await Execute(program, work);
        Program.Require(actual.AsNumber() == expected, $"{work}: expected {expected}, got {actual.AsNumber()}.");
    }

    private static async Task<NativeValue> Execute(NativeProgram program, string work)
    {
        await using NativeInterpreter interpreter = new(program,
            new ExecutionOptions(WorkingDirectory: work, SourceFile: Path.Combine(work, "source.msync")));
        return await interpreter.ExecuteAsync();
    }

    private static void CheckBytes(string work, string name, byte[] expected) =>
        Program.Require(File.ReadAllBytes(Path.Combine(work, name)).AsSpan().SequenceEqual(expected),
            $"{work}/{name}: actual output bytes differ from the language IO contract.");
}
