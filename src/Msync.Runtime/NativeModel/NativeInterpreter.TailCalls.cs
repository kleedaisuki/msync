using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    internal sealed record TailPlan(IReadOnlyList<int> Instructions);

    internal readonly record struct TailInvocation(int Target, NativeEnvironment? Environment,
        NativeArguments Arguments, int Pc, NativeFunctionCode Caller, NativeInstruction Instruction, TailPlan Plan);

    private void RunDirectTail(Activation caller, int target, NativeEnvironment? environment,
        NativeArguments arguments, int pc, TailPlan plan, CancellationToken token)
    {
        if (profile is not null) caller.Work.Counters.GuestLoopHandoff();
        NativeDatum result = RunFunction(target, environment, arguments, token);
        memory.Write(caller.ReturnDestination, result);
        if (profile is null) return;
        caller.Work.Counters.DirectReturn(scalar: false);
        new TailReturn(caller.Work.Counters, caller.Function, pc, plan).Complete();
    }

    private TailInvocation? CompleteActivation(NativeImage selected, Activation activation, NativeArguments arguments)
    {
        TailInvocation? next = activation.Tail;
        if (next is { } tail && tail.Arguments.Count != 0)
        {
            NativeExecutionFunction target = ExecutionFunction(selected, tail.Target);
            NativeArguments snapshot = target.CopyTailArguments(tail.Arguments, memory, guestHeap, activation.Work.Counters, out bool allocated);
            activation.Work.Counters.TailArguments(tail.Arguments.Count, allocated);
            next = tail with { Arguments = snapshot };
        }
        // Snapshot all next arguments before releasing either the caller frame
        // or its incoming tail buffer: self-tail calls may permute borrowed slots.
        ReleaseGuestActivation(activation, arguments);
        return next;
    }

    internal readonly record struct TailReturn(RuntimeProfile.NativeFrameCounters Counters,
        NativeFunctionCode Function, int Pc, TailPlan Plan)
    {
        internal void Complete()
        {
            int previous = Pc;
            foreach (int pc in Plan.Instructions)
            {
                Counters.Edge(previous, pc);
                Counters.Visit(pc, Function.Instructions[pc].Op);
                previous = pc;
            }
        }
    }

    private bool IsTailCall(Activation activation, int pc) => TailCallPlan(activation, pc) is not null;

    private TailPlan? TailCallPlan(Activation activation, int pc)
    {
        NativeFunctionCode function = activation.Function;
        if (function.Instructions[pc] is not { Op: NativeOp.Call, Result: >= 0 }) return null;
        if (activation.OwnsWork && activation.Work.HasWork || activation.Regions is { Count: > 0 }
            || activation.HasActiveIterators) return null;
        if (activation.FunctionIndex == image.Program.EntryPoint && image.Program.PreserveLocals) return null;
        Generation selected = activeGeneration.Value ?? generation;
        TailPlan?[]? sites = Volatile.Read(ref selected.TailSites[activation.FunctionIndex]);
        if (sites is null)
        {
            sites = TailSites(activation.Execution);
            sites = Interlocked.CompareExchange(ref selected.TailSites[activation.FunctionIndex], sites, null) ?? sites;
        }
        return sites[pc];
    }

    private static TailPlan?[] TailSites(NativeExecutionFunction executable)
    {
        NativeFunctionCode function = executable.Definition;
        TailPlan?[] sites = new TailPlan?[function.Instructions.Count];
        IReadOnlyList<bool> privateSlots = executable.RawWritable;
        for (int pc = 0; pc + 1 < sites.Length; pc++)
        {
            if (function.Instructions[pc] is { Op: NativeOp.Call, Result: >= 0 } call
                && privateSlots[call.Result])
                sites[pc] = TailPath(function, pc + 1, call.Result, privateSlots);
        }
        return sites;
    }

    private static TailPlan? TailPath(NativeFunctionCode function, int pc, int result, IReadOnlyList<bool> privateSlots)
    {
        HashSet<int> values = [result];
        HashSet<int> visited = [];
        List<int> path = [];
        int type = function.Slots[result].Type;
        while ((uint)pc < (uint)function.Instructions.Count && visited.Add(pc))
        {
            NativeInstruction instruction = function.Instructions[pc];
            path.Add(pc);
            if (instruction.Op == NativeOp.Return)
                return values.Contains(instruction.A) && type == function.Signature.Result ? new(path.ToArray()) : null;
            if (instruction.Op == NativeOp.Jump) { pc = instruction.A; continue; }
            if (instruction.Op == NativeOp.Nop) { pc++; continue; }
            if (instruction.Op is not (NativeOp.Copy or NativeOp.Move)
                || !values.Contains(instruction.A) || !privateSlots[instruction.Result]
                || function.Slots[instruction.Result].Type != type) return null;
            if (instruction.Op == NativeOp.Move) values.Remove(instruction.A);
            values.Add(instruction.Result);
            pc++;
        }
        return null;
    }
}
