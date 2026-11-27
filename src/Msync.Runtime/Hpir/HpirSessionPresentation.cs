using System.Globalization;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static class HpirSessionPresentation
{
    internal static void Write(TextWriter writer, HpirSessionValue value) => Write(writer, Format(value));

    internal static void Write(TextWriter writer, ReadOnlySpan<char> value)
    {
        if (value.EndsWith("\n", StringComparison.Ordinal)) writer.Write(value);
        else writer.WriteLine(value);
    }

    private static string Format(HpirSessionValue value) => value.Kind switch
    {
        TypeKind.Null => "null",
        TypeKind.Bool => value.Boolean ? "true" : "false",
        TypeKind.Number => value.Number.ToString(CultureInfo.InvariantCulture),
        TypeKind.String => value.Text!,
        TypeKind.Path => value.Path!.Value.ToString(),
        TypeKind.Connection => value.Connection!.Value.ToString(),
        TypeKind.Type => value.Type!.ToString(),
        TypeKind.Function => value.Text ?? $"fn #{value.Bits - 1}",
        TypeKind.Range => value.Type?.ToString() ?? "Range",
        TypeKind.Process => FormatProcess(value.Process!.Value),
        TypeKind.Scope when value.Scope!.Cycle => "object { ...cycle... }",
        TypeKind.Scope => "object { " + string.Join(", ", value.Scope!.Members.Select(member =>
            member.Name + ": " + (member.Value.Kind == TypeKind.String
                ? "\"" + member.Value.Text!.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : Format(member.Value)))) + " }",
        _ => throw new InvalidOperationException($"Unexpected HPIR presentation type {value.Kind}.")
    };

    private static string FormatProcess(HpirSessionProcess process)
    {
        if (process.State == HpirSessionProcessState.Exited) return $"Process(exit={process.ExitCode})";
        string state = process.State switch
        {
            HpirSessionProcessState.Cancelled => "cancelled",
            HpirSessionProcessState.Failed => "failed",
            _ => "pending"
        };
        return process.ProcessId is { } pid ? $"Process({state}, pid={pid})" : $"Process({state})";
    }
}
