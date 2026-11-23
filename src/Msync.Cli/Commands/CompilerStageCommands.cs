using System.Globalization;
using System.Text.Json.Nodes;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Hir;
using static Msync.Cli.CommandPipeline;
using SourceCompiler = Msync.Compiler.Compiler;

namespace Msync.Cli;

internal static class CompilerStageCommands
{
    internal static bool IsStage(string command) => command is "analyze" or "lower" or "optimize" or "codegen" or "bind";
    internal static bool IsCheckpoint(string path) => path.EndsWith(".mllvm", StringComparison.OrdinalIgnoreCase);

    internal static async ValueTask<int> Run(CommandOptions options, string file, TelemetrySession? telemetry, string color)
    {
        var input = Input(options.Command, file);
        var output = Output.Resolve(options, file);
        telemetry?.SetAttribute("input.kind", input);
        telemetry?.SetAttribute("artifact.export_kind", output.Kind);
        telemetry?.SetAttribute("artifact.output_mode", output.Path == "-" ? "stdout" : options.Output is null ? "default" : "explicit");

        switch (options.Command)
        {
            case "analyze":
                WriteHir(await ReadSemantic(input, file, telemetry), output.Path, telemetry);
                break;
            case "lower":
                var hir = await ReadSemantic(input, file, telemetry);
                var lowered = Stage(telemetry, "lower", hir,
                    static (state, trace) => LlvmCheckpoint.Lowered(SourceCompiler.LowerLlvm(state, trace)), file);
                WriteCheckpoint(lowered, output.Path, telemetry);
                break;
            case "optimize":
                var checkpoint = ReadCheckpoint(file, telemetry);
                if (checkpoint.Phase != LlvmCheckpointPhase.Lowered)
                    throw new ArgumentException("'optimize' needs a lowered checkpoint. Start again from the output of 'msync lower'.");
                var optimized = options.Optimization == 0 ? checkpoint.Graph
                    : Stage(telemetry, "optimize", (checkpoint.Graph, options.Optimization),
                        static (state, trace) => SourceCompiler.OptimizeLlvm(state.Graph, state.Optimization, trace), file);
                WriteCheckpoint(new(optimized, LlvmCheckpointPhase.Optimized, options.Optimization), output.Path, telemetry);
                break;
            case "codegen":
                if (input != "mllvm") telemetry?.SetAttribute("optimization.level", options.Optimization.ToString(CultureInfo.InvariantCulture));
                HpirProgram program = input == "mllvm"
                    ? Resume(ReadCheckpoint(file, telemetry), options, file, telemetry)
                    : HpirCommandPipeline.Compile(null, file, await ReadSemantic(input, file, telemetry), options.Optimization, telemetry);
                HpirCommandPipeline.Write(program, output.Path, telemetry);
                break;
            case "bind":
                var portable = file == "-" ? ReadPortableInput(telemetry) : ArtifactCommands.ReadHpirArtifact(file, telemetry);
                if (portable.Stage != HpirStage.Relocatable) throw new ArgumentException("'bind' needs a relocatable .mso program.");
                return HpirCommandPipeline.Run(portable,
                    new CompileOutput(output.Path, CompileEmission.Prepared, output.Announce), options, file, telemetry, color, null);
        }
        if (output.Announce) TerminalPresentation.ArtifactWritten(output.Path, output.Description, color);
        return 0;
    }

    private static string Input(string command, string file)
    {
        string suffix = Path.GetExtension(file).ToLowerInvariant();
        if (command is "lower" or "codegen" && file != "-" && suffix is not (".msync" or ".mhir" or ".mllvm" or ".mso" or ".mexe"))
            suffix = StageArtifactExtension(file);
        bool source = suffix is not (".mhir" or ".mllvm" or ".mso" or ".mexe");
        return command switch
        {
            "analyze" when source => "source",
            "lower" when suffix == ".mhir" || file == "-" => "hir",
            "lower" when source => "source",
            "optimize" => "mllvm",
            "codegen" when suffix == ".mllvm" || file == "-" => "mllvm",
            "codegen" when suffix == ".mhir" => "hir",
            "codegen" when source => "source",
            "bind" when suffix != ".mexe" => "object",
            "analyze" or "lower" or "codegen" or "bind" => throw new ArgumentException(command switch
            {
                "analyze" => "'analyze' needs source, not a compiler artifact.",
                "lower" => "'lower' needs a .mhir file or source.",
                "codegen" => "'codegen' needs a .mllvm checkpoint, .mhir file or source.",
                _ => "'bind' needs a relocatable .mso program."
            }),
            _ => throw new ArgumentException($"Unknown compiler stage '{command}'.")
        };
    }

