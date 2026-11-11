using System.Diagnostics;
using Msync.Compiler.Syntax;

namespace Msync.Cli;

internal static class LanguageServerHost
{
    internal static async Task<int> RunAsync(string[] arguments, long entered)
    {
        if (!arguments.Contains("--telemetry-db", StringComparer.Ordinal))
            return await new LanguageServer(static source => Formatter.Format(source)).RunAsync();

        var options = CommandOptions.Parse(arguments.Where(argument => argument != "--language-server").ToArray());
        long configurationStarted = Stopwatch.GetTimestamp();
        // Editor recording is an explicit host policy, not a workspace CLI setting.
        var configuration = Configuration.Load(null, disabled: true, options.Overrides);
        long exporterStarted = Stopwatch.GetTimestamp();
        using var session = TelemetrySession.TryOpen(configuration, "language-server", sqlitePath: options.TelemetryDatabase,
            tracePath: options.TracePath, disabled: options.NoTelemetry, warnings: Console.Error);
        session?.RecordStartup(entered, configurationStarted, exporterStarted);
        session?.SetAttribute("input.kind", "lsp");
        try
        {
            int result = await new LanguageServer(static source => Formatter.Format(source), session is null ? null : new(session)).RunAsync();
            session?.Finish(result == 0 ? "ok" : "error");
            return result;
        }
        catch (Exception error)
        {
            session?.Finish(error is OperationCanceledException ? "cancelled" : "error", error);
            throw;
        }
    }
}
