using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

/// <summary>A logical local frame backed by private slots or suspended caller inputs.</summary>
internal struct FrameSlots
{
    internal readonly struct Contiguous;
    internal readonly struct Split;
    internal readonly struct Raw;
    internal readonly struct RawWord;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsRaw<TLayout>() where TLayout : struct =>
        typeof(TLayout) == typeof(Raw) || typeof(TLayout) == typeof(RawWord);
    private Value[] values;
    private double[]? numbers;
    private int bias;
    // Borrowed numbers use the caller's offset; private numbers use a compact suffix.
    private int numberBias;
    private ClosureCaptures? captures;
    private int captureCount;
    private readonly int count;
    private readonly int firstNumericLocal;

    internal FrameSlots(Value[] values, int borrowedAt, ClosureCaptures? captures, int count, double[]? numbers, int firstNumericLocal)
    {
        this.values = values;
        this.numbers = numbers;
        this.count = count;
        this.firstNumericLocal = firstNumericLocal;
        this.captures = captures;
        captureCount = captures?.Count ?? 0;
        bias = Math.Max(0, borrowedAt) - captureCount;
        numberBias = bias;
        Borrowed = borrowedAt >= 0;
    }

    internal bool Borrowed { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Value? Peek<TLayout>(int slot) where TLayout : struct =>
        typeof(TLayout) != typeof(Contiguous) && (uint)slot < (uint)captureCount
            ? captures![slot] : values[bias + slot];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal double RawNumber(int slot) => numbers![numberBias + slot];

    internal bool TryNumber(int slot, bool readCell, out double number)
    {
        var value = Peek<Raw>(slot);
        if (value is null && numbers is not null)
        {
            number = RawNumber(slot);
            return true;
        }
        if (readCell && value is CellValue cell) value = cell.Data;
        if (value is NumberValue scalar)
        {
            number = scalar.Data;
            return true;
        }
        number = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Value Storage<TFrame, TLayout>(int slot, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        var value = Peek<TLayout>(slot);
        if (IsRaw<TLayout>() && value is null) return Materialize(slot, ref frame);
        return value!;
    }

    internal Value Materialize<TFrame>(int slot, ref TFrame frame) where TFrame : struct, RuntimeProfile.IFrame
    {
        int index = bias + slot;
        frame.Box();
        // A materialized argument changes representation, not the caller's value.
        return values[index] = new NumberValue(RawNumber(slot));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Store<TFrame, TLayout>(int slot, Value value, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        if (Peek<TLayout>(slot) is CellValue cell) cell.Data = value;
        else Initialize(slot, value, ref frame);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Initialize<TFrame>(int slot, Value value, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        if (Borrowed) Detach(slot, ref frame);
        values[slot] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryStoreRawNumber<TFrame>(int slot, ref OperandStack<TFrame> stack, ref TFrame frame,
        bool initialize) where TFrame : struct, RuntimeProfile.IFrame
    {
        if (firstNumericLocal < 0 || !stack.HasRawTop || (!initialize && Peek<Raw>(slot) is CellValue)) return false;
        InitializeNumber(slot, stack.PopRawNumber(), ref frame);
        frame.RawLocalWrite();
        return true;
    }

    internal void CommitNumber<TFrame>(int slot, double number, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        if (firstNumericLocal >= 0)
        {
            InitializeNumber(slot, number, ref frame);
            frame.NumericLocalCommit();
        }
        else
        {
            Initialize(slot, new NumberValue(number), ref frame);
            frame.Box();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StoreWord<TFrame>(int slot, bool raw, ref OperandStack<TFrame> stack, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        double payload;
        if (raw ? stack.TryPeekWordPayload(out payload) : TryPackExactWord(ref stack, out payload))
        {
            stack.Discard();
            InitializeNumber(slot, payload, ref frame);
            frame.ManagedWordLocal(write: true);
        }
        else Initialize(slot, stack.Pop(), ref frame);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void LoadWord<TFrame>(int slot, bool raw, ref OperandStack<TFrame> stack, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        if (Peek<Raw>(slot) is { } pending)
        {
            stack.Add(pending);
            return;
        }
        double payload = RawNumber(slot);
        if (raw) stack.AddWordPayload(payload);
        else stack.AddNumber((uint)BitConverter.DoubleToInt64Bits(payload));
        frame.ManagedWordLocal(write: false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryPackExactWord<TFrame>(ref OperandStack<TFrame> stack, out double payload)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        bool ready = stack.TryPeekNumber(0, out double widened);
        payload = BitConverter.Int64BitsToDouble((uint)widened);
        return ready;
    }

    private void InitializeNumber<TFrame>(int slot, double number, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        if (Borrowed) Detach(slot, ref frame);
        if (numbers is null)
        {
            numberBias = -firstNumericLocal;
            numbers = AllocateNumberSlots(count - firstNumericLocal, ref frame);
        }
        numbers[numberBias + slot] = number;
        values[slot] = null!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal CellValue Capture<TFrame, TLayout>(int slot, BoundScopePlaces places, int codeType, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        var value = Storage<TFrame, TLayout>(slot, ref frame);
        if (value is CellValue cell) return cell;
        var promoted = new CellValue(value) { CodeStorage = places.Storage(codeType) };
        Initialize(slot, promoted, ref frame);
        return promoted;
    }

    private void Detach<TFrame>(int replacement, ref TFrame frame) where TFrame : struct, RuntimeProfile.IFrame
    {
        var copy = new Value[count];
        captures?.CopyTo(copy);
        int rawStart = firstNumericLocal;
        double[]? rawCopy = null;
        if (firstNumericLocal >= 0 && numbers is not null)
        {
            // A live borrowed raw argument may precede every explicit local write.
            for (int i = captureCount; i < rawStart; i++)
                if (i != replacement && values[bias + i] is null) { rawStart = i; break; }
            rawCopy = AllocateNumberSlots(count - rawStart, ref frame);
        }
        if (numbers is null)
            values.AsSpan(bias + captureCount, count - captureCount).CopyTo(copy.AsSpan(captureCount));
        else
        {
            for (int i = captureCount; i < count; i++)
            {
                if (i == replacement) continue;
                if (values[bias + i] is { } value) copy[i] = value;
                else if (rawCopy is not null) rawCopy[i - rawStart] = RawNumber(i);
                else
                {
                    copy[i] = new NumberValue(RawNumber(i));
                    frame.Box();
                }
            }
        }
        values = copy;
        numbers = rawCopy;
        numberBias = -rawStart;
        captures = null;
        captureCount = 0;
        bias = 0;
        Borrowed = false;
        frame.Detach();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double[] AllocateNumberSlots<TFrame>(int slots, ref TFrame frame) where TFrame : struct, RuntimeProfile.IFrame
    {
        double[] allocated = new double[slots];
        frame.NumberSidecarAllocated(slots);
        return allocated;
    }
}
