using System.Diagnostics;
using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal readonly record struct HpirRunOutcome(ulong Value, int FailedTransfers);

internal static unsafe partial class HpirExecutor
{
    internal static ulong Run(HpirProgram program, ExecutionOptions? options = null, CancellationToken token = default)
        => RunOutcome(program, options, token).Value;

    internal static HpirRunOutcome RunOutcome(HpirProgram program, ExecutionOptions? options = null, CancellationToken token = default)
        => RunCore(program, null, MachineMode.Physical, options, token);

    internal static HpirRunOutcome RunAdaptive(HpirProgram program, ExecutionOptions? options = null, CancellationToken token = default)
        => RunCore(program, null, MachineMode.Adaptive, options, token);

    internal static HpirRunOutcome RunConfigured(HpirProgram program, ExecutionOptions? options = null, CancellationToken token = default)
        => RunCore(program, null, ConfiguredMode(), options, token);

    internal static ulong RunMachineCandidate(HpirMachineImage image, ExecutionOptions? options = null, CancellationToken token = default)
        => RunMachineOutcome(image, options, token).Value;

    internal static HpirRunOutcome RunMachineOutcome(HpirMachineImage image, ExecutionOptions? options = null, CancellationToken token = default)
    {
        if (options?.Profile is not null && image.CounterCount == 0)
            throw new ArgumentException("Compile the HPIR machine image with observation enabled", nameof(image));
        return RunCore(image.Program, image, MachineMode.Physical, options, token);
    }

