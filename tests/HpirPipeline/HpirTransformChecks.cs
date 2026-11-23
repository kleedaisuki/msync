using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirTransformChecks
{
    private const string Demand = """
        let hits = 0;
        fn count(n: Number) -> Number { global hits; hits = hits + 1; n * 2 };
        let view = $std::ranges::transform($std::ranges::iota(0, 8), count);
        let none = $std::ranges::materialize($std::ranges::take(view, 0));
        $std::io::println($std::ranges::size(none) as String);
        $std::io::println(hits as String);
        let first = $std::ranges::materialize($std::ranges::take(view, 2));
        $std::io::println($std::ranges::size(first) as String);
        $std::io::println(hits as String);
        $std::io::println($std::ranges::front(view) as String);
        $std::io::println(hits as String);
        """;

    private const string Pull = """
        let hits = 0;
        fn transform(n: Number) -> Number { global hits; hits = hits + 1; n * 2 };
        let view = $std::ranges::transform($std::ranges::iota(1, 9), transform);
        let total = 0;
        for value in view { total = total + value; break; };
        for value in $std::ranges::take(view, 2) { total = total + value; };
        $std::io::println(total as String);
        $std::io::println(hits as String);
        """;

    private const string VectorFold = """
        let hits = 0;
        fn visit(n: Number) -> Number { global hits; hits = hits + 1; n };
        fn quota(n: Number) -> Number { n - 1 };
        let values = $std::ranges::transform([1, 2, 3], visit);
        let empty = $std::ranges::take(values, 0);
        $std::io::println($std::ranges::size(empty) as String);
        $std::io::println(hits as String);
        let answer = $std::ranges::fold($std::ranges::take(values, quota(3)), 0,
            fn(accumulator: Number, value: Number) { accumulator + value });
        $std::io::println(answer as String);
        $std::io::println(hits as String);
        """;

    private const string Records = """
        let seed? = $std::io::read_line();
        let state = if seed is null { 1 } else { seed as Number };
        let size? = $std::io::read_line();
        let count = if size is null { 4096 } else { size as Number };
        fn row(i: Number) {
            state = (state * 48271) % 2147483647;
            let low = state % 4096;
            let high = low + state % 31;
            scope { low: low, high: high, keep: i % 7 != 0 }
        };
        let rows = $std::ranges::materialize($std::ranges::transform($std::ranges::iota(0, count), row));
        let seen = 0;
        let widths = 0;
        let checksum = 0;
        for item in rows {
            if item::keep {
                seen = seen + 1;
                widths = widths + item::high - item::low;
                checksum = (checksum * 65599 + item::low + item::high) % 1000003;
            };
        };
        $std::io::println(seen as String);
        $std::io::println(widths as String);
        $std::io::println(checksum as String);
        """;

    private const string Sensor = """
        let header? = $std::io::read_line();
        if header? {
            $std::io::println("missing header");
        } else {
            let limit = header as Number;
            let readings = $std::ranges::transform($std::io::stdin_lines(), fn(line: String) { line as Number });
            let accepted = $std::ranges::take($std::ranges::filter(readings, fn(value: Number) { value >= 0 }), limit);
            let previous? = null;
            let count = 0;
            let changes = 0;
            let checksum = 0;
            for value in accepted {
                if previous is not null {
                    let delta = value - previous;
                    if delta > 4 or delta < -4 { changes = changes + 1; };
                    checksum = checksum + ((count % 29) + 1) * delta;
                };
                previous = value;
                count = count + 1;
            };
            $std::io::println(count as String);
            $std::io::println(changes as String);
            $std::io::println(checksum as String);
        };
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; ++level)
        {
            Check("transform-demand", Demand, level, "count", "0\n0\n2\n2\n0\n3\n", 3, 1, 0, 16);
            Check("transform-reopen", Pull, level, "transform", "8\n3\n", 3, 0, 0, 0);
            Check("vector-fold", VectorFold, level, "visit", "0\n0\n3\n2\n", 2, 0, 0, 0, stdlibConsumer: true);
            CheckNullableEnvelope(level);
        }
        const int rows = 8192, seed = 17;
        int seen = 0, widths = 0, checksum = 0;
        long state = seed;
        for (int i = 0; i < rows; ++i)
        {
            state = state * 48271 % 2147483647;
            if (i % 7 == 0) continue;
            int low = (int)(state % 4096), width = (int)(state % 31);
            ++seen;
            widths += width;
            checksum = (int)((checksum * 65599L + 2 * low + width) % 1000003);
        }
        Check("transform-record-stream", Records, 2, "row", $"{seen}\n{widths}\n{checksum}\n", rows, 10, 8176, rows,
            $"{seed}\n{rows}\n", collects: true);
        CheckEmptyFront();
        CheckTextDemand();
        Console.WriteLine("PASS HPIR typed Range transforms: 0/2/1 compiled callbacks, records/GC, bounded live sensor demand");
    }

    private static void CheckNullableEnvelope(int level)
    {
        const string source = """
            fn choose(flag: Bool) { if flag { [1, 2] } else { null } };
            let input? = $std::io::read_line();
            let selected? = choose(input is not null);
            if selected is not null {
                $std::io::println($std::string::join($std::ranges::map(selected, fn(value: Number) { (value + 1) as String }), ","));
            } else { $std::io::println("absent"); };
            """;
        HpirProgram program = Prepare("nullable-range-envelope.msync", source, level, new(_ => { }));
        Verify(null);
        if (HpirMachineImage.IsSupported)
        {
            using var image = HpirMachineImage.Compile(program);
            Verify(image);
        }
        return;

        void Verify(HpirMachineImage? image)
        {
            foreach (var (input, expected) in new[] { ("ready\n", "2,3\n"), ("", "absent\n") })
            {
                using var reader = new StringReader(input);
                using var output = new StringWriter { NewLine = "\n" };
                var options = new ExecutionOptions(Output: output) { Input = reader };
                if (image is null) _ = HpirExecutor.Run(program, options);
                else _ = HpirExecutor.RunMachineCandidate(image, options);
                if (output.ToString() != expected)
                    throw new Exception($"The selected nullable snapshot did not preserve its Range arm at O{level}: {output}");
            }
        }
    }

    private static void Check(string name, string source, int level, string callback, string expected,
        ulong callbackCalls, ulong growths, ulong copied, ulong capacity, string input = "", bool collects = false,
        bool stdlibConsumer = false)
    {
        string path = name + ".msync";
        List<CompilationObservation> compilation = [];
        var trace = new CompilationTrace(compilation.Add, includeOptimizerPasses: true);
        HpirProgram program = Prepare(path, source, level, trace);
        Require(compilation.Any(item => item.Metrics?.GetValueOrDefault("range.transform.compiled_callback_sites") > 0),
            name + ": selection did not report the compiled callback site");
        int target = Enumerable.Range(0, program.Functions.Count).Single(id => program.Functions[id].Name == callback);
        Run(false);
        if (HpirMachineImage.IsSupported) Run(true);

        void Run(bool native)
        {
            using var output = new StringWriter { NewLine = "\n" };
            using var reader = new StringReader(input);
            var profile = new RuntimeProfile(instructionSites: true);
            var options = new ExecutionOptions(Output: output) { Input = reader, Profile = profile };
            if (!native) HpirExecutor.Run(program, options);
            else
            {
                using var image = HpirMachineImage.Compile(program, observe: true);
                HpirExecutor.RunMachineCandidate(image, options);
            }
            string context = $"{name} O{level} {(native ? "native" : "VM")}";
            Require(output.ToString() == expected, context + ": " + output);
            var samples = profile.Drain();
            var calls = samples.Where(sample => sample.Name == "vm.call"
                && sample.Attributes.GetValueOrDefault("target.function.id") == target.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Require(Sum(calls, "hits") == callbackCalls && calls.All(sample =>
            {
                string? file = sample.Attributes.GetValueOrDefault("source.file");
                return stdlibConsumer ? file?.EndsWith("/ranges.msync", StringComparison.Ordinal) == true : file == path;
            }),
                context + ": callback count/source lost at its actual consumption call");
            Require(Sum(samples, "range_buffer_growths") == growths && Sum(samples, "range_buffer_copied_items") == copied
                && Sum(samples, "range_buffer_peak_capacity") == capacity, context + ": incorrect materialization growth work");
            Require(!collects || Sum(samples, "gc_collections") > 0, context + ": no real collection with retained records");
        }
    }

    private static void CheckEmptyFront()
    {
        const string source = """
            fn forbidden(n: Number) -> Number { n * 2 };
            let view = $std::ranges::transform($std::ranges::iota(0, 0), forbidden);
            $std::ranges::front(view)
            """;
        HpirProgram program = Prepare("empty-transform.msync", source, 2);
        var profile = new RuntimeProfile();
        try { HpirExecutor.Run(program, new() { Profile = profile }); }
        catch (MsyncException error) when (error.Message.Contains("ranges.front requires a nonempty Range", StringComparison.Ordinal)
            && error.Span?.Source == "empty-transform.msync")
        {
            int callback = Enumerable.Range(0, program.Functions.Count).Single(id => program.Functions[id].Name == "forbidden");
            Require(!profile.Drain().Any(sample => sample.Name == "vm.call"
                && sample.Attributes.GetValueOrDefault("target.function.id") == callback.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "Empty front dispatched its callback.");
            return;
        }
        throw new Exception("Empty front must fail at its own source, without dispatching the callback.");
    }

    private static void CheckTextDemand()
    {
        for (int level = 0; level <= 3; ++level)
        {
            HpirProgram bounded = Prepare("bounded-sensor.msync", Sensor, level);
            Run(bounded, native: false);
            if (HpirMachineImage.IsSupported) Run(bounded, native: true);
        }

        static void Run(HpirProgram program, bool native)
        {
            Check(["3", "-4", "7", "-3", "2", "8"], "3\n2\n8\n");
            Check(["0"], "0\n0\n0\n");

            void Check(string[] lines, string expected)
            {
                using var reader = new BoundedReader(lines);
                using var output = new StringWriter { NewLine = "\n" };
                var profile = new RuntimeProfile(instructionSites: true);
                ConcurrentQueue<RuntimeTimelineRecord> timeline = new();
                var options = new ExecutionOptions(Output: output)
                {
                    Input = reader, Profile = profile, Timeline = new(timeline.Enqueue, "bounded-sensor")
                };
                if (!native) HpirExecutor.Run(program, options);
                else
                {
                    using var image = HpirMachineImage.Compile(program, observe: true);
                    HpirExecutor.RunMachineCandidate(image, options);
                }
                Require(output.ToString() == expected && reader.Reads == lines.Length && !reader.Disposed,
                    "Sensor demand changed its output, prefetched, or closed borrowed stdin.");
                var samples = profile.Drain();
                Require(Sum(samples, "text_range_yields") == (ulong)(lines.Length - 1)
                    && Sum(samples.Where(sample => sample.Name == "vm.call"
                        && sample.Attributes.ContainsKey("source.file")), "hits") == (ulong)(lines.Length - 1) * 2,
                    "Only actual line pulls and compiled map/predicate calls should be counted.");
                int cursors = lines.Length == 1 ? 0 : 1;
                Require(timeline.Count(item => item.Name == "hpir.text_range.open") == cursors
                    && timeline.Count(item => item.Name == "hpir.text_range.close") == cursors,
                    "Bounded sensor did not match its actual cursor ownership.");
            }
        }
    }

    private sealed class BoundedReader(string[] lines) : TextReader
    {
        internal int Reads { get; private set; }
        internal bool Disposed { get; private set; }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Reads >= lines.Length) throw new InvalidOperationException("The next sensor line was not demanded.");
            return ValueTask.FromResult<string?>(lines[Reads++]);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed |= disposing;
            base.Dispose(disposing);
        }
    }

    private static HpirProgram Prepare(string path, string source, int level, CompilationTrace? trace = null)
    {
        var compiled = Compiler.LowerHpir(Compiler.Analyze(source, path, trace), level, trace);
        var detached = RoundTrip(compiled);
        return RoundTrip(HpirBinding.Prepare(detached, new(Environment.CurrentDirectory, HostContext.HomeDirectory)));
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static ulong Sum(IEnumerable<RuntimeProfileSample> samples, string metric) =>
        samples.Aggregate(0UL, (total, sample) => total + sample.Metrics.GetValueOrDefault(metric));

    private static void Require(bool passes, string message)
    {
        if (!passes) throw new Exception(message);
    }
}
