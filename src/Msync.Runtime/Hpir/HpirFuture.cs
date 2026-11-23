using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Native;

namespace Msync.Runtime.Hpir;

internal readonly record struct HpirScalarInput<T>(nint Pending, T Value) where T : unmanaged
{
    internal static HpirScalarInput<T> Ready(T value) => new(0, value);
    internal static HpirScalarInput<T> Deferred(nint pointer) => new(pointer, default);
}

// Only the HPIR mutator writes a completion cell. Child pointers are exact
// heap bases; a raw T operand occupies its own field even in a mixed carrier.
internal static unsafe partial class HpirFuture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        internal nint Left;
        internal nint Right;
        internal int Job;
        internal HpirFutureOperation Operation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Cell<T> where T : unmanaged
    {
        internal Header Header;
        internal T Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Lifted<TInput, TResult> where TInput : unmanaged where TResult : unmanaged
    {
        internal Cell<TResult> Completion;
        internal TInput Left;
        internal TInput Right;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NumberFromText
    {
        internal Cell<double> Completion;
        internal int Function;
        internal int Instruction;
    }

    internal static HpirLayout CompletionLayout<T>() where T : unmanaged => new(sizeof(Cell<T>), 8, []);
    internal static HpirLayout LiftLayout<TInput, TResult>() where TInput : unmanaged where TResult : unmanaged =>
        new(sizeof(Lifted<TInput, TResult>), 8, [0, 8]);

    internal static void PublishNumber(nint pointer, HpirHostResult<double> source) =>
        Publish(pointer, source, HpirFutureOperation.HostNumber);

    internal static void PublishBool(nint pointer, HpirHostResult<byte> source) =>
        Publish(pointer, source, HpirFutureOperation.HostBool);

    internal static HpirTransferTextInput BorrowBoolText(nint pointer, HpirWork work)
    {
        var cell = (Cell<byte>*)pointer;
        if (cell->Header.Operation == HpirFutureOperation.Ready)
            return new(HpirTransferTextInput.BoolText(cell->Value));
        System.Diagnostics.Debug.Assert(cell->Header.Operation == HpirFutureOperation.HostBool);
        return new(null, Bool: work.UseInEffect<byte>(cell->Header.Job));
    }

    internal static bool ReadyNumber(nint pointer, out double value)
    {
        var cell = (Cell<double>*)pointer;
        bool ready = cell->Header.Operation == HpirFutureOperation.Ready;
        value = ready ? cell->Value : default;
        return ready;
    }

    internal static void PublishGuestNumber(nint pointer, int child) =>
        Publish(pointer, HpirHostResult<double>.Pending(child), HpirFutureOperation.GuestNumber);

    internal static void PublishProcessExit(nint pointer, nint process) =>
        *(Cell<double>*)pointer = new() { Header = new() { Operation = HpirFutureOperation.ProcessExit, Left = process } };

    internal static void PublishProcessSuccess(nint pointer, nint process) =>
        *(Cell<byte>*)pointer = new() { Header = new() { Operation = HpirFutureOperation.ProcessSuccess, Left = process } };

    internal static void PublishTextIsNull(nint pointer, nint text) =>
        *(Cell<byte>*)pointer = new() { Header = new() { Operation = HpirFutureOperation.TextIsNull, Left = text } };

    internal static void PublishTextNumber(nint pointer, nint text, int function, int instruction) =>
        *(NumberFromText*)pointer = new()
        {
            Completion = new() { Header = new() { Operation = HpirFutureOperation.TextParseNumber, Left = text } },
            Function = function,
            Instruction = instruction
        };

    private static void Publish<T>(nint pointer, HpirHostResult<T> source, HpirFutureOperation operation) where T : unmanaged
    {
        // Every explicit producer evaluation retains its own cell, including
        // when that producer's host operation has already finished.
        *(Cell<T>*)pointer = new()
        {
            Header = new() { Operation = source.Ready ? HpirFutureOperation.Ready : operation, Job = source.JobId },
            Value = source.Value
        };
    }

    internal static void LiftNumber(nint pointer, HpirFutureOperation operation,
        HpirScalarInput<double> left, HpirScalarInput<double> right, HpirWorkSite site) =>
        Lift<double, double>(pointer, operation, left, right, site);

    internal static void LiftComparison(nint pointer, HpirFutureOperation operation,
        HpirScalarInput<double> left, HpirScalarInput<double> right, HpirWorkSite site) =>
        Lift<double, byte>(pointer, operation, left, right, site);

    internal static void LiftBool(nint pointer, HpirFutureOperation operation,
        HpirScalarInput<byte> left, HpirScalarInput<byte> right, HpirWorkSite site) =>
        Lift<byte, byte>(pointer, operation, left, right, site);

    private static void Lift<TInput, TResult>(nint pointer, HpirFutureOperation operation,
        HpirScalarInput<TInput> left, HpirScalarInput<TInput> right, HpirWorkSite site)
        where TInput : unmanaged where TResult : unmanaged
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.future.lift");
        if (trace is not null)
        {
            trace.SetAttribute("operation", operation.ToString());
            trace.SetMetric("future.dependencies", (left.Pending == 0 ? 0 : 1) + (right.Pending == 0 ? 0 : 1));
        }
        *(Lifted<TInput, TResult>*)pointer = new()
        {
            Completion = new() { Header = new() { Left = left.Pending, Right = right.Pending, Operation = operation } },
            Left = left.Value,
            Right = right.Value
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T Demand<T>(nint pointer, HpirWork? owner, HpirWorkSite site, CancellationToken token,
        HpirTextDemandContext text = default) where T : unmanaged
    {
        var cell = (Cell<T>*)pointer;
        if (cell->Header.Operation != HpirFutureOperation.Ready)
            _ = Resolve(&cell->Header, owner, site, token, text, mayPark: false);
        return cell->Value;
    }

    internal static Task? PrepareGuestDemand(nint pointer, HpirWork? owner, HpirWorkSite site,
        CancellationToken token, HpirTextDemandContext text) =>
        Pending(pointer) ? Resolve((Header*)pointer, owner, site, token, text, mayPark: true) : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task? Resolve(Header* target, HpirWork? owner, HpirWorkSite site, CancellationToken token,
        HpirTextDemandContext text, bool mayPark)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.future.demand");
        Span<nint> inline = stackalloc nint[32];
        var path = new DemandPath(inline);
        int operations = 0, host = 0, shared = 0;
        try
        {
            path.Push((nint)target);
            while (!path.Empty)
            {
                var cell = (Header*)path.Top;
                var operation = cell->Operation;
                if (operation == HpirFutureOperation.Ready)
                {
                    // A host wait can advance another consumer of this same graph.
                    path.Pop();
                    ++shared;
                    continue;
                }
                bool externalProjection = operation is HpirFutureOperation.TextIsNull or HpirFutureOperation.TextParseNumber
                    or HpirFutureOperation.ProcessExit or HpirFutureOperation.ProcessSuccess;
                nint child = externalProjection ? 0 : Pending(cell->Left) ? cell->Left : Pending(cell->Right) ? cell->Right : 0;
                if (child == 0 && cell->Operation == HpirFutureOperation.MathTriple
                    && Pending(((MathTriple*)cell)->ThirdDependency))
                    child = ((MathTriple*)cell)->ThirdDependency;
                if (child != 0) { path.Push(child); continue; }
                if (mayPark && BlockingDependency(cell, owner, text) is { } waiting)
                {
                    trace?.SetAttribute("future.outcome", "parked");
                    return waiting;
                }
                if ((operations++ & 255) == 0) token.ThrowIfCancellationRequested();
                if (operation is >= HpirFutureOperation.HostNumber and <= HpirFutureOperation.ProcessSuccess)
                    host++;
                bool mayAdvanceGuest = externalProjection
                    || operation is HpirFutureOperation.HostNumber or HpirFutureOperation.HostBool or HpirFutureOperation.GuestNumber;
                if (mayAdvanceGuest && owner?.Guests is not null)
                    CompleteShared(cell, owner, trace, token, text, path.Active);
                else Complete(cell, owner, trace, token, text);
                path.Pop();
            }
            return null;
        }
        catch (InvalidOperationException error)
        {
            var failure = new MsyncException(error.Message, site.Source, error);
            trace?.Fail(failure);
            throw failure;
        }
        catch (Exception error) { trace?.Fail(error); throw; }
        finally
        {
            trace?.SetMetric("future.operations_materialized", operations);
            trace?.SetMetric("future.host_completions", host);
            trace?.SetMetric("future.shared_completions_reused", shared);
            trace?.SetMetric("future.max_dependency_depth", path.Peak);
            path.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompleteShared(Header* cell, HpirWork work, RuntimeTimelineScope? trace,
        CancellationToken token, HpirTextDemandContext text, ReadOnlySpan<nint> path)
    {
        // Another consumer can complete an ancestor and clear its edges while
        // this host wait pumps guest work. Keep the active path rooted until it returns.
        fixed (nint* items = path)
        {
            HpirHeap.CompletionPathRoots roots;
            using var retained = text.Heap.HoldCompletionPath(&roots, items, path.Length);
            Complete(cell, work, trace, token, text);
        }
    }

    private static bool Pending(nint pointer) => pointer != 0 && ((Header*)pointer)->Operation != HpirFutureOperation.Ready;

    private static Task? BlockingDependency(Header* header, HpirWork? work, HpirTextDemandContext text) => header->Operation switch
    {
        HpirFutureOperation.HostNumber or HpirFutureOperation.HostBool => work!.Waiting(header->Job),
        HpirFutureOperation.ProcessExit or HpirFutureOperation.ProcessSuccess =>
            work!.Waiting(((HpirProcessHandle*)header->Left)->Job),
        HpirFutureOperation.TextIsNull or HpirFutureOperation.TextParseNumber => text.Waiting(header->Left, work),
        HpirFutureOperation.GuestNumber => work!.Guests!.Waiting(header->Job),
        _ => null
    };

    private static void Complete(Header* header, HpirWork? owner, RuntimeTimelineScope? trace, CancellationToken token,
        HpirTextDemandContext text)
    {
        switch (header->Operation)
        {
            case HpirFutureOperation.HostNumber:
                ((Cell<double>*)header)->Value = owner!.Demand<double>(header->Job, token, trace);
                break;
            case HpirFutureOperation.HostBool:
                ((Cell<byte>*)header)->Value = owner!.Demand<byte>(header->Job, token, trace);
                break;
            case HpirFutureOperation.GuestNumber:
                ((Cell<double>*)header)->Value = owner!.Guests!.Demand(header->Job, text, token);
                break;
            case HpirFutureOperation.ProcessExit:
                ((Cell<double>*)header)->Value = HpirProcessHandle.Demand(header->Left, owner!, token, trace).ExitCode;
                break;
            case HpirFutureOperation.ProcessSuccess:
                ((Cell<byte>*)header)->Value = HpirProcessHandle.Demand(header->Left, owner!, token, trace).ExitCode == 0
                    ? (byte)1 : (byte)0;
                break;
            case HpirFutureOperation.TextIsNull:
                ((Cell<byte>*)header)->Value = text.Demand(header->Left, owner, trace, token) == 0 ? (byte)1 : (byte)0;
                break;
            case HpirFutureOperation.ScalarIsNull:
                // The scalar dependency has completed; its payload is never Null.
                ((Cell<byte>*)header)->Value = 0;
                break;
            case HpirFutureOperation.TextParseNumber:
                var projection = (NumberFromText*)header;
                nint contents = text.Demand(header->Left, owner, trace, token);
                projection->Completion.Value = text.Parse(contents, projection->Function, projection->Instruction);
                break;
            case HpirFutureOperation.MathNumber:
                CompleteMath((MathNode<double>*)header, text);
                break;
            case HpirFutureOperation.MathBool:
                CompletePredicate((MathNode<byte>*)header, text);
                break;
            case HpirFutureOperation.MathTriple:
                CompleteMath((MathTriple*)header, text);
                break;
            case >= HpirFutureOperation.Add and <= HpirFutureOperation.Negate:
                Number((Lifted<double, double>*)header);
                break;
            case >= HpirFutureOperation.Equal and <= HpirFutureOperation.GreaterEqual:
                Compare((Lifted<double, byte>*)header);
                break;
            case HpirFutureOperation.Not or HpirFutureOperation.BoolEqual or HpirFutureOperation.BoolNotEqual:
                Boolean((Lifted<byte, byte>*)header);
                break;
            default:
                throw new InvalidOperationException($"Invalid HPIR completion operation {(byte)header->Operation}.");
        }
        // The payload is now complete: the graph must not retain its inputs.
        *header = default;
    }

    private static T Value<T>(nint pointer, T ready) where T : unmanaged =>
        pointer == 0 ? ready : ((Cell<T>*)pointer)->Value;

    private static void Number(Lifted<double, double>* node)
    {
        var header = node->Completion.Header;
        double a = Value(header.Left, node->Left), b = Value(header.Right, node->Right);
        node->Completion.Value = EvaluateNumber(header.Operation, a, b);
    }

    internal static double EvaluateNumber(HpirFutureOperation operation, double a, double b) =>
        operation switch
        {
            HpirFutureOperation.Add => a + b,
            HpirFutureOperation.Subtract => a - b,
            HpirFutureOperation.Multiply => a * b,
            HpirFutureOperation.Divide => a / b,
            HpirFutureOperation.Remainder => a % b,
            HpirFutureOperation.Power => Math.Pow(a, b),
            HpirFutureOperation.Negate => -a,
            _ => throw new InvalidOperationException("Invalid HPIR Number completion operation")
        };

    private static void Compare(Lifted<double, byte>* node)
    {
        var header = node->Completion.Header;
        double a = Value(header.Left, node->Left), b = Value(header.Right, node->Right);
        node->Completion.Value = EvaluateComparison(header.Operation, a, b);
    }

    internal static byte EvaluateComparison(HpirFutureOperation operation, double a, double b)
    {
        bool value = operation switch
        {
            HpirFutureOperation.Equal => a == b,
            HpirFutureOperation.NotEqual => a != b,
            HpirFutureOperation.Less => a < b,
            HpirFutureOperation.LessEqual => a <= b,
            HpirFutureOperation.Greater => a > b,
            HpirFutureOperation.GreaterEqual => a >= b,
            _ => throw new InvalidOperationException("Invalid HPIR Number predicate completion operation")
        };
        return value ? (byte)1 : (byte)0;
    }

    private static void Boolean(Lifted<byte, byte>* node)
    {
        var header = node->Completion.Header;
        byte a = Value(header.Left, node->Left), b = Value(header.Right, node->Right);
        node->Completion.Value = EvaluateBool(header.Operation, a, b);
    }

    internal static byte EvaluateBool(HpirFutureOperation operation, byte a, byte b)
    {
        bool value = operation switch
        {
            HpirFutureOperation.Not => a == 0,
            HpirFutureOperation.BoolEqual => a == b,
            HpirFutureOperation.BoolNotEqual => a != b,
            HpirFutureOperation.ScalarIsNull => a != 0,
            _ => throw new InvalidOperationException("Invalid HPIR Bool completion operation")
        };
        return value ? (byte)1 : (byte)0;
    }

    private ref struct DemandPath(Span<nint> items)
    {
        private Span<nint> items = items;
        private nint[]? rented;
        private int count;
        internal bool Empty => count == 0;
        internal nint Top => items[count - 1];
        internal ReadOnlySpan<nint> Active => items[..count];
        internal int Peak { get; private set; }

        internal void Push(nint pointer)
        {
            if (count == items.Length) Grow();
            items[count++] = pointer;
            Peak = Math.Max(Peak, count);
        }

        internal void Pop() => count--;

        private void Grow()
        {
            nint[] next = ArrayPool<nint>.Shared.Rent(checked(2 * items.Length));
            items.CopyTo(next);
            Dispose();
            rented = next;
            items = next;
        }

        internal void Dispose()
        {
            if (rented is not null) ArrayPool<nint>.Shared.Return(rented);
        }
    }
}
