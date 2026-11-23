using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirTextDeliveryChecks
{
    private const string Name = "typed-text-delivery.msync";
    private const string Handoff = """
        fn defer() { scope { line?: $std::io::read_line() } };
        let packet = defer();
        $std::io::println("returned");
        $std::io::flush();
        $std::io::println($std::text::format_fields("{line}", packet));
        """;

    private const string Format = """
        let template = $std::io::read_text(./template.txt);
        $std::text::format(template, 7, "α") -> ./answer.txt;
        """;

    internal static void Run()
    {
        string root = Directory.CreateDirectory(Path.Combine(".temp", "hpir", $"text-delivery-{Guid.NewGuid():N}")).FullName;
        try
        {
            for (int level = 0; level <= 3; ++level)
            {
                HpirProgram program = Compile(Handoff, root, level);
                GateHandoff(program, native: false);
                if (HpirMachineImage.IsSupported) GateHandoff(program, native: true);
            }
            var format = Compile(Format, root, 2);
            CheckFormat(format, root, native: false);
            if (HpirMachineImage.IsSupported) CheckFormat(format, root, native: true);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR Text: pending Scope handoff, actual flush, typed dynamic formats and failure lineage");
    }

    private static void GateHandoff(HpirProgram program, bool native)
    {
        using var gate = new GatedReader();
        using var output = new ObservedWriter();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task run = Task.Run(() => Execute(program, new(Output: output) { Input = gate }, native, cancellation.Token));
        try
        {
            Task ready = Task.WhenAny(output.Flushed.Task, run).WaitAsync(cancellation.Token).GetAwaiter().GetResult();
            if (ready == run) run.GetAwaiter().GetResult();
            Require(ready == output.Flushed.Task && output.ToString() == "returned\n",
                "A Text result was observed before the record returned or the explicit flush was lost.");
            gate.Reply.TrySetResult("小可莉🙂");
            run.WaitAsync(cancellation.Token).GetAwaiter().GetResult();
            Require(output.ToString() == "returned\n小可莉🙂\n", "The selected Text field was lost across frame exit.");
        }
        finally
        {
            cancellation.Cancel();
            if (!run.IsCompleted)
                try { run.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }
    }

    private static void CheckFormat(HpirProgram program, string root, bool native)
    {
        string template = Path.Combine(root, "template.txt"), answer = Path.Combine(root, "answer.txt");
        File.WriteAllText(template, "{1}: {0:02d}");
        Execute(program, new(), native, default);
        Require(File.ReadAllText(answer) == "α: 07", "The dynamic template or physical operand type changed.");
        File.Delete(answer);
        File.WriteAllText(template, "😀{:z}");
        try
        {
            Execute(program, new(), native, default);
            throw new Exception("The malformed dynamic template was accepted.");
        }
        catch (MsyncException error)
        {
            Require(error.Span is { Source: Name } && error.Message.Contains("unknown format specifier", StringComparison.Ordinal)
                && error.Message.Contains("template character 2", StringComparison.Ordinal)
                && !File.Exists(answer), "A malformed template lost its source/character lineage or published output.");
        }
    }

    private sealed class GatedReader : TextReader
    {
        internal TaskCompletionSource<string?> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            new(Reply.Task.WaitAsync(cancellationToken));
    }

    private sealed class ObservedWriter : StringWriter
    {
        internal ObservedWriter() => NewLine = "\n";
        internal TaskCompletionSource Flushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flushed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static void Execute(HpirProgram program, ExecutionOptions options, bool native, CancellationToken token)
    {
        using var code = native ? HpirMachineImage.Compile(program) : null;
        if (code is null) HpirExecutor.Run(program, options, token);
        else HpirExecutor.RunMachineCandidate(code, options, token);
    }

    private static HpirProgram Compile(string source, string root, int level)
    {
        var portable = RoundTrip(Compiler.LowerHpir(Compiler.Analyze(source, Name), level));
        return RoundTrip(HpirBinding.Prepare(portable, new(root, root)));
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
