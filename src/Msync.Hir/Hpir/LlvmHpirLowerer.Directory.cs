using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly bool hasDirectoryPlans = graph.NativeCalls.Values.Any(static name =>
            name is "fs.directory_iterator" or "fs.recursive_directory_iterator");
        private Dictionary<LlvmValue, bool>? directoryPlans;
        private int directoryLayout = -1;
        internal bool HasTextDirectoryPath { get; private set; }

        internal bool? RecursiveDirectory(LlvmFunction target) => graph.NativeCalls.GetValueOrDefault(target) switch
        {
            "fs.directory_iterator" => false, "fs.recursive_directory_iterator" => true, _ => null
        };

        internal bool DirectoryPlan(LlvmValue value)
        {
            if (!hasDirectoryPlans || !value.Type.IsPointerTy()) return false;
            if (value is LlvmCallInstruction { CalledFunction: { } target } && RecursiveDirectory(target) is not null)
                return true;
            directoryPlans ??= [];
            if (directoryPlans.TryGetValue(value, out bool known)) return known;
            directoryPlans.Add(value, false);
            return directoryPlans[value] = RangeInputs(value) is { Count: > 0 } inputs && inputs.All(DirectoryPlan);
        }

        internal int DirectorySourceImport(bool recursive, bool text)
        {
            if (directoryLayout < 0) directoryLayout = AddLayout(HpirDirectoryImports.Plan());
            HasTextDirectoryPath |= text;
            _ = TextLayout();
            return FutureImport(HpirDirectoryImports.Source(directoryLayout, text, recursive));
        }

        internal int DirectoryImport(HpirImport import) => FutureImport(import);
    }

    private sealed partial class Body
    {
        private Dictionary<LlvmCallInstruction, LlvmValue>? directoryCursors;
        private int directorySources, directoryOpens, selectionOpens, directoryPulls, directoryCloses;

        private bool TryDirectoryRange(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target) return false;
            if (geometry.RecursiveDirectory(target) is { } recursive)
            {
                LlvmValue path = call.GetArgument(0);
                bool union = KnownPathUnion(path), text = !union && PathTextOperand(path, call);
                int input = PathCallAddress(call, path, text, union);
                EmitImportCall(call, geometry.DirectorySourceImport(recursive, text), [input]);
                ++directorySources;
                return true;
            }
            if (!geometry.IteratorOperation(target, out var operation) || call.ArgumentCount == 0) return false;
            var first = call.GetArgument(0);
            if (operation == LlvmIteratorOperation.Open)
            {
                bool selection = geometry.SelectionPlan(first);
                if (!selection && !geometry.DirectoryPlan(first)) return false;
                EmitImportCall(call, geometry.DirectoryImport(HpirDirectoryImports.Open(selection)), [Location(first)], safepoint: false);
                (directoryCursors ??= []).Add(call, first);
                if (selection) ++selectionOpens;
                else ++directoryOpens;
                return true;
            }
            if (first is not LlvmCallInstruction cursor || directoryCursors is not { } cursors || !cursors.TryGetValue(cursor, out var plan))
                return false;
            if (operation == LlvmIteratorOperation.Close)
            {
                EmitImportCall(call, geometry.DirectoryImport(HpirDirectoryImports.Close()), [Location(first)], safepoint: false);
                ++directoryCloses;
                return true;
            }
            if (operation != LlvmIteratorOperation.Next || call.GetArgument(1) is not LlvmAllocaInstruction item
                || !Geometry.IsPathRecord(item.AllocatedType)) return false;
            EmitImportCall(call, geometry.DirectoryImport(HpirDirectoryImports.Next()), [Location(first), Location(plan), Location(item)]);
            ++directoryPulls;
            return true;
        }

        private void ReportDirectories(CompilationTrace? trace)
        {
            trace?.Metric("range.directory_sources", directorySources);
            trace?.Metric("range.directory_opens", directoryOpens);
            if (selectionOpens != 0) trace?.Metric("range.path_selection_opens", selectionOpens);
            trace?.Metric("range.directory_pulls", directoryPulls);
            trace?.Metric("range.directory_closes", directoryCloses);
        }
    }
}
