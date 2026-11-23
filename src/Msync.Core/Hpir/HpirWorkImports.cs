namespace Msync.Core.Hpir;

public enum HpirWorkOperation : byte { Mark, Publish, Join, JoinProcess }

public static class HpirWorkImports
{
    public static HpirImport Invoke(HpirWorkOperation operation) => operation switch
    {
        HpirWorkOperation.Mark => new(HpirService.Work, (int)operation, HpirWidth.U32, []),
        HpirWorkOperation.Publish => new(HpirService.Work, (int)operation, HpirWidth.Void, [HpirWidth.U32]),
        HpirWorkOperation.Join => new(HpirService.Work, (int)operation, HpirWidth.Void, []),
        HpirWorkOperation.JoinProcess => new(HpirService.Work, (int)operation, HpirWidth.U64, [HpirWidth.U64]),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    internal static bool Valid(HpirImport import)
    {
        if ((uint)import.Binding > (uint)HpirWorkOperation.JoinProcess) return false;
        HpirImport expected = Invoke((HpirWorkOperation)import.Binding);
        return import.Result == expected.Result && import.Arguments.AsSpan().SequenceEqual(expected.Arguments);
    }
}
