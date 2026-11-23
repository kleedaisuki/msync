using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static partial class HpirExecutor
{
    private static bool AcceptsType(IReadOnlyList<HpirTypeLiteral> types, int expected, int actual)
    {
        while (expected != actual)
        {
            var target = types[expected];
            var subject = types[actual];
            if ((target.Kinds & subject.Kinds) != subject.Kinds) return false;
            if (target.Element < 0 || subject.Element < 0) return true;
            expected = target.Element;
            actual = subject.Element;
        }
        return true;
    }
}
