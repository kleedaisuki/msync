using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<LlvmValue, bool?> liveTextPlans = [];
        private readonly Dictionary<(HpirService, int), int> liveTextImports = [];
        private readonly bool hasLiveText = graph.FileLineSources != 0
            || graph.NativeCalls.Values.Any(static name => name is "io.stdin_lines" or "io.read_lines");
        private int stdinPlanLayout = -1, filePlanLayout = -1;

        internal bool LiveTextFileAnchors => filePlanLayout >= 0;
        internal bool HasLiveText => hasLiveText;

        internal HpirTextRangeSource? LiveTextSource(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target || !graph.NativeCalls.TryGetValue(target, out var name)) return null;
            if (name == "io.stdin_lines" && call.ArgumentCount == 0) return HpirTextRangeSource.Stdin;
            if (name != "io.read_lines" || call.ArgumentCount != 1) return null;
            return IsPathRecord(call.GetArgument(0).Type) ? HpirTextRangeSource.FilePath
                : StringValue(call.GetArgument(0)) ? HpirTextRangeSource.FileText : null;
        }

        // File and standard input use a different physical plan. Source kind is
        // retained at the producer and its direct returns, not recovered from a VM value.
        internal bool? LiveTextPlan(LlvmValue value)
        {
            if (value is LlvmFileInInst { Format: LlvmFileInFormat.Lines }) return true;
            if (!HasLiveText || value is not LlvmCallInstruction { CalledFunction: { } target } call || !value.Type.IsPointerTy())
                return null;
            if (liveTextPlans.TryGetValue(value, out bool? file)) return file;
            liveTextPlans.Add(value, null);
            if (LiveTextSource(call) is { } source)
                return liveTextPlans[value] = source != HpirTextRangeSource.Stdin;
            if (target.IsDeclaration) return null;

            foreach (var block in target.BasicBlocks)
            {
                if (block.LastInstruction is not LlvmReturnInstruction ret) continue;
                if (ret.ReturnValue is not { } returned || LiveTextPlan(returned) is not { } kind
                    || file is not null && file != kind) return null;
                file = kind;
            }
            return liveTextPlans[value] = file;
        }

        internal int LiveTextSourceImport(HpirTextRangeSource source)
        {
            ref int layout = ref (source == HpirTextRangeSource.Stdin ? ref stdinPlanLayout : ref filePlanLayout);
            if (layout < 0)
            {
                layout = Layouts.Count;
                Layouts.Add(HpirTextRangeImports.Plan(source));
            }
            _ = TextLayout();
            return LiveTextImport(HpirTextRangeImports.Source(source, layout));
        }

        internal int LiveTextImport(HpirImport import)
        {
            var key = (import.Service, import.Binding);
            if (liveTextImports.TryGetValue(key, out int index)) return index;
            index = Imports.Count;
            Imports.Add(import);
            liveTextImports.Add(key, index);
            return index;
        }
    }

    private sealed partial class Body
    {
        private HashSet<LlvmCallInstruction>? liveTextCursors;
        private int stdinTextSites, fileTextSites, liveTextOpens, liveTextNexts, liveTextCloses, liveTextJoins;

        private bool TryJoinLiveText(LlvmCallInstruction call, LlvmValue input)
        {
            if (geometry.LiveTextPlan(input) is not { } file || FutureRoot(input)) return false;
            _ = geometry.TextLayout();
            EmitImportCall(call, geometry.LiveTextImport(HpirTextRangeImports.Join(file)),
                [Location(input), Location(call.GetArgument(1))]);
            ++liveTextJoins;
            return true;
        }

        private bool TryLiveTextRange(LlvmCallInstruction call)
        {
            if (!geometry.HasLiveText || call.CalledFunction is not { } target) return false;
            if (geometry.IsTransformTake(call) && geometry.LiveTextWindow(call) is not null)
            {
                EmitLiveTextWindow(call);
                return true;
            }
            if (geometry.LiveTextSource(call) is { } source)
            {
                if (source == HpirTextRangeSource.Stdin)
                {
                    EmitImportCall(call, geometry.LiveTextSourceImport(source), []);
                    stdinTextSites++;
                }
                else EmitTextFileSource(call, call.GetArgument(0));
                return true;
            }
            if (!geometry.IteratorOperation(target, out var operation)) return false;
            var first = call.GetArgument(0);
            if (operation == LlvmIteratorOperation.Open && geometry.LiveTextWindow(first) is { } windowFile)
            {
                OpenLiveTextWindow(call, first, windowFile);
                return true;
            }
            if (first is LlvmCallInstruction open && textWindowCursors?.TryGetValue(open, out var window) == true)
            {
                if (operation == LlvmIteratorOperation.Close) CloseLiveTextWindow(call, open);
                else if (operation == LlvmIteratorOperation.Next && call.GetArgument(1) is LlvmAllocaInstruction output
                    && geometry.IsGuestPointer(output.AllocatedType)) PullLiveTextWindow(call, open, output, window);
                else return false;
                return true;
            }
            if (operation == LlvmIteratorOperation.Open && geometry.LiveTextPlan(first) is { } file)
            {
                EmitImportCall(call, geometry.LiveTextImport(HpirTextRangeImports.Open(file)), [Location(first)], safepoint: false);
                (liveTextCursors ??= []).Add(call);
                liveTextOpens++;
                return true;
            }
            if (!LiveTextCursor(first)) return false;
            if (operation == LlvmIteratorOperation.Next && call.GetArgument(1) is LlvmAllocaInstruction item
                && geometry.IsGuestPointer(item.AllocatedType))
            {
                EmitImportCall(call, geometry.LiveTextImport(HpirTextRangeImports.Next()), [Location(first), Location(item)]);
                liveTextNexts++;
                return true;
            }
            if (operation != LlvmIteratorOperation.Close) return false;
            EmitImportCall(call, geometry.LiveTextImport(HpirTextRangeImports.Close()), [Location(first)], safepoint: false);
            liveTextCloses++;
            return true;
        }

        private void EmitTextFileSource(LlvmInstruction origin, LlvmValue input)
        {
            if (FutureRoot(input)) throw Error("a text file Range without a ready source", origin);
            bool path = Geometry.IsPathRecord(input.Type);
            if (!path && !geometry.StringValue(input)) throw Error("a text file Range without a Path or String source", origin);
            int argument = path ? PathRecordAddress(input, origin) : Location(input);
            int imported = geometry.LiveTextSourceImport(path ? HpirTextRangeSource.FilePath : HpirTextRangeSource.FileText);
            EmitCall(origin, ~imported, Location(origin), [argument]);
            fileTextSites++;
        }

        private bool LiveTextCursor(LlvmValue value) =>
            value is LlvmCallInstruction { CalledFunction: { } target } call
            && geometry.IteratorOperation(target, out var operation) && operation == LlvmIteratorOperation.Open
            && geometry.LiveTextPlan(call.GetArgument(0)) is not null;

        private void ReportLiveTextRange(CompilationTrace? trace)
        {
            trace?.Metric("range.text_stdin_sources", stdinTextSites);
            trace?.Metric("range.text_file_sources", fileTextSites);
            trace?.Metric("range.text_cursor_opens", liveTextOpens);
            trace?.Metric("range.text_cursor_nexts", liveTextNexts);
            trace?.Metric("range.text_cursor_closes", liveTextCloses);
            trace?.Metric("range.text_windows", textWindows);
            trace?.Metric("strings.join.live_cursors", liveTextJoins);
        }
    }
}
