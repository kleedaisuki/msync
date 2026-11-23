using System.Globalization;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Hir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using static Msync.Cli.CommandPipeline;
using SourceCompiler = Msync.Compiler.Compiler;

namespace Msync.Cli;

internal static class HpirCommandPipeline
{
    internal static HpirProgram Compile(string? source, string file, HirModule? hir,
        int optimization, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("compile");
        span?.SetAttribute("source.path", file);
        var state = (source, file, hir, optimization, trace: span?.CompilerTrace);
        return span is null ? Lower(state) : span.Measure(state, Lower);

        static HpirProgram Lower((string? source, string file, HirModule? hir, int optimization, CompilationTrace? trace) state) =>
            SourceCompiler.LowerHpir(state.hir ?? SourceCompiler.Analyze(state.source!, state.file, state.trace),
                state.optimization, state.trace);
    }

    internal static int Run(HpirProgram program, CompileOutput? export, CommandOptions options, string file,
        TelemetrySession? telemetry, string color, TextReader? input, bool binary = true, SourceSnapshot? snapshot = null)
    {
        telemetry?.SetAttribute("artifact.representation", "hpir");
        if (program.OptimizationLevel >= 0)
            telemetry?.SetAttribute("optimization.level", program.OptimizationLevel.ToString(CultureInfo.InvariantCulture));
        if (options.Command == "check")
        {
            Console.WriteLine($"{file}: valid");
            return 0;
        }
        if (options.Command == "emit-ir")
        {
            if (options.Output is { } output) Write(program, output, telemetry);
            else Print(program, color);
            return 0;
        }
        if (export.HasValue && program.Stage == HpirStage.Executable)
            throw new ArgumentException("Cannot compile an already prepared program. Run it directly, or compile the original source or portable artifact.");

        string? source = BindingSource(options, file, binary);
        using var cancellation = new ConsoleCancellation();
        if (!export.HasValue || export.Value.Emission == CompileEmission.Prepared)
        {
            program = Prepare(program, source, RuntimeOptions(options, source, null, color, snapshot, input),
                telemetry, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
        }

        if (export is { } selected)
        {
            Write(program, selected.Path, telemetry);
            selected.Report();
            return 0;
        }
        if (options.Command == "bind-ir")
        {
            Write(program, options.Output ?? Path.ChangeExtension(file, ".mexe"), telemetry);
            return 0;
        }
        if (options.Command is "plan" or "plan-ir") { Print(program, color); return 0; }
        return Execute(program, options, source, telemetry, color, input, snapshot, cancellation.Token);
    }

    private static HpirProgram Prepare(HpirProgram program, string? source, ExecutionOptions options,
        TelemetrySession? telemetry, CancellationToken token)
    {
        if (program.Stage == HpirStage.Executable) return program;
        BindingContext context;
        using (TelemetrySpan? capture = telemetry?.Begin("bind"))
            context = capture is null ? CaptureContext(options, source)
                : capture.Measure((options, source), static state => CaptureContext(state.options, state.source));
        using TelemetrySpan? prepare = telemetry?.Begin("prepare");
        RuntimeTimeline? timeline = telemetry?.Timeline;
        using IDisposable? owner = prepare is null ? null : timeline?.Attach(prepare.Id);
        if (timeline is not null) options = options with { Timeline = timeline };
        return prepare is null ? HpirBinding.Prepare(program, context, options, token)
            : prepare.Measure((program, context, options, token, trace: prepare.CompilerTrace),
                static state => HpirBinding.Prepare(state.program, state.context, state.options, state.token, state.trace));
    }

    private static int Execute(HpirProgram program, CommandOptions options, string? source,
        TelemetrySession? telemetry, string color, TextReader? input, SourceSnapshot? snapshot, CancellationToken token)
    {
        using TelemetrySpan? runtime = telemetry?.Begin("runtime");
        using TelemetrySpan? execute = runtime?.Begin("execute");
        using IDisposable? owner = execute is null ? null : telemetry?.Timeline?.Attach(execute.Id);
        RuntimeProfile? profile = telemetry?.CreateProfile();
        ExecutionOptions execution = RuntimeOptions(options, source, runtime?.RuntimeObserver, color, snapshot, input)
            with { Profile = profile, Timeline = telemetry?.Timeline };
        ProcessCounters? before = execute?.MeasureProcess();
        try
        {
            execute?.SetAttribute("vm.driver", "hpir");
            HpirRunOutcome result = HpirExecutor.RunConfigured(program, execution, token);
            execute?.SetMetric("transfers.failed", result.FailedTransfers);
            bool success = result.FailedTransfers == 0;
            execute?.Complete(success);
            runtime?.Complete(success);
            return success ? 0 : 1;
        }
        catch (Exception error)
        {
            execute?.Fail(error);
            runtime?.Fail(error);
            throw;
        }
        finally
        {
            execute?.RecordProcess(before);
            telemetry?.PublishProfile(profile, execute?.Id ?? runtime?.Id ?? "");
        }
    }

    private static void Write(HpirProgram program, string output, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.write");
        span?.SetAttribute("artifact.kind", program.Stage == HpirStage.Executable ? "executable" : "object");
        span?.SetAttribute("artifact.representation", "hpir");
        try
        {
            if (output == "-")
            {
                using Stream stream = Console.OpenStandardOutput();
                HpirArtifact.WriteProduced(program, stream, span?.CompilerTrace);
            }
            else HpirArtifact.WriteProduced(program, output, span?.CompilerTrace);
            span?.Complete();
        }
        catch (Exception error) { span?.Fail(error); throw; }
    }

    private static void Print(HpirProgram program, string color)
    {
        if (!TerminalPresentation.Colors(color)) { HpirDisassembly.Write(program, Console.Out); return; }
        using var listing = new StringWriter(CultureInfo.InvariantCulture);
        HpirDisassembly.Write(program, listing);
        Console.Write(TerminalPresentation.Syntax(listing.ToString(), true));
    }
}
