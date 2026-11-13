using Msync.Core;
using Msync.Hir;
using Msync.Runtime;
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
            : span.Measure((file, trace: span.CompilerTrace), static state => HirArtifact.Read(state.file, state.trace));
    }

    internal static HirModule Analyze(string source, string file, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("compile");
        span?.SetAttribute("source.path", file);
        return span is null ? SourceCompiler.Analyze(source, file)
            : span.Measure((source, file, trace: span.CompilerTrace),
                static state => SourceCompiler.Analyze(state.source, state.file, state.trace));
    }

    internal static void WriteHir(HirModule module, string output, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.write");
        span?.SetAttribute("artifact.kind", "hir");
        try { HirCommands.Write(module, output, span?.CompilerTrace); span?.Complete(); }
        catch (Exception error) { span?.Fail(error); throw; }
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
