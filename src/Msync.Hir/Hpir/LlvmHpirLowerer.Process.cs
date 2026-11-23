using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal bool HasTextProcessSpawn { get; private set; }

        internal bool IsProcessSpawn(LlvmFunction target) => graph.NativeCalls.GetValueOrDefault(target) == "process.spawn";

        internal HpirProcessControl ProcessControl(LlvmFunction target) =>
            graph.NativeCalls.TryGetValue(target, out var name) ? HpirProcessLibraryImports.Control(name) : HpirProcessControl.Invalid;

        internal int ProcessSpawnImport(HpirProcessArgument items, bool textPath)
        {
            HasTextProcessSpawn |= textPath;
            int layout = FutureLayout(-(int)HpirService.ProcessStart, HpirFutureImports.ProcessHandle());
            return FutureImport(HpirProcessLibraryImports.Spawn(layout, items, textPath));
        }

        internal int ProcessControlImport(HpirProcessControl operation) => FutureImport(HpirProcessLibraryImports.Invoke(operation));

        internal bool ReadyProcessText(LlvmCallInstruction projection) =>
            projection.CalledFunction is { } target && graph.CompletedProcessText?.Contains(target) == true
            || projection.GetArgument(0) is LlvmCallInstruction { CalledFunction: { } origin }
                && (ProcessControl(origin) == HpirProcessControl.Wait || ProcessStart(origin, out int scheduling) && (scheduling & 1) == 0);

        internal int ReadyProcessTextImport(bool stderr)
        {
            _ = TextLayout();
            return FutureImport(new(HpirService.ProcessTextReady, stderr ? 1 : 0, HpirWidth.U64, [HpirWidth.U64]));
        }
    }

    private sealed partial class Body
    {
        private int processSpawns, processControls;

        private bool TryProcessLibrary(LlvmCallInstruction call, LlvmFunction target)
        {
            if (geometry.IsProcessSpawn(target)) return TrySpawnVector(call);
            var operation = geometry.ProcessControl(target);
            if (operation == HpirProcessControl.Invalid) return false;
            LlvmValue handle = call.GetArgument(0);
            if (processHandles?.Contains(handle) != true)
                throw Error("a process value crossing an unfinished control carrier boundary", call);
            int process = Location(handle);
            int[] arguments = operation == HpirProcessControl.WriteInput ? [process, ProcessInput(call)] : [process];
            EmitImportCall(call, geometry.ProcessControlImport(operation), arguments);
            ++processControls;
            return true;
        }

        private int ProcessInput(LlvmCallInstruction call)
        {
            LlvmValue input = call.GetArgument(1);
            if (!TextCell(input)) return Location(input);
            return ObserveText(call, input);
        }

        private bool TrySpawnVector(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 2) return false;
            var argv = call.GetArgument(1);
            if ((geometry.SelectedSnapshot(call, 1) ?? geometry.VectorShape(argv)) is not { } shape) return false;
            HpirProcessArgument kind = shape.Empty ? HpirProcessArgument.Invalid : geometry.ReflectedType(call, 1)?.Element?.Kind switch
            {
                TypeKind.String when geometry.IsGuestPointer(shape.Element) => HpirProcessArgument.Text,
                TypeKind.Path when Geometry.IsPathRecord(shape.Element) => HpirProcessArgument.Path,
                TypeKind.Number when shape.Element.IsDoubleTy() => HpirProcessArgument.Number,
                TypeKind.Bool when shape.Element.IsIntegerTy(1) => HpirProcessArgument.Bool,
                _ => HpirProcessArgument.Invalid
            };
            if (kind == HpirProcessArgument.Invalid && !shape.Empty) return false;
            if (kind == HpirProcessArgument.Path) geometry.CheckPathRecord(shape.Element, call.Source);

            var path = call.GetArgument(0);
            bool union = KnownPathUnion(path), text = !union && PathTextOperand(path, call);
            int executable = PathCallAddress(call, path, text, union);
            int count = Reserve(8, 8), data = Reserve(8, 8);
            if (shape.Empty)
            {
                Add(new(HpirOp.ConstU64, count), call.Source);
                Add(new(HpirOp.ConstU64, data), call.Source);
            }
            else
            {
                int vector = Location(argv);
                Copy(HpirWidth.U64, count, ~vector, call.Source);
                Copy(HpirWidth.U64, data, Indirect(vector, HpirTransformImports.VectorData, call), call.Source);
            }
            EmitImportCall(call, geometry.ProcessSpawnImport(kind, text), [executable, count, data]);
            ++processSpawns;
            return true;
        }
    }
}
