namespace Msync.Cli;

internal enum TelemetryLevel { Summary, Phases, Profile, Instructions }

internal static class TelemetryLevels
{
    public static TelemetryLevel? Parse(string name) => name switch
    {
        "summary" => TelemetryLevel.Summary,
        "phases" => TelemetryLevel.Phases,
        "profile" => TelemetryLevel.Profile,
        "instructions" => TelemetryLevel.Instructions,
        _ => null
    };

    public static string Name(this TelemetryLevel level) => level switch
    {
        TelemetryLevel.Summary => "summary",
        TelemetryLevel.Phases => "phases",
        TelemetryLevel.Profile => "profile",
        _ => "instructions"
    };
}