    private static HpirRunOutcome RunCore(HpirProgram program, HpirMachineImage? image, MachineMode mode,
        ExecutionOptions? options, CancellationToken token, HpirSessionState? session = null)
    {
        RuntimeProfile? profile = options?.Profile;
        RuntimeProfile.HpirCensus? census = profile?.BeginHpir(program);
        var diagnostics = new HpirDiagnostics(program, census, options?.Timeline);
        var stack = new HpirStack(diagnostics);
        using RuntimeTimelineScope? trace = diagnostics.BeginExecution(program.EntryPoint);
        trace?.SetAttribute("dispatch.engine", image is null ? "physical.interpreter" : "machine.x64");
        HpirMachineImage.State machineState = default;
        MachineExecution machine = new(&machineState);
        HpirLiteralBank literals = default;
        HpirHeap? heap = null;
        HpirWork? work = null;
        HpirDirectoryAuthority? directory = null;
        Action<RuntimeObservation>? observer = options?.Observe;
        long initialization = observer is null ? 0 : Stopwatch.GetTimestamp();
        int pc = 0;
        try
        {
            if (program.Stage != HpirStage.Executable)
                throw new MsyncException("HPIR image must be bound before execution");
            token.ThrowIfCancellationRequested();
            nint* globals;
            if (session is null)
            {
                literals = HpirLiteralBank.Link(program, options);
                globals = program.Globals.Count == 0 ? null
                    : (heap = new(program.Layouts)).LinkGlobals(program.Globals, options?.Timeline);
            }
            else
            {
                heap = session.LinkHeap(program, options?.Timeline);
                globals = session.LinkGlobals(program, heap, options?.Timeline);
                literals = session.LinkLiterals(program, heap, options?.Timeline);
                work = session.BeginWork(options?.Timeline, token);
            }
            HpirPathAnchors anchors = literals.Anchors;
            if (program.PathAnchors && program.Imports.Any(import => import.Service == HpirService.Path
                && HpirPathImports.Decode(import.Binding).Op == HpirPathOp.SetWorkingDirectory))
            {
                directory = session?.Directory ?? new(options, program.Context);
                directory.BeginExecution(program.Context);
                anchors = anchors with { Authority = directory };
            }
            machine.Start(program, image, mode, literals.Addresses, globals, token);
            int entry = program.EntryPoint;
            HpirFunction root = program.Functions[entry];
            byte* frame = stack.Enter(entry, root.FrameBytes, -1, null);
            session?.BindResult(root, frame);
            census?.For(entry).EnterFrame();
            if (observer is not null)
            {
                ReportInitialization(observer, initialization);
                initialization = 0;
            }
            if (machine.Eager)
                machine.Promote(program, entry, 0, entry, 0, "forced", diagnostics, options, token);
            ulong result = Execute(program, frame, literals.Addresses, globals, anchors, ref stack, ref heap,
                ref work, ref pc, ref machine, diagnostics, census, options, token, session: session).Value;
            if (work is null) return new(result, 0);
            Exception? cleanup = work.Settle(abort: false, new(options?.Timeline, diagnostics.Source(entry, -1), entry), work.SessionProcesses);
            int failedTransfers = work.FailedTransfers;
            trace?.SetMetric("transfers.failed", failedTransfers);
            work = null;
            if (cleanup is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
            return new(result, failedTransfers);
        }
        catch (Exception error)
        {
            if (initialization != 0) ReportInitialization(observer!, initialization, error);
            int function = stack.Current == null ? program.EntryPoint : stack.Current->Function;
            Exception? cleanup = work?.Settle(abort: true, new(options?.Timeline, diagnostics.Source(function, pc), function, pc), work.SessionProcesses);
            Exception failure = cleanup is null ? error : new AggregateException("HPIR execution and work cleanup failed.", error, cleanup);
            if (trace is not null) diagnostics.Failure(trace, failure, function, pc, stack.ActiveFrames());
            if (cleanup is not null) throw failure;
            throw;
        }
        finally
        {
            stack.Dispose();
            if (session is null) heap?.Dispose();
            literals.Dispose();
            if (session is null) directory?.Dispose();
            else directory?.EndExecution();
            try
            {
                machine.Record(trace, mode);
                if (census is not null)
                {
                    machine.Publish(census);
                    profile!.PublishHpir(program, census);
                }
            }
            finally { machine.Dispose(); }
        }
    }

    private static void ReportInitialization(Action<RuntimeObservation> observer, long start, Exception? error = null) =>
        observer(new("runtime.initialize", error is null ? "success" : error is OperationCanceledException ? "cancelled" : "failure",
            Detail: error?.Message, DurationNanoseconds: Math.Max(1, (long)Stopwatch.GetElapsedTime(start).TotalNanoseconds)));

    private static SliceOutcome Execute(HpirProgram program, byte* frame, nint* literals, nint* globals, HpirPathAnchors anchors, ref HpirStack stack, ref HpirHeap? heap,
        ref HpirWork? work, ref int currentPc, scoped ref MachineExecution machine,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirCensus? census, ExecutionOptions? options, CancellationToken token,
        bool mayPark = false, HpirSessionState? session = null)
    {
        int functionId = stack.Current->Function;
        HpirFunction function = program.Functions[functionId];
        HpirInstruction[] code = function.Instructions;
        RuntimeProfile.HpirFunctionCensus? counts = census?.For(functionId);
        int pc = currentPc;
        int physicalStart = pc;
        bool machineReady = machine.Ready(functionId);
        try
        {
            while (true)
            {
                bool fromMachine = machineReady;
                if (fromMachine)
                {
                    counts?.MachineEnter();
                    pc = machine.Enter(ref stack, pc, frame, work?.InnermostFrame ?? 0);
                    physicalStart = pc;
                    HpirStack.Frame* active = stack.Current;
                    frame = HpirStack.Data(active);
                    // Sibling functions may reuse exactly the same frame address.
                    if (functionId != active->Function)
                    {
                        functionId = active->Function;
                        function = program.Functions[functionId];
                        code = function.Instructions;
                        counts = census?.For(functionId);
                    }
                    if (pc < 0)
                    {
                        pc = ~pc;
                        token.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("HPIR machine observed an unissued cancellation");
                    }
                }
                HpirInstruction instruction = code[pc];
                if (counts is not null && instruction.Op != HpirOp.Call) counts.Step(instruction.Op, pc);
                int dest = instruction.D, a = instruction.A, b = instruction.B;
                switch (instruction.Op)
                {
                    case HpirOp.CopyU8: Write(frame, dest, Read<byte>(frame, a)); break;
                    case HpirOp.CopyU16: Write(frame, dest, Read<ushort>(frame, a)); break;
                    case HpirOp.CopyU32: Write(frame, dest, Read<uint>(frame, a)); break;
                    case HpirOp.CopyU64: Write(frame, dest, Read<ulong>(frame, a)); break;
                    case HpirOp.ConstU8: Write(frame, dest, (byte)instruction.Immediate); break;
                    case HpirOp.ConstU16: Write(frame, dest, (ushort)instruction.Immediate); break;
                    case HpirOp.ConstU32: Write(frame, dest, (uint)instruction.Immediate); break;
                    case HpirOp.ConstU64: Write(frame, dest, (ulong)instruction.Immediate); break;
                    case HpirOp.AddressOf: Write(frame, dest, (nint)(frame + a)); break;
                    case HpirOp.AddressOfStaticCell: Write(frame, dest, (nint)(literals + (int)instruction.Immediate)); break;
                    case HpirOp.AddressOfConnection:
                        Write(frame, dest, literals[HpirConnectionAbi.Text(instruction.Immediate)]
                            | (nint)HpirConnectionAbi.Kind(instruction.Immediate));
                        break;
                    case HpirOp.AddressOfStatic: Write(frame, dest, literals[(int)instruction.Immediate]); break;
                    case HpirOp.AddressOfTypeCell: Write(frame, dest, literals[program.TextLiterals.Count + (int)instruction.Immediate]); break;
                    case HpirOp.AddressOfGlobal: Write(frame, dest, globals[(int)instruction.Immediate]); break;
                    case HpirOp.AddPointer: Write(frame, dest, Read<nint>(frame, a) + Read<nint>(frame, b)); break;
                    case HpirOp.OffsetPointer: Write(frame, dest, Read<nint>(frame, a) + (nint)instruction.Immediate); break;
                    case HpirOp.AddF64: Write(frame, dest, Read<double>(frame, a) + Read<double>(frame, b)); break;
                    case HpirOp.SubF64: Write(frame, dest, Read<double>(frame, a) - Read<double>(frame, b)); break;
                    case HpirOp.MulF64: Write(frame, dest, Read<double>(frame, a) * Read<double>(frame, b)); break;
                    case HpirOp.DivF64: Write(frame, dest, Read<double>(frame, a) / Read<double>(frame, b)); break;
                    case HpirOp.RemF64: Write(frame, dest, Read<double>(frame, a) % Read<double>(frame, b)); break;
                    case HpirOp.NegF64: Write(frame, dest, -Read<double>(frame, a)); break;
                    case HpirOp.EqF64: WriteBool(frame, dest, Read<double>(frame, a) == Read<double>(frame, b)); break;
                    case HpirOp.NeF64: WriteBool(frame, dest, Read<double>(frame, a) != Read<double>(frame, b)); break;
                    case HpirOp.LtF64: WriteBool(frame, dest, Read<double>(frame, a) < Read<double>(frame, b)); break;
                    case HpirOp.LeF64: WriteBool(frame, dest, Read<double>(frame, a) <= Read<double>(frame, b)); break;
                    case HpirOp.GtF64: WriteBool(frame, dest, Read<double>(frame, a) > Read<double>(frame, b)); break;
                    case HpirOp.GeF64: WriteBool(frame, dest, Read<double>(frame, a) >= Read<double>(frame, b)); break;
                    case HpirOp.AndU8: Write(frame, dest, (byte)(Read<byte>(frame, a) & Read<byte>(frame, b))); break;
                    case HpirOp.OrU8: Write(frame, dest, (byte)(Read<byte>(frame, a) | Read<byte>(frame, b))); break;
                    case HpirOp.XorU8: Write(frame, dest, (byte)(Read<byte>(frame, a) ^ Read<byte>(frame, b))); break;
                    case HpirOp.EqU8: WriteBool(frame, dest, Read<byte>(frame, a) == Read<byte>(frame, b)); break;
                    case HpirOp.EqU64: WriteBool(frame, dest, Read<ulong>(frame, a) == Read<ulong>(frame, b)); break;
                    case HpirOp.AddU64: Write(frame, dest, unchecked(Read<ulong>(frame, a) + Read<ulong>(frame, b))); break;
                    case HpirOp.SubU64: Write(frame, dest, unchecked(Read<ulong>(frame, a) - Read<ulong>(frame, b))); break;
                    case HpirOp.MulU64: Write(frame, dest, unchecked(Read<ulong>(frame, a) * Read<ulong>(frame, b))); break;
                    case HpirOp.AndU64: Write(frame, dest, Read<ulong>(frame, a) & Read<ulong>(frame, b)); break;
                    case HpirOp.OrU64: Write(frame, dest, Read<ulong>(frame, a) | Read<ulong>(frame, b)); break;
                    case HpirOp.XorU64: Write(frame, dest, Read<ulong>(frame, a) ^ Read<ulong>(frame, b)); break;
                    case HpirOp.ShlU64: Write(frame, dest, Read<ulong>(frame, a) << (int)Read<ulong>(frame, b)); break;
                    case HpirOp.ShrU64: Write(frame, dest, Read<ulong>(frame, a) >> (int)Read<ulong>(frame, b)); break;
                    case HpirOp.SarI64: Write(frame, dest, Read<long>(frame, a) >> (int)Read<ulong>(frame, b)); break;
                    case HpirOp.LtU64: WriteBool(frame, dest, Read<ulong>(frame, a) < Read<ulong>(frame, b)); break;
                    case HpirOp.LeU64: WriteBool(frame, dest, Read<ulong>(frame, a) <= Read<ulong>(frame, b)); break;
                    case HpirOp.LtI64: WriteBool(frame, dest, Read<long>(frame, a) < Read<long>(frame, b)); break;
                    case HpirOp.LeI64: WriteBool(frame, dest, Read<long>(frame, a) <= Read<long>(frame, b)); break;
                    case HpirOp.SignExtendI8: Write(frame, dest, (long)Read<sbyte>(frame, a)); break;
                    case HpirOp.SignExtendI16: Write(frame, dest, (long)Read<short>(frame, a)); break;
                    case HpirOp.SignExtendI32: Write(frame, dest, (long)Read<int>(frame, a)); break;
                    case HpirOp.OrdinalF64ToU64: Write(frame, dest, HpirOrdinal.FromNumber(Read<double>(frame, a))); break;
                    case HpirOp.ConvertI64ToF64: Write(frame, dest, (double)Read<long>(frame, a)); break;
                    case HpirOp.ConvertU8ToF64: Write(frame, dest, (double)Read<byte>(frame, a)); break;
                    case HpirOp.BoolFromF64:
                        double number = Read<double>(frame, a);
                        WriteBool(frame, dest, number != 0 && !double.IsNaN(number));
                        break;
                    case HpirOp.Jump:
                    case HpirOp.Branch:
                    {
                        int target = instruction.Op == HpirOp.Jump ? a : Read<byte>(frame, a) != 0 ? b : dest;
                        if (target <= pc) token.ThrowIfCancellationRequested();
                        counts?.Edge(pc, target);
                        int from = pc;
                        pc = target;
                        if (machine.Watching)
                        {
                            machine.Charge(functionId, from - physicalStart + 1);
                            if (target <= from && machine.Hot(program, functionId))
                            {
                                machine.Promote(program, functionId, from, functionId, target, "backedge", diagnostics, options, token);
                                machineReady = machine.Ready(functionId);
                            }
                            physicalStart = target;
                        }
                        continue;
                    }
                    case HpirOp.Call:
                    {
                        token.ThrowIfCancellationRequested();
                        HpirCall call = function.Calls[a];
                        if (call.Target < 0)
                        {
                            HpirImport import = program.Imports[~call.Target];
                            if (mayPark && WaitGuestImport(program, heap, work, stack.Current,
                                pc, import, call, diagnostics, counts, options, token) is { } waiting)
                            {
                                currentPc = pc;
                                return new(0, waiting);
                            }
                            counts?.Step(HpirOp.Call, pc);
                            if (machine.Watching)
                            {
                                // Host calls are neither guest work nor an admission trigger.
                                if (!fromMachine) machine.Charge(functionId, pc - physicalStart);
                                physicalStart = pc + 1;
                            }
                            if (import.Service == HpirService.GuestNumber)
                            {
                                counts?.Import(pc, import);
                                LaunchNumber(program, ref heap, ref work, stack.Current, diagnostics, census, counts, pc,
                                    call, import.Binding, dest, literals, globals, anchors, options, token);
                            }
                            else if (import.Service == HpirService.LivePathBatch)
                            {
                                counts?.Import(pc, import);
                                LaunchPathBatch(program, ref heap, ref work, stack.Current, diagnostics, census, counts,
                                    pc, call, import.Binding, dest, literals, globals, anchors, options, token);
                            }
                            else if (import.Service == HpirService.LiveTransferNumberText)
                                CallLiveNumberTransfer(program, ref heap, ref work, stack.Current, diagnostics, census,
                                    counts, pc, import, call, frame, dest, literals, globals, anchors, options, token);
                            else
                            {
                                if (!mayPark) work?.Guests?.SetHostDemand(stack.Current, pc);
                                InvokeImport(program, ref heap, ref work, stack.Current, diagnostics, counts, pc, import,
                                    call, frame, dest, options, token, anchors, literals);
                            }
                            break;
                        }
                        counts?.Step(HpirOp.Call, pc);
                        HpirFunction target = program.Functions[call.Target];
                        byte* result = target.Result == HpirWidth.Void ? null : Address(frame, dest);
                        byte* callee = stack.Enter(call.Target, target.FrameBytes, pc + 1, result);
                        counts?.Call(pc, call.Target);
                        RuntimeProfile.HpirFunctionCensus? next = census?.For(call.Target);
                        CopyArguments(frame, callee, target.Parameters, call.Arguments);
                        next?.EnterFrame();
                        int caller = functionId, callSite = pc;
                        frame = callee;
                        functionId = call.Target;
                        function = target;
                        code = target.Instructions;
                        counts = next;
                        pc = 0;
                        machineReady = machine.Ready(functionId);
                        if (machine.Watching)
                        {
                            if (!fromMachine) machine.Charge(caller, callSite - physicalStart + 1);
                            if (!machineReady)
                            {
                                bool link = fromMachine && machine.HeatCall(functionId);
                                if (link || machine.Hot(program, functionId))
                                {
                                    machine.Promote(program, caller, callSite, functionId, pc,
                                        link ? "guest.native-link" : "guest.call", diagnostics, options, token);
                                    machineReady = machine.Ready(functionId);
                                }
                            }
                            physicalStart = 0;
                        }
                        continue;
                    }
                    case HpirOp.Return:
                    {
                        HpirStack.Frame* header = stack.Current;
                        counts?.Return();
                        HpirWidth width = function.Result;
                        ulong result = width == HpirWidth.Void ? 0 : ReadWidth(Address(frame, a), width);
                        if (machine.Watching)
                        {
                            if (!fromMachine) machine.Charge(functionId, pc - physicalStart + 1);
                            physicalStart = header->ResumeAt;
                        }
                        if (header->Parent != null && work?.InnermostFrame == (nint)header)
                            FinishFrameWork(program, heap, work, header, pc, instruction,
                                result, diagnostics, counts, options);
                        if (header->Parent == null)
                        {
                            if (!mayPark) work?.Guests?.DrainHostExports(header, pc, token);
                            session?.ObserveResultFrontier(result, program, heap!, work, header, pc, diagnostics, counts, token);
                            stack.Leave();
                            return new(result);
                        }
                        if (width != HpirWidth.Void) WriteWidth(header->Result, width, result);
                        pc = header->ResumeAt;
                        stack.Leave();
                        header = stack.Current;
                        frame = HpirStack.Data(header);
                        functionId = header->Function;
                        function = program.Functions[functionId];
                        code = function.Instructions;
                        counts = census?.For(functionId);
                        machineReady = machine.Ready(functionId);
                        continue;
                    }
                    default:
                        throw new MsyncException($"Unsupported HPIR instruction {instruction.Op} in '{function.Name}' at {pc}");
                }
                pc++;
            }
        }
        catch
        {
            currentPc = pc;
            throw;
        }
    }

    private static void CopyArguments(byte* caller, byte* callee, IReadOnlyList<HpirParameter> parameters,
        IReadOnlyList<int> arguments)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            HpirParameter parameter = parameters[i];
            byte* source = Address(caller, arguments[i]);
            byte* target = callee + parameter.Offset;
            switch (parameter.Width)
            {
                case HpirWidth.U8: *target = *source; break;
                case HpirWidth.U16: Unsafe.WriteUnaligned(target, Unsafe.ReadUnaligned<ushort>(source)); break;
                case HpirWidth.U32: Unsafe.WriteUnaligned(target, Unsafe.ReadUnaligned<uint>(source)); break;
                default: Unsafe.WriteUnaligned(target, Unsafe.ReadUnaligned<ulong>(source)); break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* Address(byte* frame, int operand) => operand >= 0
        ? frame + operand : (byte*)Unsafe.ReadUnaligned<nint>(frame + ~operand);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Read<T>(byte* frame, int operand) where T : unmanaged => Unsafe.ReadUnaligned<T>(Address(frame, operand));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write<T>(byte* frame, int operand, T value) where T : unmanaged => Unsafe.WriteUnaligned(Address(frame, operand), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteBool(byte* frame, int operand, bool value) => Write(frame, operand, value ? (byte)1 : (byte)0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadWidth(byte* source, HpirWidth width) => width switch
    {
        HpirWidth.U8 => *source,
        HpirWidth.U16 => Unsafe.ReadUnaligned<ushort>(source),
        HpirWidth.U32 => Unsafe.ReadUnaligned<uint>(source),
        _ => Unsafe.ReadUnaligned<ulong>(source)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteWidth(byte* destination, HpirWidth width, ulong value)
    {
        switch (width)
        {
            case HpirWidth.U8: *destination = (byte)value; break;
            case HpirWidth.U16: Unsafe.WriteUnaligned(destination, (ushort)value); break;
            case HpirWidth.U32: Unsafe.WriteUnaligned(destination, (uint)value); break;
            default: Unsafe.WriteUnaligned(destination, value); break;
        }
    }
}