    private static string StageArtifactExtension(string file)
    {
        using FileStream stream = File.OpenRead(file);
        Span<byte> prefix = stackalloc byte[8];
        int length = stream.ReadAtLeast(prefix, prefix.Length, false);
        if (LlvmCheckpointArtifact.HasMagic(prefix[..length])) return ".mllvm";
        if (HirArtifact.IsHir(prefix[..length])) return ".mhir";
        return HpirArtifact.HasMagic(prefix[..length]) ? ".mso" : "";
    }

    private static async ValueTask<HirModule> ReadSemantic(string input, string file, TelemetrySession? telemetry)
    {
        if (input == "source") return Analyze(await ReadProgramSource(file, null, telemetry), file, telemetry);
        if (file != "-") return ReadHir(file, telemetry);
        using Stream stream = Console.OpenStandardInput();
        using TelemetrySpan? span = telemetry?.Begin("artifact.read");
        span?.SetAttribute("artifact.kind", "hir");
        return span is null ? HirArtifact.Read(stream)
            : span.Measure(stream, state => HirArtifact.Read(state, span.CompilerTrace));
    }

    private static HpirProgram ReadPortableInput(TelemetrySession? telemetry)
    {
        using Stream stream = Console.OpenStandardInput();
        using TelemetrySpan? span = telemetry?.Begin("artifact.read");
        span?.SetAttribute("artifact.kind", "object");
        span?.SetAttribute("artifact.representation", "hpir");
        return span is null ? HpirArtifact.ReadTrusted(stream)
            : span.Measure(stream, state => HpirArtifact.ReadTrusted(state, span.CompilerTrace));
    }

    private static HpirProgram Resume(LlvmCheckpoint checkpoint, CommandOptions options, string file, TelemetrySession? telemetry)
    {
        RecordPhase(telemetry, checkpoint);
        if (options.ExplicitOptimization && options.Optimization != checkpoint.Optimization)
            throw new ArgumentException($"Checkpoint is {checkpoint.PhaseName} at O{checkpoint.Optimization}; " +
                $"cannot resume codegen with -O{options.Optimization}. Optimize a lowered checkpoint at the requested level first.");
        return Stage(telemetry, "codegen", checkpoint,
            static (state, trace) => SourceCompiler.CodegenHpir(state.Graph, state.Optimization, trace), file);
    }

    private static T Stage<TState, T>(TelemetrySession? telemetry, string name, TState state,
        Func<TState, CompilationTrace?, T> run, string input)
    {
        using TelemetrySpan? span = telemetry?.Begin(name);
        span?.SetAttribute("input.path", input);
        return span is null ? run(state, null)
            : span.Measure((state, run, trace: span.CompilerTrace), static work => work.run(work.state, work.trace));
    }

    private static LlvmCheckpoint ReadCheckpoint(string file, TelemetrySession? telemetry, byte[]? input = null)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.read");
        span?.SetAttribute("artifact.kind", "mllvm");
        var work = (file, input, trace: span?.CompilerTrace);
        var checkpoint = span is null ? Read(work) : span.Measure(work, Read);
        span?.SetAttribute("checkpoint.phase", checkpoint.PhaseName);
        span?.SetAttribute("optimization.level", checkpoint.Optimization.ToString(CultureInfo.InvariantCulture));
        if (span is not null)
        {
            var entry = checkpoint.Graph.Entry;
            span.SetAttribute("function.name", entry.Name);
            if (entry.BasicBlocks[0].FirstInstruction?.Source is { } source) span.SetAttribute("source.file", source.Source);
        }
        return checkpoint;

