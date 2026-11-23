using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static int CallLiveTransfer(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc,
        HpirImport import, HpirCall call, byte* frame, int destination, ExecutionOptions? options, CancellationToken token,
        BindingContext? binding, HpirTransferTextInput number = default)
    {
        var (text, layout) = HpirLiveTransferImports.Decode(import.Binding);
        NativePathValue target = PathOperand(frame, call.Arguments[^1], false, default).ToHost(contextual: true);
        NativePathValue? source = text ? null : PathOperand(frame, call.Arguments[0], false, default).ToHost(contextual: true);
        string? selection = import.Service == HpirService.LiveTransferSelection
            ? program.TextLiterals[Unsafe.ReadUnaligned<int>((byte*)Read<nint>(frame, call.Arguments[0]) + HpirPathSelectionImports.PatternOffset)]
            : null;
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        work ??= new(token);
        HpirTransferTextInput content = !text ? default : import.Service switch
        {
            HpirService.LiveTransferFutureText => BorrowTransferText(work, Read<nint>(frame, call.Arguments[0])),
            HpirService.LiveTransferBoolText => HpirFuture.BorrowBoolText(Read<nint>(frame, call.Arguments[0]), work),
            HpirService.LiveTransferNumberText => number,
            _ => new(CopyText(Read<nint>(frame, call.Arguments[0])))
        };
        nint cell = layout < 0 ? 0 : Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
        var launched = work.SubmitLiveTransfer(source, content, target, options, binding, site, selection);
        if (layout >= 0)
        {
            HpirFuture.PublishBool(cell, launched);
            Write(frame, destination, cell);
        }
        work.TrackTransfer(launched.JobId, (nint)active, cell, heap);
        return launched.JobId;
    }

    private static void CallPathSelection(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        HpirCall call, byte* frame, int destination)
    {
        HpirPathValue path = PathOperand(frame, call.Arguments[0], false, default);
        int pattern = Read<int>(frame, call.Arguments[1]);
        nint plan = Allocate(program, ref heap, active, pc, import.Binding, 1, diagnostics, counts, clear: false);
        Unsafe.WriteUnaligned((void*)plan, path);
        Unsafe.WriteUnaligned((byte*)plan + HpirPathSelectionImports.PatternOffset, pattern);
        int recipe = call.Arguments.Count == 3 ? Read<int>(frame, call.Arguments[2]) : -1;
        Unsafe.WriteUnaligned((byte*)plan + HpirPathSelectionImports.RecipeOffset, recipe);
        Write(frame, destination, plan);
    }

    private static HpirTransferTextInput BorrowTransferText(HpirWork work, nint address)
    {
        if (address == 0) return new(null);
        var completion = *(HpirTextCompletion*)address;
        return completion.Kind switch
        {
            HpirTextCompletionKind.Ready => new(completion.Text == 0 ? null : CopyText(completion.Text)),
            HpirTextCompletionKind.Input or HpirTextCompletionKind.FileRead => new(null, work.UseInEffect<string?>(completion.Job)),
            HpirTextCompletionKind.Concat => BorrowConcatTransferText(work, address),
            _ => new(null, Process: work.UseProcessInEffect(((HpirProcessHandle*)completion.Dependency)->Job),
                Stderr: completion.Kind == HpirTextCompletionKind.ProcessStderr)
        };
    }
}
