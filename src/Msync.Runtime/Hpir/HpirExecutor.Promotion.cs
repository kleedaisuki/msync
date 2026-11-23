using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private enum MachineMode : byte { Physical, Adaptive, Eager }

    private static MachineMode ConfiguredMode() => Environment.GetEnvironmentVariable("MSYNC_JIT")?.ToLowerInvariant() switch
    {
        "off" or "0" or "false" or "linked" => MachineMode.Physical,
        "force" => MachineMode.Eager,
        _ => MachineMode.Adaptive
    };

    private ref struct MachineExecution(HpirMachineImage.State* state)
    {
        private const long MinimumWork = 16 * 1024;
        private const int WorkPerInstruction = 64, NativeCallsToLink = 4;

        private enum Decision : byte { Disabled, Watching, Unsupported, Promoted, Unavailable, Cancelled, Failed }

        private struct Heat
        {
            internal long Work;
            internal int NativeCalls;
        }

        private HpirMachineImage? owned;
        private HpirMachineImage.Cursor cursor;
        private CancellationTokenRegistration cancellation;
        private nint* literals, globals;
        private Heat[]? heat;
        private Decision decision;

        internal readonly bool Watching => heat is not null && decision != Decision.Unavailable;
        internal readonly bool Eager => decision == Decision.Watching && heat is null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool Ready(int id) => cursor.Active && cursor.Contains(id);

        internal void Start(HpirProgram program, HpirMachineImage? image, MachineMode mode, nint* literalAddresses,
            nint* globalAddresses, CancellationToken token)
        {
            literals = literalAddresses;
            globals = globalAddresses;
            if (image is not null)
            {
                Activate(image, token);
                return;
            }
            if (mode == MachineMode.Physical) return;
            if (!HpirMachineImage.IsSupported)
            {
                decision = Decision.Unsupported;
                return;
            }

            if (mode == MachineMode.Adaptive) heat = new Heat[program.Functions.Count];
            decision = Decision.Watching;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Charge(int id, int instructions) => heat![id].Work += instructions;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly bool Hot(HpirProgram program, int id) =>
            heat![id].Work >= Budget(program.Functions[id]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool HeatCall(int id) => ++heat![id].NativeCalls >= NativeCallsToLink;

        private static long Budget(HpirFunction function)
            => Math.Max(MinimumWork, (long)function.Instructions.Length * WorkPerInstruction);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Promote(HpirProgram program, int from, int instruction, int target, int resumeAt, string trigger,
            HpirDiagnostics diagnostics, ExecutionOptions? options, CancellationToken token)
        {
            using RuntimeTimelineScope? trace = options?.Timeline?.Begin("hpir.machine.promote");
            if (trace is not null)
            {
                trace.SetAttribute("trigger", trigger);
                trace.SetAttribute("function.id", from.ToString(CultureInfo.InvariantCulture));
                trace.SetAttribute("function.name", program.Functions[from].Name);
                trace.SetMetric("instruction", instruction);
                trace.SetAttribute("resume.function.id", target.ToString(CultureInfo.InvariantCulture));
                trace.SetAttribute("resume.function.name", program.Functions[target].Name);
                trace.SetMetric("resume.instruction", resumeAt);
                trace.SetSource(diagnostics.Source(from, instruction));
                if (heat is not null)
                {
                    trace.SetMetric("guest.instruction_budget", Budget(program.Functions[target]));
                    trace.SetMetric("guest.instructions_observed", heat[target].Work);
                    trace.SetMetric("guest.native_waiting_calls", heat[target].NativeCalls);
                    trace.SetMetric("guest.native_waiting_call_budget", NativeCallsToLink);
                    trace.SetAttribute("cache", "miss");
                }
            }
            try
            {
                token.ThrowIfCancellationRequested();
                if (heat is null)
                    owned = HpirMachineImage.Compile(program, options?.Profile is not null, options?.Timeline, token);
                else
                {
                    owned ??= new(program, options?.Profile is not null);
                    owned.PublishFunction(target, options?.Timeline, token);
                }
                if (!cursor.Active) Activate(owned, token);
                decision = Decision.Promoted;
            }
            catch (PlatformNotSupportedException unavailable)
            {
                decision = Decision.Unavailable;
                trace?.SetAttribute("reason", unavailable.Message);
            }
            catch (Exception error)
            {
                decision = error is OperationCanceledException ? Decision.Cancelled : Decision.Failed;
                trace?.SetAttribute("decision", DecisionName());
                trace?.Fail(error);
                throw;
            }
            trace?.SetAttribute("decision", DecisionName());
        }

        private void Activate(HpirMachineImage image, CancellationToken token)
        {
            if (image.CounterCount != 0)
                state->Counters = (ulong*)NativeMemory.AllocZeroed((nuint)image.CounterCount, sizeof(ulong));
            cursor = image.Open(state);
            cursor.LinkBanks(literals, globals);
            if (token.CanBeCanceled)
                cancellation = token.UnsafeRegister(static address =>
                    Volatile.Write(ref ((HpirMachineImage.State*)(nint)address!)->Canceled, 1), (nint)state);
        }

        internal readonly int Enter(ref HpirStack stack, int pc, byte* frame, nint innermostWorkFrame)
            => cursor.Enter(ref stack, pc, frame, innermostWorkFrame);

        internal readonly void Publish(RuntimeProfile.HpirCensus census) => cursor.Publish(census);

        internal readonly void Record(RuntimeTimelineScope? trace, MachineMode mode)
        {
            if (trace is null || decision == Decision.Disabled) return;
            trace.SetAttribute("machine.promotion", DecisionName());
            if (cursor.Active) trace.SetAttribute("dispatch.engine", mode == MachineMode.Eager
                ? "machine.x64" : "physical.interpreter+machine.x64");
            if (owned is null) return;
            trace.SetMetric("machine.compiled_functions", owned.CompiledCount);
            trace.SetMetric("machine.code_bytes", owned.CodeBytes);
        }

        private readonly string DecisionName() => decision switch
        {
            Decision.Watching => "below-budget",
            Decision.Unsupported => "unsupported",
            Decision.Promoted => "promoted",
            Decision.Unavailable => "unavailable",
            Decision.Cancelled => "cancelled",
            Decision.Failed => "failed",
            _ => "disabled"
        };

        public void Dispose()
        {
            cancellation.Dispose();
            owned?.Dispose();
            if (state->Counters != null) NativeMemory.Free(state->Counters);
        }
    }
}
