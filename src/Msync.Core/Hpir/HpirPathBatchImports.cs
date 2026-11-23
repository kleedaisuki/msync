namespace Msync.Core.Hpir;

public static class HpirPathBatchImports
{
    public const int Snapshot = 0, Environment = 8;

    public static HpirImport Invoke(int worker, bool observed) => new(HpirService.LivePathBatch, worker,
        observed ? HpirWidth.U64 : HpirWidth.Void, [HpirWidth.U64, HpirWidth.U64, HpirWidth.U32, HpirWidth.U32]);

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        if ((uint)import.Binding >= (uint)program.Functions.Count
            || import.Result is not (HpirWidth.U64 or HpirWidth.Void)
            || !import.Arguments.AsSpan().SequenceEqual([HpirWidth.U64, HpirWidth.U64, HpirWidth.U32, HpirWidth.U32]))
            return false;
        HpirFunction worker = program.Functions[import.Binding];
        return worker.Result == HpirWidth.Void && worker.Parameters.Count == 2
            && worker.Parameters[0].Width == HpirWidth.U64 && worker.Parameters[1].Width == HpirWidth.U64;
    }
}
