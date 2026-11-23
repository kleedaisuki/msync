using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void InvokeTextInput(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc,
        HpirImport import, IReadOnlyList<int> arguments, byte* frame, int destination, ExecutionOptions? options,
        CancellationToken token)
    {
        nint completion;
        switch (import.Service)
        {
            case HpirService.ReadLineText or HpirService.ReadStdinText:
                work ??= new(token);
                completion = Allocate(program, ref heap, active, pc, import.Binding, 1, diagnostics, counts, clear: false);
                var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
                TextReader reader = options?.Input ?? Console.In;
                bool fullInput = import.Service == HpirService.ReadStdinText;
                HpirHostResult<string?> source = work.Submit<string?>(HpirWorkKind.Input,
                    cancellation => fullInput ? ReadRemainingInput(reader, site, cancellation)
                        : StandardTextInput.ReadLine(reader, site.Timeline, cancellation), site);
                var context = new HpirTextDemandContext(program, heap!, active, pc, diagnostics, counts);
                // The call already collected before reserving the cell. Neither result is published yet.
                *(HpirTextCompletion*)completion = source.Ready
                    ? new(source.Value is { } line ? context.Publish(line, collect: false) : 0)
                    : new(0, Job: source.JobId, Kind: HpirTextCompletionKind.Input);
                Write(frame, destination, completion);
                if (source.JobId != 0 && work.ScopeFrame(active) is var owner && owner != 0)
                    work.TrackGuestJob(source.JobId, owner, completion, heap!);
                return;
            case HpirService.ProcessStdout or HpirService.ProcessStderr:
                nint handle = Read<nint>(frame, arguments[0]);
                completion = Allocate(program, ref heap, active, pc, import.Binding, 1, diagnostics, counts, clear: false);
                *(HpirTextCompletion*)completion = new(0, handle, Kind: import.Service == HpirService.ProcessStdout
                    ? HpirTextCompletionKind.ProcessStdout : HpirTextCompletionKind.ProcessStderr);
                Write(frame, destination, completion);
                return;
            case HpirService.ProcessTextReady:
                var process = work!.CompletedProcess(((HpirProcessHandle*)Read<nint>(frame, arguments[0]))->Job);
                string output = import.Binding == 0 ? process.StandardOutput : process.StandardError;
                var ready = new HpirTextDemandContext(program, heap!, active, pc, diagnostics, counts);
                Write(frame, destination, ready.Publish(output, collect: true));
                return;
            case HpirService.DemandText:
                completion = Read<nint>(frame, arguments[0]);
                var demand = new HpirTextDemandContext(program, heap!, active, pc, diagnostics, counts);
                Write(frame, destination, demand.Demand(completion, work, null, token));
                return;
            default:
                completion = Read<nint>(frame, arguments[0]);
                nint scalar = Allocate(program, ref heap, active, pc, import.Binding, 1, diagnostics, counts, clear: false);
                if (import.Service == HpirService.TextIsNull) HpirFuture.PublishTextIsNull(scalar, completion);
                else HpirFuture.PublishTextNumber(scalar, completion, active->Function, pc);
                Write(frame, destination, scalar);
                return;
        }
    }

    private static void CallFileText(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts,
        int pc, HpirImport import, IReadOnlyList<int> arguments, byte* frame, int destination,
        ExecutionOptions? options, CancellationToken token, HpirPathAnchors anchors)
    {
        var path = PathOperand(frame, arguments[0], (import.Binding & 1) != 0, anchors).ToHost(contextual: true);
        nint cell = Allocate(program, ref heap, active, pc, import.Binding >> 1, 1, diagnostics, counts, clear: false);
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        work ??= new(token);
        var source = work.SubmitFileText(path, options, anchors.Binding(program.Context), site);
        *(HpirTextCompletion*)cell = source.Ready
            ? new(new HpirTextDemandContext(program, heap!, active, pc, diagnostics, counts).Publish(source.Value!, collect: false))
            : new(0, Job: source.JobId, Kind: HpirTextCompletionKind.FileRead);
        Write(frame, destination, cell);
        if (source.JobId != 0) work.TrackGuestJob(source.JobId, work.ScopeFrame(active), cell, heap!);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct HpirTextCompletion(nint Text, nint Dependency = 0, int Job = 0,
    HpirTextCompletionKind Kind = HpirTextCompletionKind.Ready);

internal enum HpirTextCompletionKind : byte { Ready, Input, ProcessStdout, ProcessStderr, FileRead, Concat }

internal readonly unsafe struct HpirTextDemandContext(HpirProgram program, HpirHeap heap, HpirStack.Frame* active,
    int pc, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts)
{
    internal HpirStack.Frame* Active => active;
    internal int Instruction => pc;
    internal HpirHeap Heap => heap;

    internal Task? Waiting(nint pointer, HpirWork? work)
    {
        if (pointer == 0) return null;
        var completion = (HpirTextCompletion*)pointer;
        return completion->Kind switch
        {
            HpirTextCompletionKind.Ready => null,
            HpirTextCompletionKind.Input or HpirTextCompletionKind.FileRead => work!.Waiting(completion->Job),
            HpirTextCompletionKind.Concat => HpirExecutor.WaitTextConcat(pointer, this, work),
            _ => work!.Waiting(((HpirProcessHandle*)completion->Dependency)->Job)
        };
    }

    internal nint Demand(nint pointer, HpirWork? work, RuntimeTimelineScope? trace, CancellationToken token)
    {
        if (pointer == 0) return 0;
        var completion = (HpirTextCompletion*)pointer;
        HpirTextCompletionKind kind = completion->Kind;
        if (kind == HpirTextCompletionKind.Ready) return completion->Text;
        using RuntimeTimelineScope? ownTrace = diagnostics.BeginTextDemand(active->Function, pc);
        try
        {
            RuntimeTimelineScope? observation = ownTrace ?? trace;
            observation?.SetAttribute("text.completion", kind.ToString());
            if (kind == HpirTextCompletionKind.Concat)
                return HpirExecutor.DemandTextConcat(pointer, this, work, observation, token);
            string? contents = kind is HpirTextCompletionKind.Input or HpirTextCompletionKind.FileRead
                ? work!.Demand<string?>(completion->Job, token, observation)
                : ProcessText(HpirProcessHandle.Demand(completion->Dependency, work!, token, observation), kind);
            if (completion->Kind == HpirTextCompletionKind.Ready) return completion->Text;
            nint text = contents is null ? 0 : Publish(contents, collect: true);
            *completion = new(text);
            observation?.SetMetric("text.characters", contents?.Length ?? 0);
            if (kind == HpirTextCompletionKind.Input)
                observation?.SetMetric("input.characters", contents?.Length ?? 0);
            return text;
        }
        catch (Exception error)
        {
            ownTrace?.Fail(error);
            throw;
        }
    }

    private static string ProcessText(Msync.Core.Native.NativeProcess process, HpirTextCompletionKind kind) =>
        kind == HpirTextCompletionKind.ProcessStdout ? process.StandardOutput : process.StandardError;

    internal nint Publish(string text, bool collect)
    {
        nint pointer = AllocateText(text.Length, collect);
        HpirTextStorage.Write(pointer, text.AsSpan());
        return pointer;
    }

    internal nint ReserveText(int length, out Span<char> destination)
    {
        nint pointer = AllocateText(length, collect: true);
        destination = HpirTextStorage.Initialize(pointer, length);
        return pointer;
    }

    private nint AllocateText(int length, bool collect)
    {
        int layout = program.TextLayout;
        if (collect && heap.WantsCollection)
            HpirExecutor.Collect(program.Functions, heap, active, pc, layout, diagnostics, counts);
        int elements = HpirTextStorage.Elements(length);
        return HpirExecutor.AllocatePayload(program, heap, active, pc, layout, elements, diagnostics, counts, clear: false);
    }

    internal double Parse(nint text, int function, int instruction) =>
        HpirExecutor.ParseNumber(text, diagnostics, function, instruction, counts);

    internal MsyncException Attribute(MsyncException error, int function, int instruction) => function < 0
        ? error : new MsyncException(error.Message, diagnostics.Source(function, instruction), error);
}
