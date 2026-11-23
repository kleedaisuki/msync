using System.Text.RegularExpressions;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallRegex(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        IReadOnlyList<int> arguments, byte* frame, int destination)
    {
        var (operation, literal) = HpirRegexImports.Decode(import.Binding);
        nint subject = operation == HpirRegexOp.MatchPath ? PathOperand(frame, arguments[0], false, default).NativeText
            : Read<nint>(frame, arguments[0]);
        if (subject == 0 && operation == HpirRegexOp.MatchString)
        {
            WriteBool(frame, destination, false);
            return;
        }
        ReadOnlySpan<char> text = HpirTextStorage.Read(subject);
        string? pattern = operation == HpirRegexOp.Escape ? null : literal >= 0 ? program.TextLiterals[literal]
            : CopyText(Read<nint>(frame, arguments[1]));
        using RuntimeTimelineScope? trace = diagnostics.BeginRegex(active->Function, pc, operation, literal >= 0, text.Length, pattern?.Length);
        try
        {
            if (!HpirRegexImports.ProducesText(operation))
            {
                bool matched = Regex.IsMatch(text, pattern!, RegexOptions.CultureInvariant);
                trace?.SetMetric("regex.matched", matched ? 1 : 0);
                WriteBool(frame, destination, matched);
                return;
            }

            string result = operation == HpirRegexOp.Escape ? Regex.Escape(text.ToString())
                : Regex.Replace(text.ToString(), pattern!, CopyText(Read<nint>(frame, arguments[literal >= 0 ? 1 : 2])), RegexOptions.CultureInvariant);
            var allocation = new TextAllocation(program, ref heap, active, diagnostics, counts, pc);
            bool reused = text.SequenceEqual(result);
            trace?.SetMetric("text.reused", reused ? 1 : 0);
            trace?.SetMetric("text.output_code_units", result.Length);
            if (reused)
            {
                Write(frame, destination, allocation.Reuse(subject));
                return;
            }
            nint output = allocation.Reserve(result.Length, out Span<char> buffer);
            result.AsSpan().CopyTo(buffer);
            Write(frame, destination, output);
        }
        catch (ArgumentException error)
        {
            trace?.Fail(error);
            string message = operation is HpirRegexOp.MatchString or HpirRegexOp.MatchPath ? error.Message
                : $"{HpirRegexImports.Name(operation)}: {error.Message}";
            throw new MsyncException(message, diagnostics.Source(active->Function, pc), error);
        }
    }
}
