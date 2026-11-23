using System.Globalization;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal readonly struct HpirDiagnostics(HpirProgram program, RuntimeProfile.HpirCensus? census, RuntimeTimeline? timeline)
{
    internal SourceSpan? Source(int function, int instruction)
    {
        IReadOnlyList<HpirSource> sources = program.Functions[function].Sources;
        if (instruction < 0) return sources.Count == 0 ? null : sources[0].Source;
        int left = 0, right = sources.Count;
        while (left < right)
        {
            int middle = left + (right - left) / 2;
            HpirSource source = sources[middle];
            if (source.Instruction <= instruction) left = middle + 1;
            else right = middle;
        }
        return left == 0 ? null : sources[left - 1].Source;
    }

    internal RuntimeTimelineScope? BeginExecution(int function)
    {
        if (timeline is null) return null;
        RuntimeTimelineScope scope = timeline.Begin("hpir.execute");
        Function(scope, function);
        scope.SetSource(Source(function, -1));
        return scope;
    }

    internal RuntimeTimelineScope? BeginSegment(int caller, int instruction, int target, int extent)
    {
        if (timeline is null) return null;
        RuntimeTimelineScope scope = timeline.Begin("hpir.stack.segment");
        Function(scope, target);
        scope.SetMetric("stack.frame_extent", extent);
        if (caller >= 0)
        {
            scope.SetAttribute("caller.function.id", Number(caller));
            scope.SetAttribute("caller.function.name", program.Functions[caller].Name);
            scope.SetMetric("caller.instruction", instruction);
        }
        scope.SetSource(caller < 0 ? Source(target, -1) : Source(caller, instruction));
        return scope;
    }

    internal void Segment(int target, bool reused, long reserved, long released, RuntimeTimelineScope? scope)
    {
        census?.For(target).EnterSegment(reused, reserved, released);
        if (scope is null) return;
        scope.SetAttribute("stack.segment.action", reused ? "reenter" : released != 0 ? "replace" : "allocate");
        scope.SetMetric("stack.reserved_bytes", reserved);
        scope.SetMetric("stack.released_bytes", released);
    }

    internal RuntimeTimelineScope? BeginCollection(int function, int instruction, int layout)
    {
        if (timeline is null) return null;
        RuntimeTimelineScope scope = timeline.Begin("hpir.heap.collect");
        Function(scope, function);
        scope.SetMetric("instruction", instruction);
        scope.SetMetric("allocation.requested_layout", layout);
        scope.SetSource(Source(function, instruction));
        return scope;
    }

    internal RuntimeTimelineScope? BeginTextWrite(int function, int instruction, HpirTextOutput output, int length)
    {
        if (timeline is null) return null;
        RuntimeTimelineScope scope = timeline.Begin("hpir.host.write");
        Function(scope, function);
        scope.SetAttribute("native.name", HpirScalarImports.TextOutputName(output));
        scope.SetSource(Source(function, instruction));
        scope.SetMetric("instruction", instruction);
        scope.SetMetric("output.characters", length);
        return scope;
    }

    internal RuntimeTimelineScope? BeginTextDemand(int function, int instruction)
    {
        if (timeline is null) return null;
        RuntimeTimelineScope scope = timeline.Begin("hpir.future.text_demand");
        Function(scope, function);
        scope.SetSource(Source(function, instruction));
        scope.SetMetric("instruction", instruction);
        return scope;
    }

    internal RuntimeTimelineScope? BeginRangePack(int function, int instruction, int segments, ulong length, int stride)
    {
        if (timeline is null) return null;
        var scope = timeline.Begin("hpir.range.pack");
        Function(scope, function);
        scope.SetSource(Source(function, instruction));
        scope.SetMetric("range.segments", segments);
        scope.SetMetric("range.items", (long)length);
        scope.SetMetric("range.item_bytes", stride);
        return scope;
    }

    internal RuntimeTimelineScope? BeginRegex(int function, int instruction, HpirRegexOp operation,
        bool literal, int length, int? patternLength)
    {
        if (timeline is null) return null;
        var scope = timeline.Begin("hpir.regex");
        Function(scope, function);
        scope.SetSource(Source(function, instruction));
        scope.SetAttribute("regex.operation", HpirRegexImports.Name(operation));
        scope.SetMetric("regex.input_code_units", length);
        if (patternLength is { } pattern)
        {
            scope.SetMetric("regex.pattern_code_units", pattern);
            scope.SetMetric("regex.literal", literal ? 1 : 0);
        }
        return scope;
    }

    internal RuntimeTimelineScope? BeginRegexFind(int function, int instruction, bool literal, int length, int patternLength)
    {
        if (timeline is null) return null;
        var scope = timeline.Begin("hpir.regex.find");
        Function(scope, function);
        scope.SetSource(Source(function, instruction));
        scope.SetMetric("regex.input_code_units", length);
        scope.SetMetric("regex.pattern_code_units", patternLength);
        scope.SetMetric("regex.literal", literal ? 1 : 0);
        return scope;
    }

    internal RuntimeTimelineScope? BeginScalarParse(HpirService service, int function, int instruction)
    {
        if (timeline is null) return null;
        RuntimeTimelineScope scope = timeline.Begin("hpir.scalar.parse");
        Function(scope, function);
        scope.SetAttribute("parse.carrier", service == HpirService.ParseBool ? "String" : "Path");
        scope.SetAttribute("parse.result", service == HpirService.ParsePathF64 ? "Number" : "Bool");
        scope.SetMetric("instruction", instruction);
        scope.SetSource(Source(function, instruction));
        return scope;
    }

    internal static void CollectionWork(RuntimeTimelineScope? scope, int frames, int locations, int borrows, int marked)
    {
        if (scope is null) return;
        scope.SetMetric("gc.stack_frames", frames);
        scope.SetMetric("gc.root_locations", locations);
        scope.SetMetric("gc.borrow_locations", borrows);
        scope.SetMetric("gc.root_objects", marked);
    }

    internal static void EndCollection(RuntimeTimelineScope? scope, int frames, int locations, int borrows, int marked,
        HpirHeap.Sweep sweep)
    {
        if (scope is null) return;
        CollectionWork(scope, frames, locations, borrows, marked);
        scope.SetMetric("gc.live_objects", sweep.LiveObjects);
        scope.SetMetric("gc.live_bytes", sweep.LiveBytes);
        scope.SetMetric("gc.reclaimed_objects", sweep.ReclaimedObjects);
        scope.SetMetric("gc.reclaimed_bytes", sweep.ReclaimedBytes);
        scope.SetMetric("gc.retained_free_bytes", sweep.FreeBytes);
        scope.SetMetric("gc.released_bytes", sweep.ReleasedBytes);
        scope.SetMetric("gc.traced_edges", sweep.TracedEdges);
        scope.SetMetric("gc.borrow_candidates", sweep.BorrowCandidates);
        scope.SetMetric("gc.borrow_matches", sweep.BorrowMatches);
        scope.SetMetric("gc.indexed_objects", sweep.IndexedObjects);
        scope.SetMetric("gc.workspace_bytes", sweep.TraceWorkspaceBytes);
        if (sweep.ProcessResultsReleased != 0)
            scope.SetMetric("gc.process_results_released", sweep.ProcessResultsReleased);
        if (sweep.FileReadResultsReleased != 0)
            scope.SetMetric("gc.file_read_results_released", sweep.FileReadResultsReleased);
    }

    internal void Failure(RuntimeTimelineScope? scope, Exception error, int function, int instruction, int frames)
    {
        if (scope is null) return;
        scope.Fail(error);
        scope.SetAttribute("failure.function.id", Number(function));
        scope.SetAttribute("failure.function.name", program.Functions[function].Name);
        scope.SetMetric("failure.instruction", instruction);
        scope.SetMetric("stack.frames_discarded", frames);
        scope.SetSource(Source(function, instruction) ?? Source(function, -1));
    }

    private void Function(RuntimeTimelineScope scope, int function)
    {
        scope.SetAttribute("function.id", Number(function));
        scope.SetAttribute("function.name", program.Functions[function].Name);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
