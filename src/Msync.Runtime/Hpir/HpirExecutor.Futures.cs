using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Native;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void InvokeFuture(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts,
        int pc, HpirImport import, IReadOnlyList<int> arguments, byte* frame, int destination,
        ExecutionOptions? options, CancellationToken token, HpirPathAnchors anchors)
    {
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        switch (import.Service)
        {
            case HpirService.FutureDemand:
                DemandFuture(import.Result, frame, destination, arguments, work, site, token,
                    new(program, heap!, active, pc, diagnostics, counts));
                return;
            case HpirService.FutureLift:
                LiftFuture(program, ref heap, active, diagnostics, counts, pc, import, arguments, frame, destination, site);
                return;
            case HpirService.FutureMath:
                LiftMathFuture(program, ref heap, active, diagnostics, counts, pc, import, arguments, frame, destination, site);
                return;
            case HpirService.ProcessStart:
                work ??= new(token);
                int id = StartProcess(work, import, frame, arguments, site, anchors.Binding(program.Context), options, token);
                nint handle = Allocate(program, ref heap, active, pc, import.Binding >> 1, 1, diagnostics, counts, clear: false);
                *(HpirProcessHandle*)handle = new(id);
                Write(frame, destination, handle);
                work.TrackGuestJob(id, work.ScopeFrame(active), handle, heap!);
                return;
            default:
                nint process = Read<nint>(frame, arguments[0]);
                nint cell = Allocate(program, ref heap, active, pc, import.Binding, 1, diagnostics, counts, clear: false);
                if (import.Service == HpirService.ProcessExit) HpirFuture.PublishProcessExit(cell, process);
                else HpirFuture.PublishProcessSuccess(cell, process);
                Write(frame, destination, cell);
                return;
        }
    }

    private static void DemandFuture(HpirWidth width, byte* frame, int destination, IReadOnlyList<int> arguments,
        HpirWork? owner, HpirWorkSite site, CancellationToken token, HpirTextDemandContext text)
    {
        nint pointer = Read<nint>(frame, arguments[0]);
        if (width == HpirWidth.U64)
            Write(frame, destination, pointer == 0 ? Read<double>(frame, arguments[1])
                : HpirFuture.Demand<double>(pointer, owner, site, token, text));
        else
            Write(frame, destination, pointer == 0 ? Read<byte>(frame, arguments[1])
                : HpirFuture.Demand<byte>(pointer, owner, site, token, text));
    }

    private static void LiftFuture(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        IReadOnlyList<int> arguments, byte* frame, int destination, HpirWorkSite site)
    {
        var (operation, leftMode, rightMode, layout) = HpirFutureImports.DecodeLift(import.Binding);
        HpirFutureImports.TryShape(operation, out int arity, out var width, out var scalar);
        int index = 0;
        nint readyOutput = HpirFutureImports.IsMixedResult(leftMode, rightMode) ? Read<nint>(frame, arguments[index++]) : 0;
        nint pointer;
        if (width == HpirWidth.U64)
        {
            var left = FutureInput<double>(frame, arguments, leftMode, ref index);
            var right = arity == 1 ? default : FutureInput<double>(frame, arguments, rightMode, ref index);
            if (left.Pending == 0 && right.Pending == 0)
            {
                if (scalar == HpirWidth.U64) Unsafe.WriteUnaligned((void*)readyOutput, HpirFuture.EvaluateNumber(operation, left.Value, right.Value));
                else Unsafe.WriteUnaligned((void*)readyOutput, HpirFuture.EvaluateComparison(operation, left.Value, right.Value));
                pointer = 0;
            }
            else
            {
                pointer = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
                if (scalar == HpirWidth.U64) HpirFuture.LiftNumber(pointer, operation, left, right, site);
                else HpirFuture.LiftComparison(pointer, operation, left, right, site);
            }
        }
        else
        {
            var left = FutureInput<byte>(frame, arguments, leftMode, ref index);
            var right = arity == 1 ? default : FutureInput<byte>(frame, arguments, rightMode, ref index);
            if (left.Pending == 0 && right.Pending == 0)
            {
                Unsafe.WriteUnaligned((void*)readyOutput, HpirFuture.EvaluateBool(operation, left.Value, right.Value));
                pointer = 0;
            }
            else
            {
                pointer = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
                HpirFuture.LiftBool(pointer, operation, left, right, site);
            }
        }
        Write(frame, destination, pointer);
    }

    private static HpirScalarInput<T> FutureInput<T>(byte* frame, IReadOnlyList<int> arguments,
        HpirFutureInput mode, ref int index) where T : unmanaged
    {
        if (mode == HpirFutureInput.Ready) return HpirScalarInput<T>.Ready(Read<T>(frame, arguments[index++]));
        nint pointer = Read<nint>(frame, arguments[index++]);
        if (mode == HpirFutureInput.Deferred) return HpirScalarInput<T>.Deferred(pointer);
        int payload = arguments[index++];
        return pointer == 0 ? HpirScalarInput<T>.Ready(Read<T>(frame, payload)) : HpirScalarInput<T>.Deferred(pointer);
    }

    private static void LiftMathFuture(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        IReadOnlyList<int> arguments, byte* frame, int destination, HpirWorkSite site)
    {
        var binding = HpirFutureMathImports.Decode(import.Binding);
        var (arity, payload) = HpirFutureMathImports.Shape(binding.Operation);
        int index = 0;
        nint ready = binding.Mixed ? Read<nint>(frame, arguments[index++]) : 0;
        var a = FutureInput<double>(frame, arguments, binding.First, ref index);
        var b = arity < 2 ? default : FutureInput<double>(frame, arguments, binding.Second, ref index);
        var c = arity < 3 ? default : FutureInput<double>(frame, arguments, binding.Third, ref index);
        if ((a.Pending | b.Pending | c.Pending) == 0)
        {
            if (payload == HpirWidth.U8)
                Unsafe.WriteUnaligned((void*)ready, ScalarMath.Predicate(binding.Operation, a.Value) ? (byte)1 : (byte)0);
            else
                Unsafe.WriteUnaligned((void*)ready, ScalarMath.Evaluate(binding.Operation, a.Value, b.Value, c.Value));
            Write(frame, destination, (nint)0);
            return;
        }
        nint pointer = Allocate(program, ref heap, active, pc, binding.Layout, 1, diagnostics, counts, clear: false);
        HpirFuture.LiftMath(pointer, binding.Operation, arity, payload, a, b, c, site);
        Write(frame, destination, pointer);
    }

    private static int StartProcess(HpirWork work, HpirImport import, byte* frame, IReadOnlyList<int> arguments,
        HpirWorkSite site, BindingContext? binding, ExecutionOptions? options, CancellationToken token)
    {
        var context = new NativeRuntimeContext(options, binding);
        var path = ReadProcessPath(frame, arguments[0]);
        var argv = PrepareProcessArguments(work, import.ProcessArguments, frame, arguments, path, context, site,
            out var pending);
        bool release = (import.Binding & 1) != 0;
        // Admission owns copied host lanes immediately. Only native startup waits for argv.
        int job = pending is null ? AdmitProcess(work, path, argv, context, release, pipeInput: false, site)
            : AdmitProcessWithText(work, path, argv, pending, context, release, site);
        if (!release)
        {
            using RuntimeTimelineScope? trace = site.Begin("hpir.process.synchronous_wait");
            _ = work.DemandProcess(job, completed: true, token, trace);
        }
        return job;
    }

    private static string[] PrepareProcessArguments(HpirWork work, HpirProcessArgument[] shape, byte* frame,
        IReadOnlyList<int> arguments, NativePathValue executable, NativeRuntimeContext context, HpirWorkSite site,
        out List<PendingProcessText>? pending)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.process.argv");
        trace?.SetAttribute("process.executable", executable.ToString());
        trace?.SetMetric("arguments", shape.Length);
        pending = null;
        var argv = new string[shape.Length];
        var names = new ProcessArgumentContext(executable, context);
        for (int index = 0; index < argv.Length; ++index)
        {
            int argument = arguments[index + 1];
            byte* value = Address(frame, argument);
            HpirProcessArgument kind = shape[index];
            if (kind is HpirProcessArgument.Path or HpirProcessArgument.EitherText)
                value = (byte*)Unsafe.ReadUnaligned<nint>(value);
            if (kind == HpirProcessArgument.EitherText)
                kind = value[HpirFutureImports.ProcessTextTag] == 0 ? HpirProcessArgument.Text : HpirProcessArgument.PendingText;
            if (kind == HpirProcessArgument.PendingText)
            {
                var input = BorrowTransferText(work, Unsafe.ReadUnaligned<nint>(value));
                (pending ??= []).Add(new(index, input));
                continue;
            }
            argv[index] = names.Format(kind, value);
        }
        if (pending is not null) trace?.SetMetric("arguments.pending_text", pending.Count);
        return argv;
    }

    private static NativePathValue ReadProcessPath(byte* frame, int offset) =>
        Unsafe.ReadUnaligned<HpirPathValue>((void*)Read<nint>(frame, offset)).ToHost(contextual: true);

    private static string CopyText(nint address) => HpirTextStorage.Read(address).ToString();
}

internal readonly unsafe record struct HpirProcessHandle(int Job)
{
    internal static NativeProcess Demand(nint handle, HpirWork work, CancellationToken token, RuntimeTimelineScope? trace) =>
        work.DemandProcess(((HpirProcessHandle*)handle)->Job, completed: true, token, trace);
}
