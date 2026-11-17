using System.Runtime.InteropServices;
using Msync.Core;

namespace Msync.Core.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSequenceCursor
{
    internal const int Bytes = 32;
    internal const int Open = 1, Descending = 2, Stalled = 4;
    internal const string StalledMessage = "ranges.sequence step no longer advances the Number value";

    internal double Current, Limit, Step;
    internal int State;

    internal NativeSequenceCursor(NativeNumberSequence sequence)
    {
        (Current, Limit, Step) = (sequence.First, sequence.Limit, sequence.Step);
        State = Open | (Step < 0 ? Descending : 0);
    }

    internal bool Next(out double value)
    {
        if ((State & Stalled) != 0) throw new MsyncException(StalledMessage);
        value = Current;
        if (Step > 0 ? !(value < Limit) : !(value > Limit)) return false;
        double next = value + Step;
        if (next == value) State |= Stalled;
        Current = next;
        return true;
    }
}
