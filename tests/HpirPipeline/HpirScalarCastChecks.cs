using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirScalarCastChecks
{
    private const string Source = """
        fn truth(n: Number) -> Bool { n as Bool };
        fn bit(b: Bool) -> Number { b as Number };
        fn text(b: Bool) -> String { b as String };
        fn flag(s: String) -> Bool { s as Bool };
        fn path_flag(p: Path) -> Bool { p as Bool };
        fn path_number(p: Path) -> Number { p as Number };
        fn same(n: Number, b: Bool, s: String) -> Number {
            if b as Bool { (n as Number) + bit(flag(s as String)) } else { 0 }
        };
        $std::io::println(text(truth(0)));
        $std::io::println(text(truth(-0)));
        $std::io::println(text(truth(0 / 0)));
        $std::io::println(text(truth(1 / 0)));
        $std::io::println(text(truth(-1 / 0)));
        $std::io::println(text(truth(5e-324)));
        $std::io::println(text(truth(-1)));
        $std::io::println(text(path_flag("  fAlSe  " as Path)));
        path_number(" 12.5 " as Path) + same(bit(flag("  TrUe  ")), true, "true")
        """;

    internal static void Run()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            CheckNumberText();
            for (int level = 0; level <= 3; level++)
            {
                CheckCorrect(level);
                CheckMixed(level);
                CheckObservedText(level);
                CheckBad(level, "\"1\"", "Bool", "String '1'",
                    "Use Number or the text 'true' or 'false'; strings are not tested for nonemptiness.");
                CheckBad(level, "(\"maybe\" as Path)", "Bool", "Path 'maybe'",
                    "Use Number or the text 'true' or 'false'; strings are not tested for nonemptiness.");
                CheckBad(level, "(\"not-a-number\" as Path)", "Number", "Path 'not-a-number'",
                    "Use invariant numeric text, such as '12.5' or '1e3'.");
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Console.WriteLine("PASS ready Number/Bool/String/Path casts, exact failure and identity at O0–O3");
    }

    private static void CheckNumberText()
    {
        HpirProgram program = Bind("""
            let line? = $std::io::read_line();
            if line is null { 42 } else { line as Number }
            """, 2);
        (string Text, double Expected, bool ShortInteger)[] inputs =
        [
            ("-0", -0.0, true),
            ("+999999999999999", 999999999999999, true),
            ("9007199254740993", 9007199254740992, false),
            (" -0 ", -0.0, false),
            ("-0x10", -16, false),
            ("1.25e2", 125, false)
        ];
        foreach (var (text, expected, shortInteger) in inputs)
        {
            using var input = new StringReader(text + "\n");
            var profile = new RuntimeProfile();
            ulong value = HpirExecutor.Run(program, new() { Input = input, Profile = profile });
            string metric = shortInteger ? "text_number_parse_short_integer" : "text_number_parse_general";
            Require(value == BitConverter.DoubleToUInt64Bits(expected)
                && profile.Drain().Any(sample => sample.Name == "vm.function" && sample.Metrics.GetValueOrDefault(metric) == 1),
                "The live Number cast must retain exact bits and attribute the selected parser.");
        }
    }

    private static void CheckMixed(int level)
    {
        const string source = """
            fn payload(use_number: Bool) -> Number or String {
                if use_number { 1 } else { "False" }
            };
            fn truth(value: Number or String) -> Bool { value as Bool };
            fn display(value: Number or String) -> String { value as String };
            let one = payload(true);
            let no = payload(false);
            $std::io::println(display(one));
            $std::io::println(display(no));
            if truth(one) and not truth(no) { 19 } else { 17 }
            """;
        HpirProgram program = Bind(source, level);
        foreach (bool native in Engines())
        {
            using var output = new StringWriter { NewLine = "\n" };
            ulong value = Run(program, native, new(Output: output));
            Require(BitConverter.UInt64BitsToDouble(value) == 19 && output.ToString() == "1\nFalse\n",
                "A genuine Number/String alternative must select its own conversion, including the identity arm.");
        }
    }

    private static void CheckObservedText(int level)
    {
        const string source = """
            fn flag() -> Bool {
                let line? = $std::io::read_line();
                if line is null { return false; };
                line as Bool
            };
            if flag() { 1 } else { 0 }
            """;
        HpirProgram program = Bind(source, level);
        Require(program.Imports.Any(import => import.Service == HpirService.ParseBool)
            && program.Imports.All(import => import.Service != HpirService.FutureLift),
            "An actual null-test demand must preserve the same ready input for Bool parsing.");
        foreach (bool native in Engines())
        {
            using var input = new StringReader("  tRuE  \n");
            Require(BitConverter.UInt64BitsToDouble(Run(program, native, new() { Input = input })) == 1,
                "An observed input lost its parsed Bool.");
        }
    }

    private static void CheckCorrect(int level)
    {
        var observations = new List<CompilationObservation>();
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        HpirProgram program = Bind(Source, level, trace);
        var instructions = program.Functions.SelectMany(function => function.Instructions).Select(instruction => instruction.Op).ToHashSet();
        var imports = program.Imports.Select(import => import.Service).ToHashSet();
        Require(instructions.Contains(HpirOp.BoolFromF64) && instructions.Contains(HpirOp.ConvertU8ToF64)
            && imports.IsSupersetOf([HpirService.ParseBool, HpirService.ParsePathBool, HpirService.ParsePathF64])
            && !imports.Contains(HpirService.FutureDemand)
            && observations.Any(item => item.Metrics?.GetValueOrDefault("casts.bool_text_literal") == 1),
            "Casts must select monomorphic primitive/parse paths rather than work or boxed values.");

        foreach (bool native in Engines())
        {
            using var output = new StringWriter { NewLine = "\n" };
            var timeline = new List<RuntimeTimelineRecord>();
            ulong bits = Run(program, native, new(Output: output) { Timeline = new(timeline.Add, "scalar-casts") });
            Require(BitConverter.UInt64BitsToDouble(bits) == 14.5
                && output.ToString() == "false\nfalse\nfalse\ntrue\ntrue\ntrue\ntrue\nfalse\n",
                "Cast value, result spelling or culture-independent semantics changed.");
            Require(timeline.Any(item => item.Name == "hpir.scalar.parse"
                && item.Attributes.GetValueOrDefault("parse.carrier") == "Path"
                && item.Attributes.GetValueOrDefault("parse.result") == "Number"
                && item.Metrics.GetValueOrDefault("parse.utf16_units") == 6
                && item.Attributes.GetValueOrDefault("source.file") == "scalar-casts.msync"),
                "The typed Path parse must be attributed to its actual guest source.");
        }
    }

    private static void CheckBad(int level, string input, string target, string display, string help)
    {
        string source = $"$std::io::println(\"first\");\n{input} as {target}";
        HpirProgram program = Bind(source, level);
        foreach (bool native in Engines())
        {
            using var output = new StringWriter { NewLine = "\n" };
            try
            {
                _ = Run(program, native, new(Output: output));
                throw new Exception("A bad explicit cast was accepted.");
            }
            catch (MsyncException error)
            {
                Require(error.Message == $"Cannot cast {display} to {target}. {help}"
                    && error.Span is { } span && source.AsSpan(span.Start, span.Length).EndsWith($"as {target}", StringComparison.Ordinal)
                    && output.ToString() == "first\n", "Cast diagnostic lost its original type, source or effect order.");
            }
        }
    }

    private static HpirProgram Bind(string source, int level, CompilationTrace? trace = null)
    {
        var module = Compiler.Analyze(source, "scalar-casts.msync", trace);
        HpirProgram produced = Reload(Compiler.LowerHpir(module, level, trace));
        return Reload(HpirBinding.Prepare(produced, new(Environment.CurrentDirectory, Environment.CurrentDirectory)));
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static IEnumerable<bool> Engines()
    {
        yield return false;
        if (HpirMachineImage.IsSupported) yield return true;
    }

    private static ulong Run(HpirProgram program, bool native, ExecutionOptions options)
    {
        if (!native) return HpirExecutor.Run(program, options);
        using var image = HpirMachineImage.Compile(program);
        return HpirExecutor.RunMachineCandidate(image, options);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
