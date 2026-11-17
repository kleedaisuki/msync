using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using Msync.Core;
using Msync.Core.Native;
using Msync.Core.Llvm;

namespace Msync.Runtime.Native;

internal sealed class NativeCompiledFunction(NativeCodePage page, int bytes,
    NativeFastOperation[] operations, NativeTierLayout layout, NativeCodegenStatistics statistics,
    NativeMachineLeaf? leaf = null) : IDisposable
{
    internal int Bytes { get; } = bytes;
    internal NativeFastOperation[] Operations { get; } = operations;
    internal NativeTierLayout Layout { get; } = layout;
    internal NativeCodegenStatistics Statistics { get; } = statistics;
    internal NativeMachineLeaf? Leaf { get; } = leaf;
    internal static bool MachineCallsSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64 && NativeCodePage.IsSupported;

    internal static NativeCompiledFunction? Compile(NativeImage image, NativeExecutionFunction function, NativeJitPolicy policy,
        bool observeRegisterPlanning = false, bool leafOnly = false, Func<int, NativeCompiledFunction?>? link = null,
        bool scopedGuestMemory = false)
    {
        if (!NativeCodePage.IsSupported || !BitConverter.IsLittleEndian || function.Metadata.Length == 0) return null;
        long started = Stopwatch.GetTimestamp();
        NativeSiteProfile?[]? observations = function.ObservedProfiles;
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        bool hasRoundingInstructions = architecture == Architecture.Arm64 || Sse41.IsSupported;
        NativeFastOperation[] operations = new NativeFastOperation[function.Instructions.Length];
        for (int pc = 0; pc < operations.Length; pc += function.Instructions[pc].Span)
            operations[pc] = Select(image, function, function.Instructions[pc], pc,
                observations?[pc]?.Snapshot(), function.NumberForm(pc), hasRoundingInstructions, architecture == Architecture.X64,
                scopedGuestMemory && architecture == Architecture.X64);
        if (leafOnly)
            for (int pc = 0; pc < operations.Length; pc += function.Instructions[pc].Span)
                if (operations[pc] == NativeFastOperation.Bridge) return null;
        List<NativeCodePage>? dependencies = null;
        IReadOnlyDictionary<int, NativeMachineCall>? calls = null;
        try
        {
            var views = scopedGuestMemory && architecture == Architecture.X64 && !leafOnly
                ? NativeGuestView.Link(image, function, operations) : null;
            if (link is not null && MachineCallsSupported)
                calls = LinkCalls(image, function, operations, link, scopedGuestMemory, ref dependencies);
            (int Fields, int Layouts) keptWithBridge = scopedGuestMemory && architecture == Architecture.X64
                ? KeepBridgeGroups(function, operations) : default;
            (int Copies, int Views) referencesKept = scopedGuestMemory && architecture == Architecture.X64
                ? KeepReferenceRunsWithBridge(function, operations, views) : default;
            if (!leafOnly && policy == NativeJitPolicy.Auto && (!NativeMachineCode.HasDirectPaths
                || !IsProfitableForAuto(function, operations))) return null;
            NativeCompiledFunction? result = Publish(image, function, operations, architecture, observeRegisterPlanning,
                leafOnly, calls, dependencies, Stopwatch.GetElapsedTime(started).Ticks * 100, keptWithBridge, referencesKept, views);
            if (result is not null) dependencies = null;
            return result;
        }
        finally
        {
            if (dependencies is not null)
                foreach (NativeCodePage dependency in dependencies) dependency.DangerousRelease();
        }
    }

    private static NativeCompiledFunction? Publish(NativeImage image, NativeExecutionFunction function,
        NativeFastOperation[] operations, Architecture architecture, bool observeRegisterPlanning, bool leafOnly,
        IReadOnlyDictionary<int, NativeMachineCall>? calls, List<NativeCodePage>? dependencies, long selection,
        (int Fields, int Layouts) keptWithBridge, (int Copies, int Views) referencesKept,
        IReadOnlyDictionary<int, NativeGuestView>? views)
    {
        long started = Stopwatch.GetTimestamp();
        NativeRegisterPlanMetrics? registerMetrics = observeRegisterPlanning ? new() : null;
        NativeTierLayout layout = NativeTierLayout.Create(function, operations, image, registerMetrics, calls, views);
        if (layout.LayoutBranches is { } layoutBranches)
            foreach (int pc in layoutBranches)
                // Only a bridged layout test can reach the separate branch entry.
                operations[pc + function.Instructions[pc].Span] = NativeFastOperation.Bridge;
        NativeMachineLeaf? leaf = NativeMachineLeaf.Describe(image, function, operations, layout);
        if (leafOnly && leaf is null) return null;
        long allocation = Stopwatch.GetElapsedTime(started).Ticks * 100;
        started = Stopwatch.GetTimestamp();
        byte[] bytes = NativeMachineCode.Emit(function, operations, architecture,
            OperatingSystem.IsWindows(), NativeInterpreter.TierBridgeAddress, NativeInterpreter.TierNumberAddress,
            NativeInterpreter.TierBoolAddress, image.Program.Constants, out NativeInitializationElisions initializationElisions,
            layout, NativeInterpreter.TierCommitAddress, calls, leaf?.GuestReferences > 0);
        long emission = Stopwatch.GetElapsedTime(started).Ticks * 100;
        started = Stopwatch.GetTimestamp();
        NativeCodePage? page = NativeCodePage.Create(bytes, dependencies);
        NativeCodegenStatistics statistics = new(selection, allocation, emission, Stopwatch.GetElapsedTime(started).Ticks * 100,
            layout.Regions.Length, layout.Regions.Sum(region => region.Values.Length), layout.Regions.Sum(region => region.Spills),
            layout.Regions.Sum(region => region.Operations.Length - region.Outputs.Length))
        {
            RegisterPlanning = registerMetrics,
            InitializationChecksElided = initializationElisions.Checks,
            InitializationWritesElided = initializationElisions.Writes,
            DoubleComparisonSites = observeRegisterPlanning ? operations.Where((operation, pc) =>
                operation != NativeFastOperation.Bridge && function.Instructions[pc].Op == NativeOp.MachineCompare).Count() : 0,
            UnaryMathSites = observeRegisterPlanning ? operations.Count(static operation => operation.IsUnaryMath()) : 0,
            GuestLoadSites = observeRegisterPlanning ? operations.Count(static operation => operation == NativeFastOperation.GuestLoad) : 0,
            GuestStoreSites = observeRegisterPlanning ? operations.Count(static operation => operation == NativeFastOperation.GuestStore) : 0,
            GuestLayoutSites = observeRegisterPlanning ? operations.Count(static operation => operation == NativeFastOperation.GuestHasLayout) : 0,
            GuestLayoutBranchSites = observeRegisterPlanning ? layout.LayoutBranches?.Count ?? 0 : 0,
            GuestCopySites = observeRegisterPlanning ? operations.Count(static operation => operation == NativeFastOperation.GuestCopy) : 0,
            GuestViewSites = views?.Count ?? 0,
            GuestViewsKeptWithBridge = referencesKept.Views,
            GuestCopiesKeptWithBridge = referencesKept.Copies,
            GuestAccessKeptWithBridge = keptWithBridge.Fields,
            GuestLayoutKeptWithBridge = keptWithBridge.Layouts,
            MachineCallSites = calls?.Count ?? 0,
            MachinePointerCallSites = observeRegisterPlanning ? calls?.Values.Count(call => call.Leaf.GuestReferences != 0) ?? 0 : 0,
            UnsignedValues = layout.Regions.Sum(region => region.Values.Count(value => value.Unsigned)),
            UnsignedOperations = layout.Regions.Sum(region => region.Operations.Count(operation => operation.Form != NativeNumberForm.Float64))
        };
        return page is null ? null : new(page, bytes.Length, operations, layout, statistics, leaf);
    }

    private static (int Fields, int Layouts) KeepBridgeGroups(NativeExecutionFunction function, NativeFastOperation[] operations)
    {
        int previous = -1, fields = 0, layouts = 0;
        for (int pc = 0; pc < operations.Length;)
        {
            int next = pc + function.Instructions[pc].Span;
            // An isolated operation between bridges already runs
            // inside their dispatch loop. Pulling it out adds a round trip.
            if (operations[pc].IsGuestAccess() && operations[pc] is not (NativeFastOperation.GuestCopy or NativeFastOperation.GuestView)
                && previous >= 0 && next < operations.Length
                && operations[previous] == NativeFastOperation.Bridge && operations[next] == NativeFastOperation.Bridge)
            {
                if (operations[pc] == NativeFastOperation.GuestHasLayout) layouts++;
                else fields++;
                operations[pc] = NativeFastOperation.Bridge;
            }
            previous = pc;
            pc = next;
        }
        return (fields, layouts);
    }

    private static (int Copies, int Views) KeepReferenceRunsWithBridge(NativeExecutionFunction function,
        NativeFastOperation[] operations, Dictionary<int, NativeGuestView>? views)
    {
        int copies = 0, calls = 0, start = -1, previous = -1;
        bool followsBridge = false;
        for (int pc = 0; pc < operations.Length; pc += function.Instructions[pc].Span)
        {
            if (operations[pc] is NativeFastOperation.GuestCopy or NativeFastOperation.GuestView)
            {
                if (start >= 0) continue;
                start = pc;
                followsBridge = previous >= 0 && operations[previous] == NativeFastOperation.Bridge;
                continue;
            }
            if (start >= 0 && followsBridge && operations[pc] == NativeFastOperation.Bridge)
                for (int site = start; site < pc; site += function.Instructions[site].Span)
                {
                    if (operations[site] == NativeFastOperation.GuestCopy) copies++;
                    else
                    {
                        views!.Remove(site);
                        calls++;
                    }
                    operations[site] = NativeFastOperation.Bridge;
                }
            start = -1;
            previous = pc;
        }
        return (copies, calls);
    }

    private static IReadOnlyDictionary<int, NativeMachineCall>? LinkCalls(NativeImage image, NativeExecutionFunction caller,
        NativeFastOperation[] operations, Func<int, NativeCompiledFunction?> link, bool machineClosures,
        ref List<NativeCodePage>? dependencies)
    {
        Dictionary<int, NativeMachineCall>? calls = null;
        CallableFlow? flow = null;
        int previous = -1, earlier = -1;
        NativeFrameSlot[] slots = image.Frame(caller.Index).Slots;
        for (int pc = 0; pc < operations.Length; pc += caller.Instructions[pc].Span)
        {
            int loadPc = previous, gapStart = earlier;
            earlier = previous;
            previous = pc;
            NativeExecutionInstruction instruction = caller.Instructions[pc];
            NativeInstruction call = caller.Metadata[pc];
            if (instruction.Op != NativeOp.Call || instruction.Handler != NativeInterpreter.DirectCallHandler
                || call.B < 0 || call.C >= 0 || !caller.CanWriteRaw(call.Result)
                || slots[call.Result].Binding >= 0 || slots[call.A].Binding >= 0 || !caller.HasKind(call.A, NativeKind.Function))
                continue;
            if (!machineClosures && image.Program.Functions[call.B].CaptureCount != 0) continue;
            if (link(call.B) is not { Leaf: { } leaf } code || !caller.HasKind(call.Result, leaf.ResultKind)) continue;
            if (leaf.Captures.Length != 0 && !caller.RawWritable[call.A]) continue;
            IReadOnlyList<int> inputs = call.Inputs ?? [];
            if (inputs.Count != leaf.Parameters.Length) continue;
            bool exact = true;
            for (int index = 0; index < inputs.Count; index++)
            {
                NativeKind kind = leaf.Parameters[index].Kind;
                int input = inputs[index];
                exact &= caller.HasKind(input, kind) && (kind != NativeKind.Address || machineClosures && slots[input].Binding < 0);
            }
            if (!exact) continue;
            nint address;
            try { address = code.codePage.Retain(); }
            catch (ObjectDisposedException) { continue; }
            (dependencies ??= []).Add(code.codePage);
            NativeMachineCallableSource? source = leaf.Captures.Length == 0 ? null
                : LinkCallableSource(image, caller, slots, call.A, loadPc, ref flow);
            if (source is null && leaf.Captures.Length != 0 && gapStart >= 0
                && CanInlineCallArgument(caller, slots, operations, loadPc, inputs))
            {
                NativeMachineCallableSource? field = LinkCallableSource(image, caller, slots, call.A, gapStart, ref flow);
                if (field is { Field: not null } projection) source = projection with { InlineArgument = loadPc };
            }
            if (source is { } fused) operations[fused.Pc] = NativeFastOperation.MachineFusedCall;
            (calls ??= []).Add(pc, new(call.B, leaf, inputs, address, source));
            operations[pc] = NativeFastOperation.MachineCall;
        }
        return calls;
    }

    private static bool CanInlineCallArgument(NativeExecutionFunction caller, NativeFrameSlot[] slots,
        NativeFastOperation[] operations, int pc, IReadOnlyList<int> inputs)
    {
        if (operations[pc] is not (NativeFastOperation.NumberAdd or NativeFastOperation.NumberSubtract or NativeFastOperation.NumberMultiply))
            return false;
        NativeExecutionInstruction arithmetic = caller.Instructions[pc];
        int result = arithmetic.Result;
        // The value may be computed again if a later operand sends the call to its original entry.
        return result != arithmetic.A && result != arithmetic.B && slots[result].Binding < 0
            && caller.CanWriteRaw(result) && caller.HasKind(result, NativeKind.Number)
            && caller.HasKind(arithmetic.A, NativeKind.Number) && caller.HasKind(arithmetic.B, NativeKind.Number)
            && inputs.Contains(result);
    }

    private static NativeMachineCallableSource? LinkCallableSource(NativeImage image, NativeExecutionFunction caller,
        NativeFrameSlot[] slots, int callable, int pc, ref CallableFlow? flow)
    {
        if (pc < 0) return null;
        NativeExecutionInstruction producer = caller.Instructions[pc];
        if (producer.Result != callable) return null;
        int basis;
        NativeOffset? field = null;
        if (producer.Op == NativeOp.Load && producer.Handler == NativeInterpreter.OffsetLoadHandler)
        {
            basis = producer.A;
            NativeFieldLayout layout = caller.Operands[pc].Field;
            if (layout.Type.Kinds != 1UL << (int)NativeKind.Function) return null;
            field = layout.Offset;
        }
        else if (producer.Op == NativeOp.ComposeFunction && caller.Metadata[pc].Inputs is { Count: 3 } pieces)
        {
            basis = pieces[1];
            flow ??= new(caller);
            if (!caller.RawWritable[pieces[0]] || !caller.RawWritable[pieces[2]]
                || !flow.ConstantBefore(caller, image, pieces[0], pc, NativeKind.MachineInteger)
                || !flow.ConstantBefore(caller, image, pieces[2], pc, NativeKind.Null))
                return null;
        }
        else return null;
        if (slots[basis].Binding >= 0 || !slots[basis].ExactAddress || !caller.RawWritable[basis]) return null;
        flow ??= new(caller);
        return flow.Reads[callable] == 1 ? new(pc, basis, field) : null;
    }

    private sealed class CallableFlow
    {
        internal readonly int[] Reads;
        private readonly int[] definitions;
        private readonly int entryEnd;

        internal CallableFlow(NativeExecutionFunction caller)
        {
            Reads = new int[caller.Definition.Slots.Count];
            definitions = new int[Reads.Length];
            Array.Fill(definitions, -1);
            entryEnd = Array.FindIndex(caller.Metadata, static instruction => instruction.Op is
                NativeOp.Jump or NativeOp.Branch or NativeOp.NextIterator or NativeOp.Return or NativeOp.Unreachable);
            if (entryEnd < 0) entryEnd = caller.Metadata.Length;
            for (int pc = 0; pc < caller.Metadata.Length; pc++)
            {
                NativeInstruction instruction = caller.Metadata[pc];
                foreach (int slot in NativeInstructionFacts.Reads(instruction)) Reads[slot]++;
                if (instruction.Result >= 0)
                    definitions[instruction.Result] = definitions[instruction.Result] == -1 ? pc : -2;
            }
        }

        internal bool ConstantBefore(NativeExecutionFunction caller, NativeImage image, int slot, int pc, NativeKind kind)
        {
            int definition = definitions[slot];
            return definition >= 0 && definition < Math.Min(pc, entryEnd) && caller.Metadata[definition] is { Op: NativeOp.Constant } literal
                && image.Program.Constants[literal.A].Kind == kind;
        }
    }

    private static bool IsProfitableForAuto(NativeExecutionFunction function, NativeFastOperation[] operations)
    {
        NativeExecutionInstruction[] instructions = function.Instructions;
        int directTotal = 0;
        int directCompute = 0;
        int transitions = 0;
        bool inDirect = false;
        bool hasLoop = false;
        for (int pc = 0; pc < operations.Length; pc += Math.Max(1, instructions[pc].Span))
        {
            NativeFastOperation op = operations[pc];
            if (op == NativeFastOperation.Nop) continue;
            if (op == NativeFastOperation.Bridge)
            {
                if (inDirect) { transitions++; inDirect = false; }
                NativeExecutionInstruction insn = instructions[pc];
                if (insn.Op == NativeOp.Jump && insn.A <= pc) hasLoop = true;
                else if (insn.Op == NativeOp.Branch && (insn.B <= pc || insn.C <= pc)) hasLoop = true;
                continue;
            }
            inDirect = true;
            directTotal++;
            if (op is NativeFastOperation.MachineCall or NativeFastOperation.MachineFusedCall or NativeFastOperation.FloatRemainder or NativeFastOperation.Branch or NativeFastOperation.IsNull
                or NativeFastOperation.NumberNegate or NativeFastOperation.BoolNot
                or NativeFastOperation.BoolEqual or NativeFastOperation.BoolNotEqual
                or >= NativeFastOperation.NumberAdd || op.IsUnaryMath() || op.IsGuestAccess())
            {
                directCompute++;
            }
            if (op == NativeFastOperation.Branch)
            {
                NativeExecutionInstruction insn = instructions[pc];
                if (insn.B <= pc || insn.C <= pc) hasLoop = true;
            }
            else if (op == NativeFastOperation.Jump && instructions[pc].A <= pc)
            {
                hasLoop = true;
            }
        }
        if (directCompute == 0 || directTotal < 2) return false;
        return hasLoop
            ? directTotal >= transitions
            : directCompute >= 2 && directTotal >= 4 && directTotal >= transitions * 2;
    }

    private static NativeFastOperation Select(NativeImage image, NativeExecutionFunction executable, NativeExecutionInstruction instruction,
        int pc, NativeSiteShape? observed, NativeNumberForm executableForm, bool hasRoundingInstructions, bool nativeSequences, bool guestMemory)
    {
        NativeFunctionCode function = executable.Definition;
        bool Kind(int slot, TypeKind kind) => slot >= 0 && image.Program.Types[function.Slots[slot].Type].Pointee < 0
            && (image.Program.Types[function.Slots[slot].Type].Kind == kind);
        if (instruction.Handler == NativeInterpreter.NopHandler) return NativeFastOperation.Nop;
        if (instruction.Op == NativeOp.Jump) return NativeFastOperation.Jump;
        if (instruction.Op == NativeOp.Branch) return NativeFastOperation.Branch;
        if (guestMemory && executable.TryGuestAccess(pc, out _))
            return instruction.Op == NativeOp.Load ? NativeFastOperation.GuestLoad : NativeFastOperation.GuestStore;
        if (guestMemory && executable.TryGuestLayoutCheck(pc, out _)) return NativeFastOperation.GuestHasLayout;
        if (guestMemory && executable.TryGuestCopy(pc, in instruction)) return NativeFastOperation.GuestCopy;
        if (nativeSequences && instruction.Op == NativeOp.NextIterator && Kind(instruction.Result, TypeKind.Number)
            && image.Frame(executable.Index).Slots[instruction.Result].Binding < 0)
            return NativeFastOperation.SequenceNext;
        if (instruction.Op == NativeOp.CallNative && instruction.C < 0 && executable.Metadata[pc].Inputs is { Count: 1 } inputs
            && Kind(inputs[0], TypeKind.Number) && Kind(instruction.Result, TypeKind.Number))
            return executable.Metadata[pc].Symbol switch
            {
                "math.floor" when hasRoundingInstructions => NativeFastOperation.NumberFloor,
                "math.ceil" when hasRoundingInstructions => NativeFastOperation.NumberCeiling,
                "math.trunc" when hasRoundingInstructions => NativeFastOperation.NumberTruncate,
                "math.round_even" when hasRoundingInstructions => NativeFastOperation.NumberRoundEven,
                "math.abs" => NativeFastOperation.NumberAbs,
                "math.sqrt" => NativeFastOperation.NumberSqrt,
                _ => NativeFastOperation.Bridge
            };
        if (instruction.Op == NativeOp.Select && Kind(instruction.A, TypeKind.Bool))
        {
            if (Kind(instruction.Result, TypeKind.Number) && Kind(instruction.B, TypeKind.Number)
                && Kind(instruction.C, TypeKind.Number)) return NativeFastOperation.NumberSelect;
            if (Kind(instruction.Result, TypeKind.Bool) && Kind(instruction.B, TypeKind.Bool)
                && Kind(instruction.C, TypeKind.Bool)) return NativeFastOperation.BoolSelect;
        }
        if (instruction.Op == NativeOp.MachineCompare && Kind(instruction.A, TypeKind.Number) && Kind(instruction.B, TypeKind.Number))
            return NativeFastOperations.FloatingCompare((LlvmCmpPredicate)instruction.Immediate);
        if (instruction.Op == NativeOp.Return)
        {
            if (instruction.A < 0 || Kind(instruction.A, TypeKind.Null)) return NativeFastOperation.NullReturn;
            if (Kind(instruction.A, TypeKind.Number)) return NativeFastOperation.NumberReturn;
            if (Kind(instruction.A, TypeKind.Bool)) return NativeFastOperation.BoolReturn;
        }
        if (instruction.Op == NativeOp.Constant)
            return image.Program.Constants[instruction.A].Kind switch
            {
                NativeKind.Number => NativeFastOperation.NumberConstant,
                NativeKind.Bool => NativeFastOperation.BoolConstant,
                NativeKind.Null when Kind(instruction.Result, TypeKind.Null) => NativeFastOperation.NullConstant,
                _ => NativeFastOperation.Bridge
            };
        if (instruction.Op == NativeOp.Copy)
        {
            if (Kind(instruction.A, TypeKind.Number)) return NativeFastOperation.NumberCopy;
            if (Kind(instruction.A, TypeKind.Bool)) return NativeFastOperation.BoolCopy;
        }
        if (instruction.Op == NativeOp.Load && instruction.Handler == NativeInterpreter.FrameLoadHandler)
        {
            if (Kind(instruction.A, TypeKind.Number) && Kind(instruction.Result, TypeKind.Number)) return NativeFastOperation.NumberCopy;
            if (Kind(instruction.A, TypeKind.Bool) && Kind(instruction.Result, TypeKind.Bool)) return NativeFastOperation.BoolCopy;
        }
        if (instruction.Op == NativeOp.Move)
        {
            if (Kind(instruction.A, TypeKind.Number) && Kind(instruction.Result, TypeKind.Number)) return NativeFastOperation.NumberMove;
            if (Kind(instruction.A, TypeKind.Bool) && Kind(instruction.Result, TypeKind.Bool)) return NativeFastOperation.BoolMove;
        }
        if (instruction.Op == NativeOp.Store && instruction.Handler == NativeInterpreter.FrameStoreHandler)
        {
            if (Kind(instruction.A, TypeKind.Number) && Kind(instruction.Result, TypeKind.Number)) return NativeFastOperation.NumberCopy;
            if (Kind(instruction.A, TypeKind.Bool) && Kind(instruction.Result, TypeKind.Bool)) return NativeFastOperation.BoolCopy;
        }
        if (instruction.Op == NativeOp.Narrow && Kind(instruction.Result, TypeKind.Number)) return NativeFastOperation.NumberCopy;
        if (instruction.Op == NativeOp.Narrow && Kind(instruction.Result, TypeKind.Bool)) return NativeFastOperation.BoolCopy;
        if (instruction.Op == NativeOp.Unary)
            return (NativeUnary)instruction.Immediate switch
            {
                NativeUnary.Negate when Kind(instruction.A, TypeKind.Number) => NativeFastOperation.NumberNegate,
                NativeUnary.Not when Kind(instruction.A, TypeKind.Bool) => NativeFastOperation.BoolNot,
                NativeUnary.IsNull when instruction.A >= 0 && image.Program.Types[function.Slots[instruction.A].Type].Pointee < 0 => NativeFastOperation.IsNull,
                _ => NativeFastOperation.Bridge
            };
        if (instruction.Op != NativeOp.Binary) return NativeFastOperation.Bridge;
        if (Kind(instruction.A, TypeKind.Bool) && Kind(instruction.B, TypeKind.Bool)
            || observed is { First: NativeKind.Bool, Second: NativeKind.Bool })
        {
            return (NativeBinary)instruction.Immediate switch
            {
                NativeBinary.Equal => NativeFastOperation.BoolEqual,
                NativeBinary.NotEqual => NativeFastOperation.BoolNotEqual,
                _ => NativeFastOperation.Bridge
            };
        }
        if (!(Kind(instruction.A, TypeKind.Number) && Kind(instruction.B, TypeKind.Number)
            || observed is { First: NativeKind.Number, Second: NativeKind.Number })) return NativeFastOperation.Bridge;
        return (NativeBinary)instruction.Immediate switch
        {
            NativeBinary.Add => NativeFastOperation.NumberAdd,
            NativeBinary.Subtract => NativeFastOperation.NumberSubtract,
            NativeBinary.Multiply => NativeFastOperation.NumberMultiply,
            NativeBinary.Divide => NativeFastOperation.NumberDivide,
            NativeBinary.Remainder when executableForm.HasFlag(NativeNumberForm.UnsignedResult) => NativeFastOperation.NumberRemainder,
            NativeBinary.Remainder when Kind(instruction.A, TypeKind.Number) && Kind(instruction.B, TypeKind.Number)
                && NativeSystemMath.Remainder != 0 => NativeFastOperation.FloatRemainder,
            NativeBinary.Equal => NativeFastOperation.NumberEqual,
            NativeBinary.NotEqual => NativeFastOperation.NumberNotEqual,
            NativeBinary.Less => NativeFastOperation.NumberLess,
            NativeBinary.LessEqual => NativeFastOperation.NumberLessEqual,
            NativeBinary.Greater => NativeFastOperation.NumberGreater,
            NativeBinary.GreaterEqual => NativeFastOperation.NumberGreaterEqual,
            _ => NativeFastOperation.Bridge
        };
    }

    private readonly NativeCodePage codePage = page;
    internal NativeCodePage.Entry OpenEntry() => codePage.OpenEntry();
    internal unsafe int Run(nint context, int pc)
    {
        using var entry = OpenEntry();
        return ((delegate* unmanaged[Cdecl]<nint, int, int>)entry.Address)(context, pc);
    }
    public void Dispose() => codePage.Dispose();
}

internal readonly record struct NativeCodegenStatistics(long SelectionNanoseconds, long AllocationNanoseconds,
    long EmissionNanoseconds, long PublicationNanoseconds, int Regions, int Values, int Spills, int EliminatedStores)
{
    internal NativeRegisterPlanMetrics? RegisterPlanning { get; init; }
    internal int UnsignedValues { get; init; }
    internal int UnsignedOperations { get; init; }
    internal int InitializationChecksElided { get; init; }
    internal int InitializationWritesElided { get; init; }
    internal int DoubleComparisonSites { get; init; }
    internal int UnaryMathSites { get; init; }
    internal int GuestLoadSites { get; init; }
    internal int GuestStoreSites { get; init; }
    internal int GuestLayoutSites { get; init; }
    internal int GuestLayoutBranchSites { get; init; }
    internal int GuestCopySites { get; init; }
    internal int GuestViewSites { get; init; }
    internal int GuestViewsKeptWithBridge { get; init; }
    internal int GuestCopiesKeptWithBridge { get; init; }
    internal int GuestLayoutKeptWithBridge { get; init; }
    internal int GuestAccessKeptWithBridge { get; init; }
    internal int MachineCallSites { get; init; }
    internal int MachinePointerCallSites { get; init; }
}
