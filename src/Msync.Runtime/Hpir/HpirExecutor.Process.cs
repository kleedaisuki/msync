using System.Globalization;
using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static int AdmitProcess(HpirWork work, NativePathValue path, string[] argv, NativeRuntimeContext context,
        bool release, bool pipeInput, HpirWorkSite site) => work.SubmitProcess(cancellation => release
            ? new(Task.Run(() => NativeHostIO.StartTextProcessAsync(path, argv, context, pipeInput, cancellation).AsTask()))
            : NativeHostIO.StartTextProcessAsync(path, argv, context, pipeInput, cancellation), site);

    private readonly struct ProcessArgumentContext(NativePathValue executable, NativeRuntimeContext context)
    {
        private readonly bool remote = executable.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl;

        internal string Format(HpirProcessArgument kind, byte* value) => kind switch
        {
            HpirProcessArgument.Text => CopyText(Unsafe.ReadUnaligned<nint>(value)),
            HpirProcessArgument.Number => Unsafe.ReadUnaligned<double>(value).ToString(CultureInfo.InvariantCulture),
            HpirProcessArgument.Bool => *value != 0 ? "true" : "false",
            HpirProcessArgument.Path => Path(Unsafe.ReadUnaligned<HpirPathValue>(value).ToHost(contextual: true)),
            _ => throw new InvalidOperationException("Unknown HPIR Process argument ABI.")
        };

        private string Path(NativePathValue path) => context.DryRun ? path.ToString() : remote ? path.Raw : context.LocalPath(path);
    }

    private static void CallProcessSpawn(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc,
        HpirImport import, HpirCall call, byte* frame, int destination, ExecutionOptions? options, HpirPathAnchors anchors,
        CancellationToken token)
    {
        var (layout, kind, text) = HpirProcessLibraryImports.DecodeSpawn(import.Binding);
        // Capture the complete argv before host admission; no work borrows guest memory.
        var path = PathOperand(frame, call.Arguments[0], text, anchors).ToHost(contextual: true);
        var context = new NativeRuntimeContext(options, anchors.Binding(program.Context));
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        int length = checked((int)Read<ulong>(frame, call.Arguments[1]));
        string[] argv = VectorProcessArguments(path, context, kind, length, (byte*)Read<nint>(frame, call.Arguments[2]), site);
        work ??= new(token);
        int job = AdmitProcess(work, path, argv, context, release: false, pipeInput: true, site);
        using (RuntimeTimelineScope? trace = site.Begin("hpir.process.start_wait"))
            _ = work.DemandProcess(job, completed: false, token, trace);
        nint handle = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
        *(HpirProcessHandle*)handle = new(job);
        Write(frame, destination, handle);
        work.TrackGuestJob(job, work.ScopeFrame(active), handle, heap!);
    }

    private static string[] VectorProcessArguments(NativePathValue path, NativeRuntimeContext context, HpirProcessArgument kind,
        int length, byte* values, HpirWorkSite site)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.process.argv");
        trace?.SetAttribute("process.executable", path.ToString());
        trace?.SetAttribute("arguments.source", "snapshot");
        trace?.SetMetric("arguments", length);
        if (length == 0) return [];
        int stride = kind switch { HpirProcessArgument.Path => HpirPathImports.PathBytes, HpirProcessArgument.Bool => 1, _ => 8 };
        var format = new ProcessArgumentContext(path, context);
        var argv = new string[length];
        for (int i = 0; i < length; ++i, values += stride) argv[i] = format.Format(kind, values);
        return argv;
    }

    private static void CallProcessControl(HpirWork work, HpirProcessControl operation,
        HpirCall call, byte* frame, int destination, HpirWorkSite site, bool dryRun, CancellationToken token)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.process.control");
        trace?.SetAttribute("operation", HpirProcessLibraryImports.Name(operation));
        try
        {
            if (dryRun && operation >= HpirProcessControl.Kill)
            {
                if (operation is HpirProcessControl.Kill or HpirProcessControl.Terminate) WriteBool(frame, destination, true);
                return;
            }
            var handle = Read<nint>(frame, call.Arguments[0]);
            bool completed = operation == HpirProcessControl.Wait;
            trace?.SetAttribute("process.wait_for", completed ? "completion" : "started");
            var process = work.DemandProcess(((HpirProcessHandle*)handle)->Job, completed, token, trace);
            switch (operation)
            {
                case HpirProcessControl.Pid: Write(frame, destination, (double)NativeHostIO.ProcessId(process)); return;
                case HpirProcessControl.Running: WriteBool(frame, destination, NativeHostIO.ProcessRunning(process)); return;
                case HpirProcessControl.Wait: Write(frame, destination, handle); return;
                case HpirProcessControl.Kill or HpirProcessControl.Terminate:
                    WriteBool(frame, destination, NativeHostIO.StopProcess(process, operation == HpirProcessControl.Kill)); return;
                case HpirProcessControl.WriteInput:
                    WaitProcessControl(work, NativeHostIO.WriteProcessInput(process, CopyText(Read<nint>(frame, call.Arguments[1])), token), token);
                    return;
                case HpirProcessControl.CloseInput:
                    WaitProcessControl(work, NativeHostIO.CloseProcessInput(process, token), token); return;
            }
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            if (error is OperationCanceledException or MsyncException { Span: not null }) throw;
            throw new MsyncException($"{HpirProcessLibraryImports.Name(operation)}: {error.Message}", site.Source, error);
        }
    }

    private static void WaitProcessControl(HpirWork work, ValueTask operation, CancellationToken token)
    {
        if (operation.IsCompletedSuccessfully) return;
        Task wait = operation.AsTask();
        if (!wait.IsCompleted) work.Guests?.PumpHostDemand(wait, token);
        wait.GetAwaiter().GetResult();
    }
}
