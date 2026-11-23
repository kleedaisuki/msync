using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallWord32(HpirImport import, HpirCall call, byte* frame, int destination,
        HpirDiagnostics diagnostics, int function, int pc)
    {
        double first = Read<double>(frame, call.Arguments[0]);
        double second = call.Arguments.Count == 1 ? 0 : Read<double>(frame, call.Arguments[1]);
        try { Write(frame, destination, (double)Word32.Evaluate((NumericIntrinsic)import.Binding, first, second)); }
        catch (MsyncException error)
        {
            throw new MsyncException(error.Message, diagnostics.Source(function, pc), error);
        }
    }
}
