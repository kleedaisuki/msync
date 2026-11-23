using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using Msync.Runtime.Native;
using static Msync.Core.Hpir.HpirOp;
using static Msync.Core.Hpir.HpirWidth;

internal static class HpirTextInputChecks
{
    private static readonly SourceSpan conversion = new("hpir-text.msync", 48, 14);

    internal static void Run()
    {
        HpirProgram program = Build();
        RunWithInput(program, "0x2a", 42);
        RunWithInput(program, null, 0);
        RunWithInput(program, "invalid-number", expected: null);
        Console.WriteLine("HPIR typed line input: deferred output order, EOF, exact parse and released work passed.");
    }

    private static HpirProgram Build()
    {
        var entry = new HpirFunction("input", 48, U64, [],
        [
            new(Call, D: 0, A: 0),
            new(Call, D: 8, A: 1),
            new(AddressOfStatic, D: 16, Immediate: 0),
            new(Call, A: 2),
            new(Call, D: 24, A: 3),
            new(Branch, D: 6, A: 24, B: 9),
            new(Call, D: 32, A: 4),
            new(Call, D: 40, A: 5),
            new(Return, A: 40),
            new(ConstU64, D: 40),
            new(Return, A: 40)
        ],
        [
            new(~0, []), new(~1, [0]), new(~2, [16]),
            new(~3, [8]), new(~4, [0]), new(~5, [32])
        ], [new(6, conversion)])
        {
            Safepoints = [new(0, []), new(1, [0]), new(4, [0, 8]), new(6, [0]), new(7, [32])]
        };
        var program = new HpirProgram([entry], 0)
        {
            TextLiterals = ["before input demand"],
            TextLayout = 0,
            Layouts = [new(2, 2, []), HpirTextInputImports.Completion(), HpirTextInputImports.NullProjection(),
                HpirTextInputImports.NumberProjection()],
            Imports = [HpirTextInputImports.ReadLine(1), HpirTextInputImports.IsNull(2),
                HpirScalarImports.OutputCall(HpirTextOutput.PrintLine), HpirFutureImports.Demand(U8),
                HpirTextInputImports.ParseNumber(3), HpirFutureImports.Demand(U64)]
        };
        using var image = new MemoryStream();
        HpirArtifact.WriteProduced(program, image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }

    private static void RunWithInput(HpirProgram program, string? input, double? expected)
    {
        using var reader = new GatedReader();
        using var output = new MarkerWriter();
        var records = new System.Collections.Concurrent.ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions(Output: output)
        {
            Input = reader, Timeline = new(records.Enqueue, "test")
        };
        Task<ulong> run = Task.Run(() => HpirExecutor.Run(program, options));
        try
        {
            Task first = Task.WhenAny(output.Marker.Task, run).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (first == run)
            {
                _ = run.GetAwaiter().GetResult();
                throw new Exception("HPIR entry returned before signalling the gated input output.");
            }
            if (run.IsCompleted || reader.Line.Task.IsCompleted) throw new Exception("HPIR observed pending input before explicit demand.");
        }
        finally { reader.Line.TrySetResult(input); }
        try
        {
            double actual = BitConverter.UInt64BitsToDouble(run.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            if (actual != expected) throw new Exception($"HPIR input expected {expected}, got {actual}.");
        }
        catch (MsyncException failure) when (expected is null && failure.Span == conversion
            && failure.Message.Contains("invalid-number", StringComparison.Ordinal)) { }
        if (reader.Calls != 1 || output.ToString() != "before input demand\n")
            throw new Exception("HPIR re-read the typed text cell or reordered stdout.");
        RuntimeTimelineRecord settle = records.Single(record => record.Name == "hpir.work.settle");
        if (settle.Metrics["work.outstanding"] != 0)
            throw new Exception("The materialized text completion retained its host job.");
    }

    private sealed class GatedReader : TextReader, IAsyncLineInput
    {
        internal readonly TaskCompletionSource<string?> Line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return new(Line.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class MarkerWriter : StringWriter
    {
        internal MarkerWriter() => NewLine = "\n";
        internal readonly TaskCompletionSource Marker = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteLineAsync(buffer, cancellationToken).ConfigureAwait(false);
            Marker.TrySetResult();
        }
    }
}
