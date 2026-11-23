using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using Msync.Runtime.Native;

internal static class HpirTextObservationChecks
{
    private const string Observed = """
        fn read_number() -> Number {
            let line? = $std::io::read_line();
            let view? = line;
            $std::io::println("queued");
            if view is null { return 0; };
            view as Number
        };
        let seed = read_number();
        let result = seed * 3 + 1;
        $std::io::println("done");
        result
        """;

    private const string Control = """
        fn optional_read(use: Bool) -> Number {
            let line? = $std::io::read_line();
            if use {
                if line is null { return 0; };
                return line as Number;
            };
            5
        };
        optional_read(false)
        """;

    private const string AfterLoop = """
        let count = 0;
        for item in $std::ranges::iota(0, 3) {
            count = count + item;
        };
        let input? = $std::io::read_line();
        let number = if input is null { 1 } else { input as Number };
        (number + count) * 2
        """;

    private const string InsideLoop = """
        let sum = 0;
        for ordinal in $std::ranges::iota(1, 3) {
            let input? = $std::io::read_line();
            let number = if input is null { 0 } else { input as Number };
            sum = sum + ordinal * number;
        };
        sum
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; level++)
        {
            List<CompilationObservation> observations = [];
            var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
            HpirProgram program = Build(Observed, "observed-text.msync", level, trace);
            Require(program.Imports.Any(import => import.Service == HpirService.ParseF64)
                && program.Imports.All(import => import.Service is not (HpirService.TextParseNumber or HpirService.FutureLift))
                && observations.Any(item => item.Metrics?.GetValueOrDefault("flow.observed_text_conversions") == 1),
                $"Dominated, same-producer conversion at O{level} should cut the deferred result flow.");
            Check(program, machine: false);
            if (HpirMachineImage.IsSupported) Check(program, machine: true);
            CheckNonentry(AfterLoop, "observed-after-loop.msync", level, "4\n", 14, "", 8);
            CheckNonentry(InsideLoop, "observed-inside-loop.msync", level, "4\n7\n", 18, "7\n4\n", 15);

            HpirProgram control = Build(Control, "conditional-text.msync", level);
            Require(control.Imports.Any(import => import.Service == HpirService.TextParseNumber),
                "A conditional observation outside the producer's entry path was generalized without evidence.");
            using var input = new Gate();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            Require(BitConverter.UInt64BitsToDouble(HpirExecutor.Run(control, new() { Input = input }, stop.Token)) == 5
                && !input.Line.Task.IsCompleted, "A skipped observation must not consume the line.");
        }
        Console.WriteLine("PASS observed Text→ready Number in entry and loops; skipped demand and bad input retain behavior at O0–O3");
    }

    private static void CheckNonentry(string source, string name, int level, string input, double expected,
        string machineInput, double machineExpected)
    {
        List<CompilationObservation> observations = [];
        HpirProgram program = Build(source, name, level,
            new(observations.Add, includeOptimizerPasses: true));
        Require(program.Imports.Any(import => import.Service == HpirService.ParseF64)
            && program.Imports.All(import => import.Service is not (HpirService.TextParseNumber or HpirService.FutureLift))
            && observations.Any(item => item.Metrics?.GetValueOrDefault("flow.observed_text_nonentry") == 1),
            "A dominating observation outside entry was ignored.");
        using var reader = new StringReader(input);
        Require(BitConverter.UInt64BitsToDouble(HpirExecutor.Run(program, new() { Input = reader })) == expected,
            "The parse crossed a loop or consumed the wrong input.");
        if (!HpirMachineImage.IsSupported) return;
        using var image = HpirMachineImage.Compile(program);
        using var nativeInput = new StringReader(machineInput);
        Require(BitConverter.UInt64BitsToDouble(HpirExecutor.RunMachineCandidate(image, new() { Input = nativeInput })) == machineExpected,
            "A native continuation reused an earlier iteration's input or parsed EOF.");
    }

    private static HpirProgram Build(string source, string name, int level, CompilationTrace? trace = null)
    {
        HpirProgram produced = Compiler.LowerHpir(Compiler.Analyze(source, name, trace), level, trace);
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(produced, stream);
        stream.Position = 0;
        return HpirBinding.Prepare(HpirArtifact.ReadTrusted(stream), new(Environment.CurrentDirectory, Environment.CurrentDirectory));
    }

    private static void Check(HpirProgram program, bool machine)
    {
        using HpirMachineImage? image = machine ? HpirMachineImage.Compile(program) : null;
        CheckInput("7\n", 22);
        CheckInput("", 1);
        CheckInput("not-number\n", null);
        return;

        void CheckInput(string input, double? expected)
        {
            using var reader = new StringReader(input);
            using var output = new StringWriter { NewLine = "\n" };
            var options = new ExecutionOptions(Output: output) { Input = reader };
            try
            {
                ulong result = image is null ? HpirExecutor.Run(program, options) : HpirExecutor.RunMachineCandidate(image, options);
                Require(expected == BitConverter.UInt64BitsToDouble(result) && output.ToString() == "queued\ndone\n",
                    "Observed text changed a valid answer or output order.");
            }
            catch (MsyncException error) when (expected is null)
            {
                Require(error.Message.Contains("Cannot cast String 'not-number' to Number", StringComparison.Ordinal)
                    && error.Span?.Start == Observed.IndexOf("view as Number", StringComparison.Ordinal)
                    && output.ToString() == "queued\n", "A failed ready conversion lost its source or crossed a later effect.");
            }
        }
    }

    private sealed class Gate : TextReader, IAsyncLineInput
    {
        internal TaskCompletionSource<string?> Line { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken token) => new(Line.Task.WaitAsync(token));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
