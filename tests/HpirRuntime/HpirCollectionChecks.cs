using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using static Msync.Core.Hpir.HpirOp;
using static Msync.Core.Hpir.HpirWidth;

internal static class HpirCollectionChecks
{
    internal static void Run()
    {
        SourceSpan allocation = new("hpir-collection-test.msync", 96, 5);
        var entry = new HpirFunction("entry", 48, U64, [],
        [
            new(Call, D: 0, A: 0),
            new(Call, D: 8, A: 1),
            new(CopyU64, D: ~0, A: 8),
            F64(~8, 41),
            new(AddressOf, D: 16, A: 40),
            new(Call, A: 2),
            new(CopyU64, D: 8, A: ~0),
            new(AddF64, D: 24, A: ~8, B: 40),
            new(Return, A: 24)
        ], [new(~0, []), new(~1, []), new(1, [16])], [])
        { Safepoints = [new(0, []), new(1, [0]), new(5, [0])] };

        var collect = new HpirFunction("collect", 64, HpirWidth.Void, [new(0, U64)],
        [
            new(Call, D: 8, A: 0),
            new(OffsetPointer, D: 16, A: 8, Immediate: 8),
            F64(~16, 1),
            F64(24, 1),
            F64(40, 32),
            F64(48, 0),
            new(Call, D: 32, A: 1),
            new(SubF64, D: 40, A: 40, B: 24),
            new(GtF64, D: 56, A: 40, B: 48),
            new(Branch, D: 10, A: 56, B: 6),
            new(CopyU64, D: ~0, A: ~16),
            new(Return)
        ], [new(~2, []), new(~3, [])], [new(6, allocation)])
        {
            Safepoints = [new(0, []) { BorrowOffsets = [0] }, new(6, []) { BorrowOffsets = [0, 16] }]
        };
        HpirProgram program = new([entry, collect], 0)
        {
            Layouts = [new(8, 8, [0]), new(8, 8, []), new(16, 8, []), new(32 * 1024, 8, [])],
            Imports =
            [
                new(HpirService.AllocateObject, 0, U64, []),
                new(HpirService.AllocateObject, 1, U64, []),
                new(HpirService.AllocateObject, 2, U64, []),
                new(HpirService.AllocateObject, 3, U64, [])
            ]
        };

        var records = new List<RuntimeTimelineRecord>();
        var profile = new RuntimeProfile(instructionSites: true);
        var options = new ExecutionOptions { Profile = profile, Timeline = new(records.Add, "test") };
        Require(HpirExecutor.Run(program, options) == Bits(42), "active interior and suspended exact roots survive");
        Require(HpirExecutor.Run(program) == Bits(42), "collection and transitive trace without recording");

        var collections = records.Where(static record => record.Name == "hpir.heap.collect").ToArray();
        Require(collections.Length > 1 && collections.All(record => record.Outcome == "ok"
            && record.Metrics["gc.stack_frames"] == 2 && record.Metrics["gc.root_locations"] == 1
            && record.Metrics["gc.borrow_locations"] == 2 && record.Metrics["gc.root_objects"] == 2
            && record.Metrics["gc.live_objects"] == 3 && record.Metrics["gc.reclaimed_objects"] > 0
            && record.Metrics["gc.borrow_matches"] == 1 && record.Metrics["gc.traced_edges"] > 0
            && record.Attributes["source.file"] == allocation.Source), "causal collection metrics and source");

        IReadOnlyList<RuntimeProfileSample> samples = profile.Drain();
        RuntimeProfileSample function = samples.Single(sample => sample.Name == "vm.function"
            && sample.Attributes["function.name"] == "collect");
        Require(function.Metrics["gc_collections"] == (ulong)collections.Length
            && function.Metrics["native_heap_objects"] == 33 && function.Metrics["gc_reclaimed_objects"] > 0,
            "attributed heap and collection census");
        Require(samples.Any(sample => sample.Name == "vm.native" && sample.Attributes["allocation.layout"] == "3"
            && sample.Attributes.GetValueOrDefault("source.file") == allocation.Source && sample.Metrics["calls"] == 32),
            "allocation import and concrete layout attribution");
    }

    private static HpirInstruction F64(int offset, double value) =>
        new(ConstU64, D: offset, Immediate: BitConverter.DoubleToInt64Bits(value));

    private static ulong Bits(double value) => (ulong)BitConverter.DoubleToInt64Bits(value);

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new Exception(detail);
    }
}
