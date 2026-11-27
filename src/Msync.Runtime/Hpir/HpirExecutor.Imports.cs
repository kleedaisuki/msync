using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InvokeImport(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import, HpirCall call,
        byte* frame, int destination, ExecutionOptions? options, CancellationToken token, HpirPathAnchors anchors, nint* literals)
    {
        counts?.Import(pc, import);
        switch (import.Service)
        {
            case HpirService.AppendTextChunk:
                CallAppendTextChunk(program, ref heap, active, diagnostics, counts, pc, import, call, frame, destination, options);
                return;
            case HpirService.RangeSequence or HpirService.RangeDeferredSequence or HpirService.RangeDrop or HpirService.RangeTake
                or HpirService.RangeOpen:
                CallNumericRange(program, ref heap, work, active, diagnostics, counts, pc, import, call, frame, destination,
                    options, token);
                return;
            case HpirService.RangeTransform or HpirService.RangeTransformTake or HpirService.RangeTransformOpen
                or HpirService.RangeBufferGrow or HpirService.RangePackWords or HpirService.RangeBound:
                CallTransform(program, ref heap, work, active, diagnostics, counts, pc, import, call.Arguments,
                    frame, destination, options, token);
                return;
            case HpirService.LiveTransfer or HpirService.LiveTransferFutureText or HpirService.LiveTransferBoolText
                or HpirService.LiveTransferSelection:
                CallLiveTransfer(program, ref heap, ref work, active, diagnostics, counts, pc, import, call, frame, destination,
                    options, token, anchors.Binding(program.Context));
                return;
            case HpirService.FileMetadata:
                CallFileMetadata(program, ref heap, ref work, active, diagnostics, counts, pc, import, call, frame, destination, options, token, anchors);
                return;
            case HpirService.ReadFileText:
                CallFileText(program, ref heap, ref work, active, diagnostics, counts, pc, import, call.Arguments, frame, destination,
                    options, token, anchors);
                return;
            case HpirService.FileBytesOpen or HpirService.FileBytesChunk:
                CallFileBytes(program, ref work, active, diagnostics, pc, import, call, frame, destination, options, token, anchors);
                return;
            case HpirService.WriteFileText:
                CallFileTextWrite(program, ref work, active, diagnostics, pc, import, call.Arguments,
                    frame, options, token, anchors);
                return;
            case HpirService.CreateDirectories:
                CallCreateDirectories(program, ref work, active, diagnostics, pc, import, call, frame, destination,
                    options, token, anchors);
                return;
            case HpirService.RenameFile:
                CallFileRename(program, ref work, active, diagnostics, pc, call.Arguments, frame, options, token, anchors);
                return;
            case HpirService.Path:
                CallPath(program, ref heap, ref work, active, diagnostics, counts, pc, import, call, frame, destination, anchors, options, token);
                return;
            case HpirService.PathSelection:
                CallPathSelection(program, ref heap, active, diagnostics, counts, pc, import, call, frame, destination);
                return;
            case HpirService.DirectorySource or HpirService.DirectoryOpen or HpirService.DirectoryNext or HpirService.DirectoryClose:
                CallDirectory(program, ref heap, ref work, active, diagnostics, counts, pc, import, call, frame, destination,
                    options, anchors, token);
                return;
            case HpirService.Work:
                CallWorkFrontier(work, (HpirWorkOperation)import.Binding, call, frame, destination,
                    new(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc), token);
                return;
            case HpirService.HasLayout:
                WriteBool(frame, destination, HpirHeap.HasLayout(Read<nint>(frame, call.Arguments[0]), import.Binding));
                return;
            case HpirService.ContextCheck:
                WriteBool(frame, destination, ExecuteContextCheck(program.ContextChecks[import.Binding], program.Context, options, token));
                return;
            case HpirService.TypeName:
                Write(frame, destination, literals[import.Binding + Read<int>(frame, call.Arguments[0]) - 1]);
                return;
            case HpirService.TypeAccepts:
                WriteBool(frame, destination, AcceptsType(program.TypeLiterals, Read<int>(frame, call.Arguments[0]) - 1,
                    Read<int>(frame, call.Arguments[1]) - 1));
                return;
            case HpirService.AllocateObject:
                Write(frame, destination, Allocate(program, ref heap, active, pc, import.Binding, 1, diagnostics, counts));
                return;
            case HpirService.AllocateSessionScope:
                var scope = program.SessionAllocations[import.Binding];
                nint instance = Allocate(program, ref heap, active, pc, scope.Layout, 1, diagnostics, counts);
                HpirHeap.SetSessionSite(instance, scope.Site);
                Write(frame, destination, instance);
                return;
            case HpirService.SessionRangeItem:
                token.ThrowIfCancellationRequested();
                heap!.Session.DisplayItem(Read<nint>(frame, call.Arguments[0]), program.SessionRangeItems[import.Binding]);
                return;
            case HpirService.Word32:
                CallWord32(import, call, frame, destination, diagnostics, active->Function, pc);
                return;
            case HpirService.TextFormat:
                CallTextFormat(program, ref heap, work, active, diagnostics, counts, pc, import, call, frame, destination, token);
                return;
            case HpirService.ConcatTextPending:
                MakeTextConcat(program, ref heap, active, diagnostics, counts, pc, import, call, frame, destination);
                return;
            case HpirService.String:
                CallString(program, ref heap, active, diagnostics, counts, pc, (HpirStringOp)import.Binding, call.Arguments, frame, destination, token);
                return;
            case HpirService.Regex:
                CallRegex(program, ref heap, active, diagnostics, counts, pc, import, call.Arguments, frame, destination);
                return;
            case HpirService.RegexFind:
                CallRegexFind(program, ref heap, active, diagnostics, counts, pc, import, call.Arguments, frame, destination);
                return;
            case HpirService.TextRangeSource or HpirService.TextRangeOpen or HpirService.TextRangeNext or HpirService.TextRangeClose
                or HpirService.TextRangeJoin:
                CallTextRange(program, ref heap, ref work, active, diagnostics, counts, pc, import, call.Arguments,
                    frame, destination, options, token, anchors);
                return;
            case HpirService.MathF64:
                CallMath(import, frame, destination, call.Arguments, active->Function, pc, diagnostics);
                return;
            case HpirService.FormatF64:
                double number = Read<double>(frame, call.Arguments[0]);
                Write(frame, destination, FormatNumber(program, ref heap, active, pc, import.Binding, number, diagnostics, counts));
                return;
            case HpirService.ParseF64:
                nint sourceText = Read<nint>(frame, call.Arguments[0]);
                Write(frame, destination, ParseNumber(sourceText, diagnostics, active->Function, pc, counts));
                return;
            case HpirService.ParseBool or HpirService.ParsePathBool or HpirService.ParsePathF64:
                ParseScalarCast(import.Service, Read<nint>(frame, call.Arguments[0]), frame, destination,
                    diagnostics, active->Function, pc);
                return;
            case HpirService.ReadLineText or HpirService.ReadStdinText or HpirService.DemandText or HpirService.TextIsNull or HpirService.TextParseNumber
                or HpirService.ProcessStdout or HpirService.ProcessStderr or HpirService.ProcessTextReady:
                InvokeTextInput(program, ref heap, ref work, active, diagnostics, counts, pc, import, call.Arguments,
                    frame, destination, options, token);
                return;
            case HpirService.WriteText:
                WriteText((HpirTextOutput)import.Binding, call.Arguments.Count == 0 ? 0 : Read<nint>(frame, call.Arguments[0]),
                    active->Function, pc, diagnostics, options, token);
                return;
            case HpirService.Transfer or HpirService.SharedTransfer:
                Transfer(program, ref work, import.Binding, options, token, diagnostics, active->Function, pc,
                    shared: import.Service == HpirService.SharedTransfer);
                return;
            case HpirService.ProcessSpawn:
                CallProcessSpawn(program, ref heap, ref work, active, diagnostics, counts, pc, import, call, frame,
                    destination, options, anchors, token);
                return;
            case HpirService.ProcessControl:
                CallProcessControl(work!, (HpirProcessControl)import.Binding, call, frame, destination,
                    new(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc),
                    options?.DryRun == true, token);
                return;
            case HpirService.FutureDemand or HpirService.FutureLift or HpirService.FutureMath or HpirService.ProcessStart
                or HpirService.ProcessExit or HpirService.ProcessSuccess:
                InvokeFuture(program, ref heap, ref work, active, diagnostics, counts, pc, import,
                    call.Arguments, frame, destination, options, token, anchors);
                return;
            case HpirService.Fail:
                if (import.Binding == (int)HpirFailure.RangeIndex)
                    throw RangeIndexFailure(Read<double>(frame, call.Arguments[0]), diagnostics.Source(active->Function, pc));
                string message = (HpirFailure)import.Binding switch
                {
                    HpirFailure.UnknownCallable => "Function is outside the admitted HPIR call targets.",
                    HpirFailure.RangeStalled => "ranges.sequence step no longer advances the Number value",
                    HpirFailure.RangeEmpty => "ranges.front requires a nonempty Range",
                    HpirFailure.RangeIndexStalled => "ranges.enumerate_from index no longer advances the Number value",
                    _ => "Reached an unreachable HPIR block."
                };
                throw new MsyncException(message, diagnostics.Source(active->Function, pc));
            case HpirService.FailDiagnostic:
                throw new MsyncException(program.TextLiterals[import.Binding], diagnostics.Source(active->Function, pc));
            default:
                throw new MsyncException($"Unsupported HPIR service '{import.Service}'", diagnostics.Source(active->Function, pc));
        }
    }

    private static MsyncException RangeIndexFailure(double number, SourceSpan? source)
    {
        if (!double.IsFinite(number)) return new("ranges.at requires finite Number arguments", source);
        ulong index = HpirOrdinal.FromNumber(number);
        return index == HpirOrdinal.Invalid
            ? new("ranges.at requires a nonnegative integral count representable by the platform iterator", source)
            : new($"ranges.at has no item at index {index}", source);
    }

    private static nint Allocate(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        int pc, int layout, int elements, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, bool clear = true)
    {
        heap ??= new(program.Layouts);
        if (heap.WantsCollection)
            Collect(program.Functions, heap, active, pc, layout, diagnostics, counts);
        return AllocatePayload(program, heap, active, pc, layout, elements, diagnostics, counts, clear);
    }

    internal static nint AllocatePayload(HpirProgram program, HpirHeap heap, HpirStack.Frame* active,
        int pc, int layout, int elements, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, bool clear)
    {
        HpirHeap.Allocation allocation;
        try { allocation = heap.Allocate(layout, elements, clear); }
        catch (OutOfMemoryException error)
        {
            throw new MsyncException($"HPIR guest heap could not allocate layout {layout} ({elements} elements of {program.Layouts[layout].Size} payload bytes)",
                diagnostics.Source(active->Function, pc), error);
        }
        counts?.HeapAllocation((long)program.Layouts[layout].Size * elements, allocation);
        return allocation.Address;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Collect(IReadOnlyList<HpirFunction> functions, HpirHeap heap, HpirStack.Frame* frame,
        int instruction, int layout, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts)
    {
        using RuntimeTimelineScope? trace = diagnostics.BeginCollection(frame->Function, instruction, layout);
        var roots = new RootCensus { Marked = heap.MarkGlobals() };
        trace?.SetMetric("roots.globals", heap.GlobalCount);
        int paths = heap.MarkCompletionPaths();
        roots.Marked += paths;
        if (paths != 0) trace?.SetMetric("roots.active_completion_paths", paths);
        try
        {
            ScanStackRoots(functions, heap, frame, instruction, diagnostics, ref roots);
            int active = roots.Frames;
            heap.Guests?.ScanParked(ref roots);
            if (trace is not null && roots.Frames != active) trace.SetMetric("roots.cooperative_frames", roots.Frames - active);
            if (trace is not null && roots.EffectNumbers != 0) trace.SetMetric("roots.effect_number_marked", roots.EffectNumbers);
            HpirHeap.Sweep sweep = heap.Collect();
            counts?.Collection(roots.Frames, roots.Locations, roots.Borrows, roots.Marked, sweep);
            HpirDiagnostics.EndCollection(trace, roots.Frames, roots.Locations, roots.Borrows, roots.Marked, sweep);
        }
        catch (Exception error)
        {
            HpirDiagnostics.CollectionWork(trace, roots.Frames, roots.Locations, roots.Borrows, roots.Marked);
            trace?.Fail(error);
            throw;
        }
    }

    internal struct RootCensus { internal int Frames, Locations, Borrows, Marked, EffectNumbers; }

    private static void ScanStackRoots(IReadOnlyList<HpirFunction> functions, HpirHeap heap, HpirStack.Frame* frame,
        int instruction, HpirDiagnostics diagnostics, ref RootCensus roots)
    {
        for (; frame != null; instruction = frame->ResumeAt - 1, frame = frame->Parent)
        {
            if (RootMap(functions[frame->Function].Safepoints, instruction) is not { } map)
                throw new MsyncException($"Missing HPIR root map for '{functions[frame->Function].Name}' at {instruction}",
                    diagnostics.Source(frame->Function, instruction));
            byte* data = HpirStack.Data(frame);
            foreach (int offset in map.RootOffsets)
                if (heap.Mark(Unsafe.ReadUnaligned<nint>(data + offset))) roots.Marked++;
            foreach (int offset in map.BorrowOffsets)
                if (heap.MarkBorrow(Unsafe.ReadUnaligned<nint>(data + offset))) roots.Marked++;
            roots.Locations += map.RootOffsets.Length;
            roots.Borrows += map.BorrowOffsets.Length;
            roots.Frames++;
        }
    }

    private static HpirRootMap? RootMap(IReadOnlyList<HpirRootMap> maps, int instruction)
    {
        int left = 0, right = maps.Count;
        while (left < right)
        {
            int middle = left + (right - left) / 2;
            HpirRootMap map = maps[middle];
            if (map.Instruction == instruction) return map;
            if (map.Instruction < instruction) left = middle + 1;
            else right = middle;
        }
        return null;
    }
}
