using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private unsafe bool TryRunSsa(Activation activation, NativeSsaBody body, CancellationToken token)
    {
        if (!TryExecuteSsa(activation.Execution, body, activation.Work.Counters,
            activation.Frame, default, token, out ulong result)) return false;
        ReturnScalar(activation, body.Result, result);
        return true;
    }

    private bool TryRunSsaDirect(NativeImage image, int index, NativeSsaBody body, NativeArguments arguments,
        NativeAddress destination, CancellationToken token)
    {
        if (disposed || tierOptions.Policy is not (NativeJitPolicy.Auto or NativeJitPolicy.Force)
            || arguments.Count != body.Parameters || !destination.Type.Accepts(body.Result)) return false;
        var counters = profile?.NativeCounters(image.Program, index) ?? default;
        if (counters.NeedsSites) return false;
        NativeExecutionFunction executable = ExecutionFunction(image, index);
        try
        {
            if (!TryExecuteSsa(executable, body, counters, null, arguments, token, out ulong result)) return false;
            counters.EnterCall();
            counters.SsaFramelessCall();
            WriteScalarResult(destination, body.Result, result, counters);
        }
        catch
        {
            executable.ReturnTailArguments(arguments.Storage, memory);
            throw;
        }
        executable.ReturnTailArguments(arguments.Storage, memory);
        return true;
    }

    private unsafe bool TryExecuteSsa(NativeExecutionFunction executable, NativeSsaBody body,
        RuntimeProfile.NativeFrameCounters counters, NativeFrame? frame, NativeArguments arguments,
        CancellationToken token, out ulong result)
    {
        ulong* state = stackalloc ulong[body.Values];
        result = 0;
        for (int i = 0; i < body.Parameters; i++)
        {
            NativeDatum argument = frame is not null ? frame.GetDatum(executable.Definition.CaptureCount + i)
                : arguments.Read(i, memory);
            if (!argument.IsReady || argument.Value.Kind != body.Kinds[i])
            {
                counters.SsaEntryFallback();
                return false;
            }
            state[i] = argument.Value.Kind == NativeKind.Bool ? argument.Value.AsBool() ? 1UL : 0UL
                : BitConverter.DoubleToUInt64Bits(argument.Value.AsNumber());
        }
        if (executable.GetSsaCode(body, counters) is not { } code) return false;
        if (!code.HasRegisterEntry)
            foreach (NativeSsaLiteral literal in body.Literals) state[literal.Value] = literal.Bits;
        token.ThrowIfCancellationRequested();
        using var entry = code.Open();
        var run = (delegate* unmanaged[Cdecl]<ulong*, int, int, ulong*, int>)entry.Address;
        ulong* calls = stackalloc ulong[2];
        ulong* count = counters.Observed && code.CallSites != 0 ? calls : null;
        if (count != null) { count[0] = 0; count[1] = 0; }
        int resume = 0, polls = 0;
        while ((resume = run(state, resume, 4096, count)) != 0)
        {
            resume--;
            polls++;
            token.ThrowIfCancellationRequested();
        }
        counters.SsaRun(polls, count == null ? 0 : (long)count[0], count == null ? 0 : (long)count[1]);
        result = state[0];
        return true;
    }
}
