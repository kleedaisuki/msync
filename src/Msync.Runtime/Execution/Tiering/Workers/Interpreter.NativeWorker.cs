using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private const int NativeWorkerWarmup = 32;
    private IndexedWorkerSite?[]? indexedWorkers;

    private bool TryNativeWorkerCall<TFrame>(Module image, int target, FunctionValue function, int arity,
        ref OperandStack<TFrame> stack, CancellationToken cancellationToken, out ValueTask<Value>? continuation)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        continuation = null;
        var sites = indexedWorkers ??= new IndexedWorkerSite?[image.Functions.Count];
        var site = sites[target] ??= new();
        if (site.Visits < NativeWorkerWarmup && ++site.Visits < NativeWorkerWarmup) return false;
        if (site.Prepare(executionTargets, image, target) is not { } plan
            || Volatile.Read(ref guestTransferWork) != 0)
            return false;

        int captures = image.Functions[target].CaptureCount;
        if ((function.Captures?.Count ?? 0) != captures)
            return NativeWorkerMiss(image, target);
        foreach (int written in plan.WritableParameters)
            if (stack.PeekReference(arity - 1 - (written - captures)) is CellValue { Data: CellValue })
                return NativeWorkerMiss(image, target);

        InlineWorkerValues inline = default;
        Span<WorkerValue> values = plan.Accesses.Length <= InlineWorkerValues.Capacity
            ? ((Span<WorkerValue>)inline)[..plan.Accesses.Length] : new WorkerValue[plan.Accesses.Length];
        if (!PrepareWorkerValues(image, plan, values, function, arity, captures, ref stack))
            return NativeWorkerMiss(image, target);

        Span<ulong> arguments = plan.Inputs.Length <= InlineWorkerValues.Capacity
            ? stackalloc ulong[plan.Inputs.Length] : new ulong[plan.Inputs.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            var input = plan.Inputs[i];
            if (!values[input.Access].TryArgument(input.Type, input.ShiftCount, out arguments[i]))
                return NativeWorkerMiss(image, target);
        }

        ReadOnlySpan<byte> rows = [];
        Span<byte> writableRows = [];
        NativeWorker.Version? version;
        if (plan.Storage >= 0)
        {
            var storage = values[plan.Storage].Reference;
            if (plan.UsesArenaRows)
            {
                if (storage is not SetArena<double> arena || !ArenaIndicesFit(plan, arguments, arena.Rows.Length))
                    return NativeWorkerMiss(image, target);
                version = site.Code(plan.Worker, profile, image, target);
                if (version is null) return false;
                if (plan.WritesArenaRows) writableRows = MemoryMarshal.AsBytes(arena.Rows);
                else rows = MemoryMarshal.AsBytes(arena.Rows);
            }
            else
            {
                if (storage is not DenseVectorValue vector
                    || !ScalarIndicesFit(plan, arguments, (int)values[plan.StorageCountAccess].Number))
                    return NativeWorkerMiss(image, target);
                version = site.Code(plan.Worker, profile, image, target);
                if (version is null) return false;
                if (!vector.TryBorrowNumbers(out var actual)) return NativeWorkerMiss(image, target);
                writableRows = MemoryMarshal.AsBytes(actual);
            }
        }
        else version = site.Code(plan.Worker, profile, image, target);

        if (version is null) return false;
        if (image.Functions[target].CodeSignature?.RequiredStatics is { Count: > 0 } required)
            staticScopes.RequireReady(required);
        Span<ulong> outputs = plan.SpillCount <= InlineWorkerValues.Capacity
            ? stackalloc ulong[plan.SpillCount] : new ulong[plan.SpillCount];
        ulong result = writableRows.IsEmpty
            ? version.Run(rows, arguments, cancellationToken, profile, image, target, plan, outputs)
            : version.RunWritable(writableRows, arguments, cancellationToken, profile, image, target, plan, outputs);
        if (plan.WritesScalarStorage)
        {
            stack.CompleteCall(arity, Value.Null);
            return true;
        }
        int register = plan.Worker.Result;
        if (plan.SpillCount != 0)
        {
            if (result != 0)
            {
                var exit = plan.Exits[(int)result - 1];
                profile?.NativeWorkerPrefixExit(image, target);
                continuation = ResumeNativePrefix(image, target, function, arity, plan.Worker, exit,
                    outputs, values, ref stack, cancellationToken);
                return true;
            }
            if (!plan.ReturnsNull)
            {
                register = plan.GuestResult;
                result = outputs[0];
            }
        }
        if (plan.ReturnsNull)
        {
            stack.CompleteCall(arity, Value.Null);
            return true;
        }
        switch (plan.Worker.Slots[register])
        {
            case NativeWorkerType.Bool: stack.CompleteCall(arity, result != 0 ? Value.True : Value.False); break;
            case NativeWorkerType.Index: stack.CompleteNumberCall(arity, (int)result); break;
            case NativeWorkerType.Word32: stack.CompleteNumberCall(arity, (uint)result); break;
            case NativeWorkerType.Number: stack.CompleteNumberCall(arity, BitConverter.Int64BitsToDouble((long)result)); break;
        }
        return true;
    }

    private static bool ScalarIndicesFit(IndexedMethodPlan plan, ReadOnlySpan<ulong> arguments, int count)
    {
        if (count > plan.MaximumRows) return false;
        for (int i = 0; i < plan.Inputs.Length; i++)
            if (plan.Inputs[i].Type == NativeWorkerType.Index && plan.Inputs[i].Access != plan.StorageCountAccess
                && arguments[i] >= (ulong)count) return false;
        return true;
    }

    private static bool ArenaIndicesFit(IndexedMethodPlan plan, ReadOnlySpan<ulong> arguments, int extent)
    {
        for (int i = 0; i < arguments.Length; i++)
            if (plan.Inputs[i].Type == NativeWorkerType.Index && arguments[i] >= (ulong)extent) return false;
        return true;
    }

    private ValueTask<Value> ResumeNativePrefix<TFrame>(Module image, int target, FunctionValue function, int arity,
        NativeWorkerPlan worker, IndexedMethodPlan.PrefixExit exit, ReadOnlySpan<ulong> outputs,
        ReadOnlySpan<WorkerValue> values,
        ref OperandStack<TFrame> stack, CancellationToken cancellationToken) where TFrame : struct, RuntimeProfile.IFrame
    {
        var captures = function.Captures ?? [];
        var body = image.Functions[target];
        var locals = InitializeFrame(body, captures);
        var arguments = stack.CopyArguments(arity, locals, body.CaptureCount);
        for (int i = 0; i < exit.Locals.Length; i++)
        {
            var location = exit.Locals[i];
            if (location.Access >= 0)
            {
                var value = values[location.Access];
                locals[location.Local] = value.Reference ?? new NumberValue(value.Number);
                continue;
            }
            ulong bits = outputs[i];
            locals[location.Local] = worker.Slots[location.Register] switch
            {
                NativeWorkerType.Bool => bits == 0 ? Value.False : Value.True,
                NativeWorkerType.Index => new NumberValue((int)bits),
                NativeWorkerType.Word32 => new NumberValue((uint)bits),
                _ => new NumberValue(BitConverter.Int64BitsToDouble((long)bits)),
            };
        }
        return RunCodeAsync(target, locals, exit.Pc, cancellationToken, captures, callerArguments: arguments);
    }

    private bool NativeWorkerMiss(Module image, int target)
    {
        profile?.NativeWorkerGuardMiss(image, target);
        return false;
    }

    private bool PrepareWorkerValues<TFrame>(Module image, IndexedMethodPlan plan, Span<WorkerValue> values,
        FunctionValue function, int arity, int captures, ref OperandStack<TFrame> stack) where TFrame : struct, RuntimeProfile.IFrame
    {
        BoundScopePlaces? places = null;
        for (int i = 0; i < values.Length; i++)
        {
            var access = plan.Accesses[i];
            switch (access.Kind)
            {
                case IndexedMethodPlan.AccessKind.Slot:
                    if (access.Operand < captures)
                    {
                        values[i] = new(ReadValue(function.Captures![access.Operand]));
                        break;
                    }
                    int depth = arity - 1 - (access.Operand - captures);
                    values[i] = stack.TryPeekNumber(depth, out double number)
                        ? new(null, number) : new(ReadValue(ReadValue(stack.PeekReference(depth)!)));
                    break;
                case IndexedMethodPlan.AccessKind.Member:
                    if (values[access.Receiver].Reference is not ScopeValue scope
                        || !Places(image, ref places).TryField(scope, access.Operand, out int offset))
                        return false;
                    values[i] = new(scope.GetOperandField(offset, out double member), member);
                    break;
                case IndexedMethodPlan.AccessKind.AdmittedScope:
                    if (values[access.Receiver].Reference is not ScopeValue selected
                        || !Places(image, ref places).IsAdmitted(selected, access.Operand))
                        return false;
                    values[i] = values[access.Receiver];
                    break;
                case IndexedMethodPlan.AccessKind.Count:
                    if (values[access.Receiver].Reference is not DenseVectorValue vector) return false;
                    values[i] = new(null, vector.Count);
                    break;
                case IndexedMethodPlan.AccessKind.ArenaRoot:
                case IndexedMethodPlan.AccessKind.ArenaSize:
                    if (values[access.Receiver].Reference is not SetArena arena) return false;
                    values[i] = new(null, access.Kind == IndexedMethodPlan.AccessKind.ArenaRoot ? arena.Root : arena.Count);
                    break;
                case IndexedMethodPlan.AccessKind.Capture:
                    if (values[access.Receiver].Reference is not FunctionValue { Captures: { } cells }
                        || (uint)access.Operand >= (uint)cells.Count) return false;
                    values[i] = new(ReadValue(cells[access.Operand]));
                    break;
            }
        }
        foreach (var guard in plan.Calls)
        {
            if (values[guard.Callee].Reference is not FunctionValue { NativeName: null } selected
                || (selected.Captures?.Count ?? 0) != guard.Captures
                || guard.Identity is int identity && selected.Function != identity)
                return false;
        }
        return true;
    }

    private readonly record struct WorkerValue(Value? Reference, double Number = 0)
    {
        internal bool TryArgument(NativeWorkerType type, bool shiftCount, out ulong argument)
        {
            argument = 0;
            if (type == NativeWorkerType.Bool)
            {
                if (Reference is not BoolValue boolean) return false;
                argument = boolean.Data ? 1UL : 0UL;
                return true;
            }

            double value;
            if (Reference is null) value = Number;
            else if (Reference is NumberValue numeric) value = numeric.Data;
            else return false;
            if (type == NativeWorkerType.Number)
            {
                argument = (ulong)BitConverter.DoubleToInt64Bits(value);
                return true;
            }
            if (type == NativeWorkerType.Word32)
            {
                if (!Word32.TryOperand(value, out uint word) || shiftCount && word > 31) return false;
                argument = word;
                return true;
            }

            int index = (int)value;
            if (index < 0 || index != value || index == 0 && double.IsNegative(value)) return false;
            argument = (uint)index;
            return true;
        }
    }

    [InlineArray(Capacity)]
    private struct InlineWorkerValues
    {
        internal const int Capacity = 32;
        private WorkerValue first;
    }

    private sealed class IndexedWorkerSite
    {
        internal int Visits;
        private bool imported;
        private bool attempted;
        private IndexedMethodPlan? plan;
        private NativeWorker.Version? version;

        internal IndexedMethodPlan? Prepare(ExecutionTargets targets, Module image, int target)
        {
            if (!imported)
            {
                imported = true;
                targets.TryExecutionWorker(image, target, out plan);
            }
            return plan;
        }

        internal NativeWorker.Version? Code(NativeWorkerPlan worker, RuntimeProfile? profile, Module image, int target)
        {
            if (attempted) return version;
            attempted = true;
            bool counted = profile is not null;
            long started = counted ? Stopwatch.GetTimestamp() : 0;
            bool[]? physical = null;
            if (counted) worker = NativeWorker.Observe(worker, out physical);
            if (NativeWorker.Compile(worker, counted, out int bytes) is not { } page) return null;
            if (profile is not null)
                profile.DirectSourceFrame(image, target).JitCompiled(Stopwatch.GetElapsedTime(started).Ticks * 100, bytes);
            return version = new(worker, page, counted, physical);
        }
    }
}