        static LlvmCheckpoint Read((string file, byte[]? input, CompilationTrace? trace) state)
        {
            if (state.input is { } bytes)
            {
                using MemoryStream buffer = new(bytes, writable: false);
                return LlvmCheckpointArtifact.Read(buffer, state.trace);
            }
            if (state.file != "-") return LlvmCheckpointArtifact.Read(state.file, state.trace);
            using Stream stdin = Console.OpenStandardInput();
            return LlvmCheckpointArtifact.Read(stdin, state.trace);
        }
    }

    private static void WriteCheckpoint(LlvmCheckpoint checkpoint, string path, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.write");
        span?.SetAttribute("artifact.kind", "mllvm");
        span?.SetAttribute("checkpoint.phase", checkpoint.PhaseName);
        try
        {
            if (path == "-")
            {
                using Stream stream = Console.OpenStandardOutput();
                LlvmCheckpointArtifact.Write(checkpoint, stream, span?.CompilerTrace);
            }
            else LlvmCheckpointArtifact.Write(checkpoint, path, span?.CompilerTrace);
            span?.Complete();
        }
        catch (Exception error) { span?.Fail(error); throw; }
    }

    private static void RecordPhase(TelemetrySession? telemetry, LlvmCheckpoint checkpoint)
    {
        telemetry?.SetAttribute("checkpoint.phase", checkpoint.PhaseName);
        telemetry?.SetAttribute("optimization.level", checkpoint.Optimization.ToString(CultureInfo.InvariantCulture));
    }

    internal static int Disassemble(string file, byte[]? bytes, CommandOptions options, string color, TelemetrySession? telemetry)
    {
        if (options.EditorJson)
            throw new ArgumentException("The .mllvm checkpoint view supports text and --json, not --editor-json.");
        var checkpoint = ReadCheckpoint(file, telemetry, bytes);
        RecordPhase(telemetry, checkpoint);
        using TelemetrySpan? span = telemetry?.Begin("artifact.disassemble");
        span?.SetAttribute("artifact.kind", "mllvm");
        try
        {
            string header = $"; msync checkpoint: {checkpoint.PhaseName}, O{checkpoint.Optimization}";
            if (options.Json)
            {
                Console.WriteLine(new JsonObject
                {
                    ["kind"] = "mllvm", ["phase"] = checkpoint.PhaseName, ["optimization"] = checkpoint.Optimization,
                    ["text"] = header + Environment.NewLine + LlvmIrWriter.Write(checkpoint.Graph.Module, span?.CompilerTrace)
                }.ToJsonString());
            }
            else if (TerminalPresentation.Colors(color))
                Console.Write(TerminalPresentation.LlvmSyntax(header + Environment.NewLine +
                    LlvmIrWriter.Write(checkpoint.Graph.Module, span?.CompilerTrace)));
            else
            {
                Console.WriteLine(header);
                LlvmIrWriter.Write(checkpoint.Graph.Module, Console.Out, span?.CompilerTrace);
            }
            span?.Complete();
            return 0;
        }
        catch (Exception error) { span?.Fail(error); throw; }
    }

    private sealed record Output(string Path, string Kind, string Description, bool Announce)
    {
        internal static Output Resolve(CommandOptions options, string input)
        {
            var (extension, kind, description) = options.Command switch
            {
                "analyze" => (".mhir", "hir", "typed HIR"),
                "lower" => (".mllvm", "mllvm", "lowered LLVM checkpoint"),
                "optimize" => (".mllvm", "mllvm", "optimized LLVM checkpoint"),
                "codegen" => (".mso", "object", "portable program"),
                "bind" => (".mexe", "executable", "prepared program"),
                _ => throw new ArgumentException($"Unknown compiler stage '{options.Command}'.")
            };
            if (input == "-" && options.Output is null) throw new ArgumentException("Stage input from stdin requires '-o FILE' or '-o -'.");
            string path = options.Output ?? System.IO.Path.ChangeExtension(input, options.Command == "optimize" ? ".opt.mllvm" : extension);
            if (path != "-")
            {
                string suffix = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (suffix is ".msync" or ".mhir" or ".mllvm" or ".mso" or ".mexe" && suffix != extension)
                    throw new ArgumentException($"'{options.Command}' produces {extension}; cannot write it as '{path}'.");
                if (input != "-" && string.Equals(System.IO.Path.GetFullPath(input), System.IO.Path.GetFullPath(path),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ArgumentException($"Stage output would overwrite its input '{input}'; choose a different -o path.");
            }
            return new(path, kind, description, options.Output is null);
        }
    }
}
