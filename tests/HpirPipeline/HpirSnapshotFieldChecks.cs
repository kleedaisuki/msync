using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirSnapshotFieldChecks
{
    private const string Clone = """
        let Cursor = $std::ranges::Cursor;
        let first = Cursor::begin([null, 5]);
        let clone = Cursor::clone(first);
        let initial = Cursor::has_value(first) and Cursor::value(first) is null;
        let more = Cursor::next(first);
        let read? = Cursor::value(first);
        let buffered = Cursor::has_value(clone) and Cursor::value(clone) is null;
        let continued = Cursor::next(clone);
        let another? = Cursor::value(clone);
        let finished = not Cursor::next(first) and not Cursor::has_value(first);
        if initial and more and buffered and continued and finished and read is not null and another is not null {
            read * 10 + another
        } else { -1 }
        """;

    private const string Reassign = """
        let Cursor = $std::ranges::Cursor;
        let cursor = Cursor::begin([null, 5]);
        let clone = Cursor::clone(cursor);
        let alias = cursor;
        alias::items = [null, 9];
        Cursor::next(cursor);
        Cursor::next(clone);
        let updated? = Cursor::value(cursor);
        let original? = Cursor::value(clone);
        if updated is not null and original is not null { updated * 10 + original } else { -1 }
        """;

    private const string Split = """
        let Cursor = $std::ranges::Cursor;
        let first = Cursor::begin($std::ranges::drop([null, 5], 1));
        let clone = Cursor::clone(first);
        let value? = Cursor::value(clone);
        if Cursor::has_value(clone) and value is not null and not Cursor::next(clone) { value } else { -1 }
        """;

    private const string Mixed = """
        let Cursor = $std::ranges::Cursor;
        let cursor = Cursor::begin([null, 5]);
        let alias = cursor;
        alias::items = $std::ranges::drop([null, 7], 1);
        let item? = Cursor::value(cursor);
        if item is not null { item } else { -1 }
        """;

    private const string LiveOverwrite = """
        let Cursor = $std::ranges::Cursor;
        let cursor = Cursor::begin([4, 5]);
        let alias = cursor;
        alias::items = $std::ranges::iota(0, 2);
        Cursor::value(cursor)
        """;

    private const string Sorted = """
        let calls = 0;
        fn less(left: Number, right: Number) -> Bool {
            calls = calls + 1;
            $std::ranges::less(left, right)
        };
        let short = not $std::ranges::is_sorted_with([1, 4, 2, 3], less);
        let ordered = $std::ranges::is_sorted_with([0/0, -1, -0, 0], less);
        let nan_late = not $std::ranges::is_sorted_with([1, 0/0], less);
        let single = $std::ranges::is_sorted_with([1], less);
        let empty = $std::ranges::is_sorted_with($std::ranges::empty_of(Number), less);
        if short and ordered and nan_late and single and empty { calls } else { -1 }
        """;

    internal static void Run()
    {
        for (int level = 0; level <= 3; ++level)
        {
            Require(Clone, 55, level);
            Require(Reassign, 95, level);
            Require(Split, 5, level);
            Require(Mixed, 7, level, consumers: 1);
            RequireSlidingReads(level);
        }
        RequireConflict();
        RetainSlideOwner();
        Console.WriteLine("PASS HPIR public Cursor and sorted reads: nullable items, clone independence, common snapshot layout, genuine live overwrite and collection");
    }

    private static void Require(string source, double expected, int level, int consumers = 2)
    {
        var observations = new List<CompilationObservation>();
        HpirProgram portable = Lower(source, level, observations);
        if (!observations.Any(item => item.Metrics is { } counts
            && counts.GetValueOrDefault("range.fields.snapshot_consumers") >= (ulong)consumers
            && counts.GetValueOrDefault("range.fields.snapshot_edges") > 0))
            throw new InvalidOperationException($"O{level} did not retain the producer's concrete snapshot fields.");
        RequireResult(portable, expected, level);
    }

    private static void RequireSlidingReads(int level)
    {
        var observations = new List<CompilationObservation>();
        HpirProgram portable = Lower(Sorted, level, observations);
        if (!observations.Any(item => item.Metrics?.GetValueOrDefault("range.slide.local_reads") == 2)
            || !observations.Any(item => item.Metrics?.GetValueOrDefault("range.sorted.natural_number_less_sites") == 1))
            throw new InvalidOperationException("The adjacent comparison did not select borrowed Number pairs.");
        RequireResult(portable, 6, level);
    }

    private static void RetainSlideOwner()
    {
        const string source = """
            fn less(current: Number, previous: Number) -> Bool {
                let i = 0;
                let units = 0;
                loop {
                    if i == 4500 { break; };
                    units = units + $std::string::length($std::string::repeat("😀", 512));
                    i = i + 1;
                };
                units != 4500 * 512 or $std::ranges::less(current, previous)
            };
            if $std::ranges::is_sorted_with([1, 2, 3], less) { 1 } else { -1 }
            """;
        RequireResult(Lower(source, 2, []), 1, 2, expectCollection: true);
    }

    private static void RequireResult(HpirProgram portable, double expected, int level, bool expectCollection = false)
    {
        string cwd = Environment.CurrentDirectory;
        using var output = new MemoryStream();
        HpirArtifact.WriteProduced(portable, output);
        output.Position = 0;
        HpirProgram program = HpirBinding.Prepare(HpirArtifact.ReadTrusted(output), new(cwd, cwd));
        output.SetLength(0);
        HpirArtifact.WriteProduced(program, output);
        output.Position = 0;
        program = HpirArtifact.ReadTrusted(output);
        var interpreted = expectCollection ? new RuntimeProfile() : null;
        ulong bits = HpirExecutor.Run(program, new() { Profile = interpreted });
        if (BitConverter.UInt64BitsToDouble(bits) != expected)
            throw new InvalidOperationException($"O{level} public Cursor expected {expected}, got {BitConverter.UInt64BitsToDouble(bits)}.");
        RequireCollection(interpreted);
        if (!HpirMachineImage.IsSupported) return;
        var machine = expectCollection ? new RuntimeProfile() : null;
        using var image = HpirMachineImage.Compile(program, observe: expectCollection);
        if (HpirExecutor.RunMachineCandidate(image, new() { Profile = machine }) != bits)
            throw new InvalidOperationException($"O{level} native public Cursor differs from the interpreted result.");
        RequireCollection(machine);
    }

    private static void RequireCollection(RuntimeProfile? profile)
    {
        if (profile is not null && !profile.Drain().Any(sample => sample.Metrics.GetValueOrDefault("gc_collections") != 0))
            throw new InvalidOperationException("The borrowed adjacent-window control never crossed a guest collection.");
    }

    private static void RequireConflict()
    {
        var observations = new List<CompilationObservation>();
        HpirProgram? portable = null;
        try { portable = Lower(LiveOverwrite, 2, observations); }
        catch (MsyncException failure) when (failure.Message.Contains("ranges.at", StringComparison.Ordinal)) { }
        if (!observations.Any(item => item.Metrics?.GetValueOrDefault("range.fields.snapshot_conflicts") > 0
            && item.Metrics.GetValueOrDefault("range.fields.snapshot_consumers") == 0))
            throw new InvalidOperationException("The producer guessed one geometry for incompatible writes.");
        if (portable is not null) RequireResult(portable, 0, 2);
    }

    private static HpirProgram Lower(string source, int level, List<CompilationObservation> observations)
    {
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        return Compiler.LowerHpir(Compiler.Analyze(source, "public-snapshot-cursor.msync", trace), level, trace);
    }
}
