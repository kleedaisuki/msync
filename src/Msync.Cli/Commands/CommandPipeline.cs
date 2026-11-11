using Msync.Core;
using Msync.Hir;
using Msync.Optimization;
using Msync.Runtime;
using static Msync.Cli.ArtifactCommands;
using static Msync.Cli.SourceCommands;
using SourceCompiler = Msync.Compiler.Compiler;

namespace Msync.Cli;

internal static class CommandPipeline
{
    internal static string InputKind(string file) =>
        HirCommands.IsArtifact(file) ? "hir" : file.EndsWith(".mexe", StringComparison.OrdinalIgnoreCase) ? "executable" : "object";

    internal static ValueTask<string> ReadProgramSource(string file, TextReader? input, TelemetrySession? telemetry) =>
        telemetry?.Begin("source.read", TelemetryLevel.Phases) is { } span
            ? ReadObserved(file, input, span) : ReadSource(file, input);

    private static async ValueTask<string> ReadObserved(string file, TextReader? input, TelemetrySpan span)
    {
        using (span)
        {
            try
            {
                string text = await ReadSource(file, input);
                span.SetMetric("source.utf16_units", text.Length);
                span.Complete();
                return text;
            }
            catch (Exception error) { span.Fail(error); throw; }
        }
    }

    internal static HirModule ReadHir(string file, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.read");
        span?.SetAttribute("artifact.kind", "hir");
        return span is null ? HirArtifact.Read(file)
            : span.Measure(file, static file => HirArtifact.Read(file));
    }

    internal static Module ReadBytecode(string file, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.read");
        if (span is null) return Artifact.Read(file, formBlocks: false);
        Module module = span.Measure(file, static file => Artifact.Read(file, formBlocks: false));
        string kind = module.Stage == ArtifactStage.Relocatable ? "object" : "executable";
        span.SetAttribute("artifact.kind", kind);
        telemetry!.SetAttribute("input.kind", kind);
        return module;
    }

    internal static HirModule Analyze(string source, string file, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("compile");
        span?.SetAttribute("source.path", file);
        return span is null ? SourceCompiler.Analyze(source, file)
            : span.Measure((source, file, trace: span.CompilerTrace),
                static state => SourceCompiler.Analyze(state.source, state.file, state.trace));
    }

    internal static Module Compile(string? source, string file, HirModule? hir, int optimization, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("compile");
        span?.SetAttribute("source.path", file);
        var input = (source, file, hir, optimization, trace: span?.CompilerTrace);
        return span is null ? CompileInput(input) : span.Measure(input, CompileInput);

        static Module CompileInput((string? source, string file, HirModule? hir, int optimization, CompilationTrace? trace) state) =>
            state.hir is { } typed ? SourceCompiler.LowerInstructions(typed, state.optimization, state.trace)
                : SourceCompiler.CompileInstructions(state.source!, state.file, state.optimization, state.trace);
    }

    internal static void WriteHir(HirModule module, string output, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.write");
        span?.SetAttribute("artifact.kind", "hir");
        try { HirCommands.Write(module, output); span?.Complete(); }
        catch (Exception error) { span?.Fail(error); throw; }
    }

    internal static void WriteBytecode(Module module, string output, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.write");
        span?.SetAttribute("artifact.kind", module.Stage == ArtifactStage.Relocatable ? "object" : "executable");
        try { Write(module, output); span?.Complete(); }
        catch (Exception error) { span?.Fail(error); throw; }
    }

    internal static Module Prepare(Module module, string? sourceFile, ExecutionOptions options,
        TelemetrySession? telemetry, CancellationToken cancellationToken)
    {
        BindingContext context = new(Environment.CurrentDirectory, HostContext.HomeDirectory, sourceFile);
        Module bound;
        using (TelemetrySpan? bind = telemetry?.Begin("bind"))
            bound = bind is null ? Binding.BindInstructions(module, context)
                : bind.Measure((module, context), static state => Binding.BindInstructions(state.module, state.context));
        using TelemetrySpan? prepare = telemetry?.Begin("prepare");
        return prepare is null ? ContextualBackend.PrepareInstructions(bound, options, cancellationToken)
            : prepare.Measure((bound, options, cancellationToken),
                static state => ContextualBackend.PrepareInstructions(state.bound, state.options, state.cancellationToken));
    }

    internal static async Task<int> Execute(Module module, CommandOptions options, string? sourceFile,
        TelemetrySession? telemetry, string color, SourceSnapshot? snapshot, TextReader? input, CancellationToken cancellationToken)
    {
        if (telemetry is null)
        {
            Interpreter interpreter = ExecutionHost.Create(module, RuntimeOptions(options, sourceFile, null, color, snapshot, input), cancellationToken);
            await interpreter.ExecuteAsync(cancellationToken);
            return interpreter.FailedTransfers == 0 ? 0 : 1;
        }

        using TelemetrySpan runtime = telemetry.Begin("runtime")!;
        RuntimeProfile? profile = telemetry.CreateProfile();
        ExecutionOptions execution = RuntimeOptions(options, sourceFile, runtime.RuntimeObserver, color, snapshot, input)
            with { Profile = profile };
        string profileOwner = runtime.Id;
        try
        {
            Interpreter interpreter;
            using (TelemetrySpan initialize = runtime.Begin("runtime.initialize")!)
                interpreter = initialize.Measure((module, execution, trace: initialize.CompilerTrace, cancellationToken),
                    static state => ExecutionHost.Create(state.module, state.execution, state.trace, state.cancellationToken));
            using TelemetrySpan execute = runtime.Begin("execute")!;
            profileOwner = execute.Id;
            ProcessCounters? before = execute.MeasureProcess();
            try { await interpreter.ExecuteAsync(cancellationToken); }
            catch (Exception error) { execute.Fail(error); throw; }
            finally
            {
                execute.RecordProcess(before);
                execute.RecordScheduling(interpreter.SchedulingStatistics);
            }
            bool success = interpreter.FailedTransfers == 0;
            execute.Complete(success);
            runtime.Complete(success);
            return success ? 0 : 1;
        }
        catch (Exception error) { runtime.Fail(error); throw; }
        finally { telemetry.PublishProfile(profile, profileOwner); }
    }

    internal static ExecutionOptions RuntimeOptions(CommandOptions options, string? sourceFile, Action<RuntimeObservation>? observe,
        string color, SourceSnapshot? snapshot = null, TextReader? input = null) =>
        new(SourceFile: sourceFile, DryRun: options.DryRun, Jobs: options.Jobs, BufferSize: options.BufferSize,
            MemoryLimit: options.MemoryLimit, SpoolLimit: options.SpoolLimit, Observe: observe)
        {
            Input = input,
            ReportError = error => TerminalPresentation.Error(error, color, snapshot,
                "Check the source, destination and permissions, then retry. Completed transfers are retained.")
        };
}
