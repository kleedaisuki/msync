using System.Globalization;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallMath(HpirImport import, byte* frame, int destination, IReadOnlyList<int> arguments,
        int function, int pc, HpirDiagnostics diagnostics)
    {
        var operation = (HpirMathOp)import.Binding;
        double first = Read<double>(frame, arguments[0]);
        if (import.Result == HpirWidth.U8)
        {
            WriteBool(frame, destination, ScalarMath.Predicate(operation, first));
            return;
        }
        double second = arguments.Count > 1 ? Read<double>(frame, arguments[1]) : 0;
        double third = arguments.Count > 2 ? Read<double>(frame, arguments[2]) : 0;
        try { Write(frame, destination, ScalarMath.Evaluate(operation, first, second, third)); }
        catch (MsyncException error) when (error.Span is null)
        {
            throw new MsyncException(error.Message, diagnostics.Source(function, pc), error);
        }
    }

    private static nint FormatNumber(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active, int pc,
        int layout, double number, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts)
    {
        Span<char> buffer = stackalloc char[64];
        if (!number.TryFormat(buffer, out int length, provider: CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Invariant Number format exceeded the HPIR buffer");
        int elements = HpirTextStorage.Elements(length);
        nint text = Allocate(program, ref heap, active, pc, layout, elements, diagnostics, counts, clear: false);
        HpirTextStorage.Write(text, buffer[..length]);
        return text;
    }

    internal static double ParseNumber(nint address, HpirDiagnostics diagnostics, int function, int pc,
        RuntimeProfile.HpirFunctionCensus? counts)
    {
        if (address == 0) throw new MsyncException("Cannot cast Null to Number. Handle Null first.", diagnostics.Source(function, pc));
        ReadOnlySpan<char> text = HpirTextStorage.Read(address);
        NumberTextPath path = ScalarNumberText.Parse(text, out double result);
        counts?.NumberTextParse(path);
        if (path != NumberTextPath.Invalid) return result;
        throw ScalarCastFailure("String", text.ToString(), "Number", NumberCastHelp, diagnostics, function, pc);
    }

    private static void WriteText(HpirTextOutput output, nint address, int function, int pc, HpirDiagnostics diagnostics,
        ExecutionOptions? options, CancellationToken token)
    {
        bool flush = HpirScalarImports.FlushOnly(output);
        ReadOnlySpan<char> contents = flush ? default : HpirTextStorage.Read(address);
        using RuntimeTimelineScope? trace = diagnostics.BeginTextWrite(function, pc, output, contents.Length);
        bool error = output is HpirTextOutput.Error or HpirTextOutput.ErrorLine or HpirTextOutput.FlushError;
        bool newline = output is HpirTextOutput.PrintLine or HpirTextOutput.ErrorLine;
        TextWriter writer = error ? options?.Error ?? Console.Error : options?.Output ?? Console.Out;
        try
        {
            Task write;
            if (flush) write = writer.FlushAsync(token);
            else
            {
                // An asynchronous writer may retain Memory after invocation.
                string text = contents.ToString();
                write = newline ? writer.WriteLineAsync(text.AsMemory(), token) : writer.WriteAsync(text.AsMemory(), token);
            }
            if (trace is not null) trace.SetMetric("await.suspended", write.IsCompleted ? 0 : 1);
            if (!write.IsCompletedSuccessfully) write.GetAwaiter().GetResult();
        }
        catch (Exception failure)
        {
            trace?.Fail(failure);
            if (failure is OperationCanceledException) throw;
            throw new MsyncException($"{HpirScalarImports.TextOutputName(output)}: {failure.Message}",
                diagnostics.Source(function, pc), failure);
        }
    }
}
