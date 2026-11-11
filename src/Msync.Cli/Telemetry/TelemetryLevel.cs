namespace Msync.Cli;

internal enum TelemetryLevel { Summary, Phases, Profile, Instructions, Diagnostics }

internal static class TelemetryLevels
{
    public static TelemetryLevel? Parse(string name) => name switch
    {
        "summary" => TelemetryLevel.Summary,
        "phases" => TelemetryLevel.Phases,
        "profile" => TelemetryLevel.Profile,
        "instructions" => TelemetryLevel.Instructions,
        "diagnostics" => TelemetryLevel.Diagnostics,
        _ => null
    };

    public static string Name(this TelemetryLevel level) => level switch
    {
        TelemetryLevel.Summary => "summary",
        TelemetryLevel.Phases => "phases",
        TelemetryLevel.Profile => "profile",
        TelemetryLevel.Instructions => "instructions",
        _ => "diagnostics"
    };
}
