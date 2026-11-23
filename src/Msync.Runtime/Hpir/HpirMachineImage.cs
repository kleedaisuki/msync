using System.Globalization;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal sealed unsafe partial class HpirMachineImage : IDisposable
{
    private readonly List<NativeCodePage> pages = [];
    private readonly nint[] stubs;
    private readonly int[]? counterStarts;
    private nint* entries;

    internal HpirMachineImage(HpirProgram program, bool observe)
    {
        if (program.Stage != HpirStage.Executable)
            throw new MsyncException("HPIR image must be bound before machine code emission");
        Program = program;
        stubs = new nint[program.Functions.Count];
        if (observe)
        {
            counterStarts = new int[stubs.Length];
            int counters = 0;
            for (int id = 0; id < counterStarts.Length; id++)
            {
                counterStarts[id] = counters;
                counters = checked(counters + program.Functions[id].Instructions.Length * CountersPerInstruction);
            }
            CounterCount = counters;
        }
        entries = (nint*)NativeMemory.AllocZeroed((nuint)stubs.Length * 2, (nuint)sizeof(nint));
    }

    internal HpirProgram Program { get; }
    internal int CounterCount { get; }
    internal int CompiledCount { get; private set; }
    internal long CodeBytes { get; private set; }
    internal static bool IsSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64 && NativeCodePage.IsSupported;
    // The first row supplies direct call targets, the second supplies
    // function-local resume tables. Both have immutable published entries.
    private nint Target(int id) => entries[id];

    internal static HpirMachineImage Compile(HpirProgram program, bool observe = false, RuntimeTimeline? timeline = null,
        CancellationToken token = default)
    {
        var image = new HpirMachineImage(program, observe);
        try
        {
            image.Publish(0, program.Functions.Count, timeline, token);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    internal void PublishFunction(int id, RuntimeTimeline? timeline, CancellationToken token)
    {
        System.Diagnostics.Debug.Assert(entries[id] == 0);
        Publish(id, 1, timeline, token);
    }

    private void Publish(int first, int count, RuntimeTimeline? timeline, CancellationToken token)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("HPIR machine blocks require x64 executable pages");
        using RuntimeTimelineScope? trace = timeline?.Begin("hpir.machine.emit");
        try
        {
            var emitter = new X64(this, first, count);
            for (int id = first; id < first + count; id++)
            {
                token.ThrowIfCancellationRequested();
                EmitFunction(emitter, id, timeline);
            }
            token.ThrowIfCancellationRequested();
            byte[] bytes = emitter.Finish();
            trace?.SetMetric("functions", count);
            trace?.SetMetric("code.bytes", bytes.Length);
            if (trace is not null && counterStarts is not null)
            {
                int end = first + count == stubs.Length ? CounterCount : counterStarts[first + count];
                trace.SetMetric("profile.counters", end - counterStarts[first]);
            }
            trace?.SetMetric("guest.inline_service_sites", emitter.InlineServiceSites);
            trace?.SetMetric("guest.direct_cached_sites", emitter.CachedCallSites);
            trace?.SetMetric("guest.direct_late_sites", emitter.LateCallSites);
            trace?.SetMetric("scalar.forwarded_operands", emitter.ForwardedOperands);
            trace?.SetMetric("scalar.elided_stores", emitter.ElidedStores);
            PublishPage(emitter, bytes, first, count, timeline);
        }
        catch (Exception failure)
        {
            trace?.Fail(failure);
            throw;
        }
    }

    private void EmitFunction(X64 emitter, int id, RuntimeTimeline? timeline)
    {
        HpirFunction function = Program.Functions[id];
        using RuntimeTimelineScope? body = timeline?.Begin("hpir.machine.emit.function");
        if (body is null)
        {
            emitter.Function(id, function, counterStarts?[id] ?? 0);
            return;
        }

        body.SetAttribute("function.id", id.ToString(CultureInfo.InvariantCulture));
        body.SetAttribute("function.name", function.Name);
        if (function.Sources.Count != 0) body.SetSource(function.Sources[0].Source);
        try
        {
            int start = emitter.Length;
            int calls = emitter.DirectCallSites;
            int cached = emitter.CachedCallSites;
            int late = emitter.LateCallSites;
            int inline = emitter.InlineServiceSites;
            int forwarding = emitter.ForwardedOperands;
            int stores = emitter.ElidedStores;
            emitter.Function(id, function, counterStarts?[id] ?? 0);
            body.SetMetric("instructions", function.Instructions.Length);
            body.SetMetric("code.bytes", emitter.Length - start);
            body.SetMetric("guest.direct_call_sites", emitter.DirectCallSites - calls);
            body.SetMetric("guest.direct_cached_sites", emitter.CachedCallSites - cached);
            body.SetMetric("guest.direct_late_sites", emitter.LateCallSites - late);
            body.SetMetric("guest.inline_service_sites", emitter.InlineServiceSites - inline);
            body.SetMetric("scalar.forwarded_operands", emitter.ForwardedOperands - forwarding);
            body.SetMetric("scalar.elided_stores", emitter.ElidedStores - stores);
        }
        catch (Exception failure)
        {
            body.Fail(failure);
            throw;
        }
    }

    private void PublishPage(X64 emitter, byte[] bytes, int first, int count, RuntimeTimeline? timeline)
    {
        using RuntimeTimelineScope? seal = timeline?.Begin("hpir.machine.publish");
        seal?.SetMetric("code.bytes", bytes.Length);
        pages.EnsureCapacity(pages.Count + 1);
        NativeCodePage? page = NativeCodePage.Create(bytes);
        if (page is null)
        {
            var failure = new PlatformNotSupportedException("Could not publish executable HPIR code pages");
            seal?.Fail(failure);
            throw failure;
        }
        pages.Add(page);
        nint address = page.DangerousGetHandle();
        for (int id = first; id < first + count; id++)
        {
            stubs[id] = address;
            int* row = (int*)(address + emitter.TableOffset(id));
            entries[stubs.Length + id] = (nint)row;
            entries[id] = (nint)row + row[0];
        }
        CompiledCount += count;
        CodeBytes += bytes.Length;
    }

    internal const int CountersPerInstruction = 3;

    [StructLayout(LayoutKind.Explicit, Size = StackOffset + HpirStack.MachineCursor.Bytes)]
    internal struct State
    {
        internal const int CancelOffset = 0, CounterOffset = 8, LiteralOffset = 16, GlobalOffset = 24, EntryTableOffset = 32, StackOffset = 40;
        [FieldOffset(CancelOffset)] internal int Canceled;
        [FieldOffset(CounterOffset)] internal ulong* Counters;
        [FieldOffset(LiteralOffset)] internal nint* Literals;
        [FieldOffset(GlobalOffset)] internal nint* Globals;
        [FieldOffset(EntryTableOffset)] internal nint* Entries;
        [FieldOffset(StackOffset)] internal HpirStack.MachineCursor Stack;
    }

    internal Cursor Open(State* state)
    {
        state->Entries = entries;
        return new(this, state);
    }

    // A cursor borrows its image for the entire execution. In particular, a
    // guest jump may leave the page containing the entry trampoline.
    internal readonly ref struct Cursor(HpirMachineImage image, State* state)
    {
        internal bool Active => state != null;
        internal bool Contains(int id) => state->Entries[id] != 0;

        internal void LinkBanks(nint* literals, nint* globals)
        {
            if (state == null) return;
            state->Literals = literals;
            state->Globals = globals;
        }

        internal int Enter(ref HpirStack stack, int pc, byte* frame, nint innermostWorkFrame)
        {
            int function = stack.Current->Function;
            state->Stack = stack.ExportCursor(innermostWorkFrame);
            int* row = (int*)state->Entries[image.stubs.Length + function];
            nint start = (nint)row + row[pc];
            int boundary = ((delegate* unmanaged<byte*, State*, nint, int>)image.stubs[function])(frame, state, start);
            GC.KeepAlive(image);
            stack.AdoptCursor(in state->Stack);
            return boundary;
        }

        internal void Publish(RuntimeProfile.HpirCensus census)
        {
            if (state == null || state->Counters == null) return;
            for (int function = 0; function < image.stubs.Length; function++)
            {
                if (state->Entries[function] == 0) continue;
                HpirFunction body = image.Program.Functions[function];
                HpirInstruction[] instructions = body.Instructions;
                ulong* current = state->Counters + image.counterStarts![function];
                for (int pc = 0; pc < instructions.Length; pc++, current += CountersPerInstruction)
                {
                    ulong hits = current[0];
                    if (hits == 0) continue;
                    HpirInstruction instruction = instructions[pc];
                    var counts = census.For(function);
                    counts.MachineStep(instruction.Op, pc, hits);
                    if (instruction.Op == HpirOp.Jump) counts.Edge(pc, instruction.A, current[1]);
                    else if (instruction.Op == HpirOp.Branch)
                    {
                        counts.Edge(pc, instruction.B, current[1]);
                        counts.Edge(pc, instruction.D, current[2]);
                    }
                    else if (instruction.Op == HpirOp.Call)
                    {
                        int target = body.Calls[instruction.A].Target;
                        if (target < 0) counts.Import(pc, image.Program.Imports[~target], hits, inline: true);
                        else
                        {
                            counts.MachineCall(pc, target, hits);
                            census.For(target).EnterFrame(hits);
                        }
                    }
                    else if (instruction.Op == HpirOp.Return)
                        counts.MachineReturn(hits);
                }
            }
        }

    }

    public void Dispose()
    {
        if (entries == null) return;
        foreach (NativeCodePage page in pages) page.Dispose();
        NativeMemory.Free(entries);
        entries = null;
    }
}
