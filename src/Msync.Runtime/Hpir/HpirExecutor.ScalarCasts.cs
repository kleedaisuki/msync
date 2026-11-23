using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private const string BoolCastHelp = "Use Number or the text 'true' or 'false'; strings are not tested for nonemptiness.";
    private const string NumberCastHelp = "Use invariant numeric text, such as '12.5' or '1e3'.";

    private static void ParseScalarCast(HpirService service, nint source, byte* frame, int destination,
        HpirDiagnostics diagnostics, int function, int pc)
    {
        using RuntimeTimelineScope? trace = diagnostics.BeginScalarParse(service, function, pc);
        try { ParseScalarCast(service, source, frame, destination, diagnostics, function, pc, trace); }
        catch (Exception error) when (trace is not null) { trace.Fail(error); throw; }
    }

    private static void ParseScalarCast(HpirService service, nint source, byte* frame, int destination,
        HpirDiagnostics diagnostics, int function, int pc, RuntimeTimelineScope? trace)
    {
        if (service == HpirService.ParseBool)
        {
            if (source == 0)
                throw new MsyncException("Cannot cast Null 'null' to Bool. Handle Null first or admit Null in the target type.",
                    diagnostics.Source(function, pc));
            ReadOnlySpan<char> text = HpirTextStorage.Read(source);
            trace?.SetMetric("parse.utf16_units", text.Length);
            if (bool.TryParse(text, out bool result))
            {
                WriteBool(frame, destination, result);
                return;
            }
            throw ScalarCastFailure("String", text.ToString(), "Bool", BoolCastHelp, diagnostics, function, pc);
        }

        HpirPathValue path = Unsafe.ReadUnaligned<HpirPathValue>((void*)source);
        ReadOnlySpan<char> contents = HpirTextStorage.Read(path.NativeText);
        trace?.SetMetric("parse.utf16_units", contents.Length);
        if (service == HpirService.ParsePathF64)
        {
            NumberTextPath parse = ScalarNumberText.Parse(contents, out double result);
            trace?.SetAttribute("parse.path", parse == NumberTextPath.ShortInteger ? "short-integer" : "general");
            if (parse != NumberTextPath.Invalid)
            {
                Write(frame, destination, result);
                return;
            }
            throw ScalarCastFailure("Path", PathDisplay(path, contents), "Number", NumberCastHelp, diagnostics, function, pc);
        }
        if (bool.TryParse(contents, out bool boolean))
        {
            WriteBool(frame, destination, boolean);
            return;
        }
        throw ScalarCastFailure("Path", PathDisplay(path, contents), "Bool", BoolCastHelp, diagnostics, function, pc);
    }

    private static string PathDisplay(HpirPathValue value, ReadOnlySpan<char> native) =>
        value.Local ? native.ToString() : value.ToHost().ToString();

    private static MsyncException ScalarCastFailure(string source, string value, string target, string help,
        HpirDiagnostics diagnostics, int function, int pc) =>
        new($"Cannot cast {source} '{value}' to {target}. {help}", diagnostics.Source(function, pc));
}
