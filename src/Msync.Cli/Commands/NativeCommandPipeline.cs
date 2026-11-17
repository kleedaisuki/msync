using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime;
using Msync.Runtime.Native;
using static Msync.Cli.CommandPipeline;
using SourceCompiler = Msync.Compiler.Compiler;

namespace Msync.Cli;

internal static class NativeCommandPipeline
{
    internal static NativeProgram Read(string file, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.read");
        NativeProgram program = span is null ? ReadFile(file) : span.Measure((file, trace: span.CompilerTrace),
            static state => ReadFile(state.file, state.trace));
        string kind = program.Stage == ArtifactStage.Relocatable ? "object" : "executable";
        span?.SetAttribute("artifact.kind", kind);
        telemetry?.SetAttribute("input.kind", kind);
        return program;
    }

    internal static NativeProgram Read(Stream stream) => Read(stream, null);

    private static NativeProgram Read(Stream stream, CompilationTrace? trace) => NativeArtifact.Read(stream, trace);

    private static NativeProgram ReadFile(string file, CompilationTrace? trace = null)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            return Read(stream, trace);
        }
        catch (Exception error) when (error is IOException or MsyncException)
        {
            throw new MsyncException($"Cannot read artifact '{file}': {error.Message}",
                (error as MsyncException)?.Span, error);
        }
    }

    internal static NativeProgram Compile(string? source, string file, HirModule? hir,
        int optimization, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("compile");
        span?.SetAttribute("source.path", file);
        var state = (source, file, hir, optimization, trace: span?.CompilerTrace);
        return span is null ? Lower(state) : span.Measure(state, Lower);

        static NativeProgram Lower((string? source, string file, HirModule? hir, int optimization, CompilationTrace? trace) state) =>
            state.hir is { } typed ? SourceCompiler.LowerNative(typed, state.optimization, state.trace)
                : SourceCompiler.CompileNative(state.source!, state.file, state.optimization, state.trace);
    }

    internal static void Write(NativeProgram program, string output, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.write");
        span?.SetAttribute("artifact.kind", program.Stage == ArtifactStage.Relocatable ? "object" : "executable");
        try
        {
            if (output == "-")
            {
                using Stream stream = Console.OpenStandardOutput();
                NativeArtifact.WriteProduced(program, stream, span?.CompilerTrace);
            }
            else NativeArtifact.WriteProduced(program, output, span?.CompilerTrace);
            span?.Complete();
        }
        catch (Exception error) { span?.Fail(error); throw; }
    }

    internal static void Print(NativeProgram program, string color) =>
        Console.Write(TerminalPresentation.Syntax(NativeArtifact.Disassemble(program), TerminalPresentation.Colors(color)));

    internal static NativeProgram Bind(NativeProgram program, string? sourceFile, TelemetrySession? telemetry,
        ExecutionOptions? options = null, CancellationToken token = default)
    {
        if (program.Stage == ArtifactStage.Executable) return program;
        options ??= new();
        BindingContext context;
        using (TelemetrySpan? bind = telemetry?.Begin("bind"))
            context = bind is null ? CaptureContext(options, sourceFile)
                : bind.Measure((options, sourceFile), static state => CaptureContext(state.options, state.sourceFile));
        using TelemetrySpan? prepare = telemetry?.Begin("prepare");
        RuntimeTimeline? timeline = telemetry?.Timeline;
        using IDisposable? owner = prepare is null ? null : timeline?.Attach(prepare.Id);
        if (timeline is not null) options = options with { Timeline = timeline };
        return prepare is null ? NativePreparation.Prepare(program, context, options, token)
            : prepare.Measure((program, context, options, token, trace: prepare.CompilerTrace), static state => NativePreparation.Prepare(state.program, state.context, state.options, state.token, state.trace));
    }

    private static BindingContext CaptureContext(ExecutionOptions options, string? sourceFile) =>
        new(Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory), HostContext.HomeDirectory, sourceFile);

    internal static async Task<int> Run(NativeProgram program, CommandOptions options, string file, bool binary,
        TelemetrySession? telemetry, string color, SourceSnapshot? snapshot, TextReader? input)
    {
        telemetry?.SetAttribute("optimization.level", program.OptimizationLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.Command == "check")
        {
            Console.WriteLine($"{file}: valid");
            return 0;
        }
        if (options.Command is "compile" or "emit-ir")
        {
            if (options.Output is { } output) Write(program, output, telemetry);
            else Print(program, color);
            return 0;
        }
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            string? sourceFile = options.SourceFile is { } requested ? Path.GetFullPath(requested)
                : binary || file == "-" ? null : Path.GetFullPath(file);
            program = Bind(program, sourceFile, telemetry,
                RuntimeOptions(options, sourceFile, null, color, snapshot, input), cancellation.Token);
            if (options.Command == "bind-ir")
            {
                Write(program, options.Output ?? Path.ChangeExtension(file, ".mexe"), telemetry);
                return 0;
            }
            if (options.Command is "plan" or "plan-ir") { Print(program, color); return 0; }
            return await Execute(program, options, sourceFile, telemetry, color, snapshot, input, cancellation.Token);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task<int> Execute(NativeProgram program, CommandOptions options, string? sourceFile,
        TelemetrySession? telemetry, string color, SourceSnapshot? snapshot, TextReader? input, CancellationToken token)
    {
        using TelemetrySpan? runtime = telemetry?.Begin("runtime");
        RuntimeProfile? profile = telemetry?.CreateProfile();
        ExecutionOptions execution = RuntimeOptions(options, sourceFile, runtime?.RuntimeObserver, color, snapshot, input)
            with
        { Profile = profile, Timeline = telemetry?.Timeline, ScopedGuestValues = true };
        string profileOwner = runtime?.Id ?? "";
        try
        {
            NativeInterpreter interpreter;
            using (TelemetrySpan? initialize = runtime?.Begin("runtime.initialize"))
            using (initialize is null ? null : telemetry?.Timeline?.Attach(initialize.Id))
            {
                interpreter = initialize is null ? new(program, execution)
                    : initialize.Measure((program, execution), static state => new NativeInterpreter(state.program, state.execution));
            }
            await using (interpreter)
            {
                using TelemetrySpan? execute = runtime?.Begin("execute");
                using IDisposable? owner = execute is null ? null : telemetry?.Timeline?.Attach(execute.Id);
                execute?.SetAttribute("vm.driver", "inline");
                profileOwner = execute?.Id ?? profileOwner;
                ProcessCounters? before = execute?.MeasureProcess();
                try { interpreter.ExecuteEntry(token); }
                catch (Exception error) { execute?.Fail(error); throw; }
                finally
                {
                    execute?.RecordProcess(before);
                    execute?.RecordScheduling(interpreter.SchedulingStatistics);
                    execute?.RecordTiering(interpreter.TierStatistics);
                }
                bool success = interpreter.FailedTransfers == 0;
                execute?.Complete(success);
                runtime?.Complete(success);
                return success ? 0 : 1;
            }
        }
        catch (Exception error) { runtime?.Fail(error); throw; }
        finally { telemetry?.PublishProfile(profile, profileOwner); }
    }
}
