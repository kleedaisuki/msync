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
            NativeArguments snapshot = target.CopyTailArguments(tail.Arguments, memory, out bool allocated);
            activation.Work.Counters.TailArguments(tail.Arguments.Count, allocated);
            next = tail with { Arguments = snapshot };
        }
        // Snapshot all next arguments before releasing either the caller frame
        // or its incoming tail buffer: self-tail calls may permute borrowed slots.
        ReleaseActivation(activation, arguments);
        return next;
    }

    private readonly record struct TailReturn(RuntimeProfile.NativeFrameCounters Counters,
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
            sites = TailSites(function);
            sites = Interlocked.CompareExchange(ref selected.TailSites[activation.FunctionIndex], sites, null) ?? sites;
        }
        return sites[pc];
    }

    private static TailPlan?[] TailSites(NativeFunctionCode function)
    {
        TailPlan?[] sites = new TailPlan?[function.Instructions.Count];
        HashSet<int> exposed = function.Instructions
            .Where(instruction => instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation)
            .Select(instruction => instruction.A).ToHashSet();
        // Geometric access through another frame location can also observe a result slot.
        if (exposed.Count != 0 && function.Instructions.Any(instruction => instruction.Op is
            NativeOp.AddressOffset or NativeOp.IndexAddress or NativeOp.CopyMemory or NativeOp.ClearMemory))
            return sites;
        for (int pc = 0; pc + 1 < sites.Length; pc++)
        {
            if (function.Instructions[pc] is { Op: NativeOp.Call, Result: >= 0 } call
                && PrivateTemporary(function, call.Result, exposed))
                sites[pc] = TailPath(function, pc + 1, call.Result, exposed);
        }
        return sites;
    }

    private static bool PrivateTemporary(NativeFunctionCode function, int slot, HashSet<int> exposed) =>
        function.Slots[slot] is { Role: NativeSlotRole.Temporary, Escapes: false } && !exposed.Contains(slot);

    private static TailPlan? TailPath(NativeFunctionCode function, int pc, int result, HashSet<int> exposed)
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
                || !values.Contains(instruction.A) || !PrivateTemporary(function, instruction.Result, exposed)
                || function.Slots[instruction.Result].Type != type) return null;
            if (instruction.Op == NativeOp.Move) values.Remove(instruction.A);
            values.Add(instruction.Result);
            pc++;
        }
        return null;
    }
}
