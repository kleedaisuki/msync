using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    internal static unsafe nint CompiledCopyHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, NativeStep>)&ExecuteCompiledCopy;

    private static NativeStep ExecuteCompiledCopy(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        owner.memory.CopyCompiled(activation.Execution.Operands[pc].Type!, frame.Location(instruction.A), frame.Location(instruction.Result));
        if (owner.profile is not null) activation.Work.Counters.CompiledCopy();
        return new NativeStep(pc + instruction.Span);
    }

    internal static unsafe nint DirectCallHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, NativeStep>)&ExecuteDirectCall;

    private static NativeStep ExecuteDirectCall(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token) =>
        owner.Call(activation, activation.Execution.Metadata[pc], pc, token, bounded: true);

    internal static unsafe nint ConnectionQualifyHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, NativeStep>)&ExecuteConnectionQualify;

    private static NativeStep ExecuteConnectionQualify(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeConnections.NativeEndpoint endpoint = owner.image.Connections[activation.Execution.ConnectionSlot(pc)];
        NativeRuntimeContext origin = owner.context.CaptureDirectory();
        NativeDatum source = activation.Frame.GetDatum(instruction.B);
        NativeDatum value = source.IsReady ? new(NativeValue.FromPath(endpoint.Qualify(origin.AsPath(source.Value), origin)))
            : source.Map(value => NativeValue.FromPath(endpoint.Qualify(origin.AsPath(value), origin)),
                activation.Execution.Metadata[pc].Source, Msync.Core.TypeRef.Path);
        activation.Frame.SetDatum(instruction.Result, owner.Track(value));
        return new NativeStep(pc + instruction.Span);
    }

    internal static unsafe nint UnsignedBinaryHandler => (nint)(delegate* managed<NativeInterpreter, Activation, in NativeExecutionInstruction, int, CancellationToken, NativeStep>)&ExecuteUnsignedBinary;

    private static NativeStep ExecuteUnsignedBinary(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        NativeFrame frame = activation.Frame;
        ulong a = (ulong)NativeStorage.LoadReadyNumber(frame.Location(instruction.A));
        ulong b = (ulong)NativeStorage.LoadReadyNumber(frame.Location(instruction.B));
        NativeAddress target = frame.Location(instruction.Result);
        owner.memory.RemovePending(target);
        NativeBinary op = (NativeBinary)instruction.Immediate;
        if (op >= NativeBinary.Equal)
            NativeStorage.StoreBool(target, op switch
            {
                NativeBinary.Equal => a == b, NativeBinary.NotEqual => a != b,
                NativeBinary.Less => a < b, NativeBinary.LessEqual => a <= b,
                NativeBinary.Greater => a > b, _ => a >= b
            });
        else NativeStorage.StoreNumberCompiled(target, op switch
        {
            NativeBinary.Add => a + b, NativeBinary.Subtract => a - b,
            NativeBinary.Multiply => a * b, NativeBinary.Remainder => a % b,
            _ => throw new InvalidOperationException($"Unsupported unsigned operation {op}")
        });
        if (owner.profile is not null) owner.memory.RecordScalarWrite(target,
            op >= NativeBinary.Equal ? NativeKind.Bool : NativeKind.Number);
        return new NativeStep(pc + instruction.Span);
    }

    internal static nint BackendHandlerAddress(NativeBackendHandler handler, NativeOp op) => handler switch
    {
        NativeBackendHandler.CompiledCopy => CompiledCopyHandler,
        NativeBackendHandler.DirectCall => DirectCallHandler,
        NativeBackendHandler.Nop => NopHandler,
        NativeBackendHandler.FrameStore => FrameStoreHandler,
        NativeBackendHandler.FrameLoad => FrameLoadHandler,
        NativeBackendHandler.OffsetLoad => OffsetLoadHandler,
        NativeBackendHandler.OffsetStore => OffsetStoreHandler,
        NativeBackendHandler.OffsetBind => OffsetBindHandler,
        NativeBackendHandler.ProjectLoad => ProjectLoadHandler,
        NativeBackendHandler.ProjectStore => ProjectStoreHandler,
        NativeBackendHandler.ConnectionQualify => ConnectionQualifyHandler,
        NativeBackendHandler.UnsignedBinary => UnsignedBinaryHandler,
        NativeBackendHandler.NumberBinary => ExecutionHandlerAddress(NativeOp.Binary, number: true),
        _ => ExecutionHandlerAddress(op)
    };

    private static readonly NativeBackendHandler[] BackendHandlers = Enum.GetValues<NativeBackendHandler>();

    internal static NativeBackendHandler BackendHandlerKind(nint handler, NativeOp op)
    {
        if (handler == NopHandler) return NativeBackendHandler.Nop;
        foreach (NativeBackendHandler kind in BackendHandlers)
            if (BackendHandlerAddress(kind, op) == handler) return kind;
        throw new InvalidOperationException($"Unregistered backend handler for {op}.");
    }
}
