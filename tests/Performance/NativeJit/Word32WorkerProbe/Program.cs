using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Msync.Runtime;
using Op = Msync.Optimization.Execution.NativeWorkerOp;
using Rep = Msync.Optimization.Execution.NativeWorkerType;

Directory.CreateDirectory(".temp/native-word32");

foreach (string abi in new[] { "sysv", "ms", "arm" })
{
    foreach (var i in Enum.GetValues<NumericIntrinsic>().Where(i => i is >= NumericIntrinsic.And and <= NumericIntrinsic.ReverseBytes))
    {
        NativeWorkerPlan plan = new(
            [new(Op.Word32Intrinsic, 2, 0, 1, Intrinsic: i), new(Op.Return, Left: 2)],
            [Rep.Word32, Rep.Word32, Rep.Word32, Rep.Word32], [0, 1], 3);
        Write(abi, i.ToString(), plan);
    }

    Write(abi, "unsigned", new(
        [new(Op.Word32ToNumber, 1, 0), new(Op.Return, Left: 1)],
        [Rep.Word32, Rep.Number, Rep.Number], [0], 2));

    Write(abi, "walk", new(
        [
            new(Op.Word32Constant, 1, Data: 7),
            new(Op.LoadIndex, 2, 0),
            new(Op.Word32Intrinsic, 2, 2, 1, Intrinsic: NumericIntrinsic.RotateLeft),
            new(Op.StoreIndex, Left: 0, Right: 2),
            new(Op.LoadIndex, 0, 0, Data: 4),
            new(Op.IndexEqual, 4, 0, 3),
            new(Op.BranchIfFalse, Left: 4, Data: 1),
            new(Op.Word32Constant, 2, Data: unchecked((int)0xdeadbeef)),
            new(Op.Return, Left: 2),
        ],
        [Rep.Index, Rep.Word32, Rep.Word32, Rep.Index, Rep.Bool, Rep.Word32],
        [0, 3], 5, 8), counted: true);
}

static void Write(string abi, string name, NativeWorkerPlan plan, bool counted = false)
    => File.WriteAllBytes($".temp/native-word32/{abi}-{name}.bin", NativeWorker.EmitForTest(plan, counted, abi));

namespace Msync.Runtime
{
    internal static partial class NativeWorker
    {
        internal static byte[] EmitForTest(NativeWorkerPlan plan, bool counted, string abi) => abi == "arm"
            ? new Arm64Emitter(plan, counted).Emit()!
            : new X64Emitter(plan, counted, windows: abi == "ms").Emit();

        private static int[] ResumeTargets(NativeWorkerPlan plan) =>
            plan.Instructions.Select((op, pc) => (op, pc))
                .Where(i => i.op.Data <= i.pc && i.op.Op is Op.Jump or Op.BranchIfFalse or Op.BranchIfTrue)
                .Select(i => i.op.Data).Distinct().Order().ToArray();
    }
}
