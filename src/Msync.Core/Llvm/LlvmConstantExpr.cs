// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Constants.h/.cpp,
// ConstantsContext.h ConstantExprKeyType / ConstantUniqueMap.
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Msync.Core.Llvm;

public readonly record struct LlvmConstantExprRange(LlvmAPInt Lower, LlvmAPInt Upper);

public sealed partial class LlvmContext
{
    internal readonly Dictionary<LlvmConstantExprKey, LlvmConstantExpr> ExpressionConstants = [];
    // Project diagnostics sink, not a semantic LLVMContext option.
    public Dictionary<string, ulong>? ConstantExpressionMetrics { get; set; }
}

internal readonly struct LlvmConstantExprKey : IEquatable<LlvmConstantExprKey>
{
    internal LlvmConstantExprKey(LlvmType type, LlvmOpcode opcode, IReadOnlyList<LlvmConstant> operands,
        uint flags, LlvmType? sourceElementType, LlvmConstantExprRange? inRange, IReadOnlyList<int>? shuffleMask = null)
    {
        Type = type; Opcode = opcode; Operands = operands.ToArray(); Flags = flags;
        SourceElementType = sourceElementType; InRange = inRange; ShuffleMask = shuffleMask?.ToArray() ?? [];
        HashCode hash = new();
        hash.Add(RuntimeHelpers.GetHashCode(type)); hash.Add(opcode); hash.Add(flags);
        hash.Add(sourceElementType is null ? 0 : RuntimeHelpers.GetHashCode(sourceElementType)); hash.Add(inRange);
        foreach (var operand in Operands) hash.Add(RuntimeHelpers.GetHashCode(operand));
        foreach (int element in ShuffleMask) hash.Add(element);
        hashCode = hash.ToHashCode();
    }
    internal LlvmType Type { get; }
    internal LlvmOpcode Opcode { get; }
    internal LlvmConstant[] Operands { get; }
    internal uint Flags { get; }
    internal LlvmType? SourceElementType { get; }
    internal LlvmConstantExprRange? InRange { get; }
    internal int[] ShuffleMask { get; }
    private readonly int hashCode;
    public bool Equals(LlvmConstantExprKey other)
    {
        if (Type != other.Type || Opcode != other.Opcode || Flags != other.Flags ||
            SourceElementType != other.SourceElementType || InRange != other.InRange || Operands.Length != other.Operands.Length || !ShuffleMask.AsSpan().SequenceEqual(other.ShuffleMask)) return false;
        for (int index = 0; index < Operands.Length; ++index)
            if (!ReferenceEquals(Operands[index], other.Operands[index])) return false;
        return true;
    }
    public override bool Equals(object? value) => value is LlvmConstantExprKey other && Equals(other);
    public override int GetHashCode() => hashCode;
}

public sealed partial class LlvmConstantExpr : LlvmConstant
{
    private LlvmConstantExprKey key;
    private readonly IReadOnlyList<int> shuffleMask;
    private LlvmConstantExpr(LlvmConstantExprKey key)
        : base(key.Type, (byte)LlvmValueKind.ConstantExprVal, key.Operands.Cast<LlvmValue?>().ToArray())
    {
        this.key = key;
        shuffleMask = key.ShuffleMask.Length == 0 ? Array.Empty<int>() : Array.AsReadOnly(key.ShuffleMask);
    }

    public LlvmOpcode Opcode => key.Opcode;
    public uint Flags => key.Flags;
    public bool IsCast => Opcode is >= LlvmOpcode.Trunc and <= LlvmOpcode.AddrSpaceCast;
    public bool HasNoUnsignedWrap => (Flags & 1) != 0;
    public bool HasNoSignedWrap => (Flags & 2) != 0;
    public LlvmType? SourceElementType => key.SourceElementType;
    public LlvmType? ResultElementType => Opcode == LlvmOpcode.GetElementPtr
        ? LlvmGetElementPtrInstruction.GetIndexedType(SourceElementType!, Indices.Cast<LlvmValue>().ToArray()) : null;
    public LlvmGepNoWrapFlags NoWrapFlags => LlvmGepNoWrapFlags.FromRaw((byte)Flags);
    public LlvmConstantExprRange? InRange => key.InRange;
    public IReadOnlyList<int> ShuffleMask => shuffleMask;
    public LlvmConstant PointerOperand => GetConstantOperand(0);
    public int IndexCount => OperandCount - 1;
    public IEnumerable<LlvmConstant> Indices => Enumerable.Range(1, IndexCount).Select(GetConstantOperand);
    public LlvmConstant GetConstantOperand(int index) => (LlvmConstant)GetOperand(index)!;
    public LlvmConstant GetIndex(int index) => GetConstantOperand(index + 1);

    public static bool IsDesirableBinOp(LlvmOpcode opcode) => opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Xor;
    public static bool IsSupportedBinOp(LlvmOpcode opcode) => IsDesirableBinOp(opcode);
    public static bool IsDesirableCastOp(LlvmOpcode opcode) => opcode is LlvmOpcode.Trunc or LlvmOpcode.PtrToAddr
        or LlvmOpcode.PtrToInt or LlvmOpcode.IntToPtr or LlvmOpcode.BitCast or LlvmOpcode.AddrSpaceCast;
    public static bool IsSupportedCastOp(LlvmOpcode opcode) => IsDesirableCastOp(opcode);
    public static bool IsSupportedGetElementPtr(LlvmType source) => !source.IsScalableTy();

    private static LlvmConstantExpr Intern(LlvmType type, LlvmOpcode opcode, IReadOnlyList<LlvmConstant> operands,
        uint flags = 0, LlvmType? sourceElementType = null, LlvmConstantExprRange? inRange = null, IReadOnlyList<int>? shuffleMask = null)
    {
        LlvmConstantExprKey key = new(type, opcode, operands, flags, sourceElementType, inRange, shuffleMask);
        Count(type.Context, "constant_expr.unique.queries");
        if (!type.Context.ExpressionConstants.TryGetValue(key, out var result))
        { type.Context.ExpressionConstants.Add(key, result = new(key)); Count(type.Context, "constant_expr.unique.created"); }
        else Count(type.Context, "constant_expr.unique.hit");
        return result;
    }

    public static LlvmConstant Get(LlvmOpcode opcode, LlvmConstant left, LlvmConstant right, uint flags = 0) =>
        Get(opcode, left, right, flags, null)!;

    public static LlvmConstant? Get(LlvmOpcode opcode, LlvmConstant left, LlvmConstant right, uint flags,
        LlvmType? onlyIfReducedType)
    {
        Debug.Assert(IsSupportedBinOp(opcode) && left.Type == right.Type && left.Type.IsIntOrIntVectorTy());
        if (FoldBinary(opcode, left, right) is { } folded)
        { Count(left.Context, "constant_expr.binary.folded"); return folded; }
        if (onlyIfReducedType == left.Type) return null;
        return Intern(left.Type, opcode, [left, right], flags);
    }
    public static LlvmConstant GetAdd(LlvmConstant left, LlvmConstant right, bool noUnsignedWrap = false, bool noSignedWrap = false) =>
        Get(LlvmOpcode.Add, left, right, (noUnsignedWrap ? 1U : 0) | (noSignedWrap ? 2U : 0))!;
    public static LlvmConstant GetSub(LlvmConstant left, LlvmConstant right, bool noUnsignedWrap = false, bool noSignedWrap = false) =>
        Get(LlvmOpcode.Sub, left, right, (noUnsignedWrap ? 1U : 0) | (noSignedWrap ? 2U : 0))!;
    public static LlvmConstant GetXor(LlvmConstant left, LlvmConstant right) => Get(LlvmOpcode.Xor, left, right)!;
    public static LlvmConstant GetNeg(LlvmConstant value, bool noSignedWrap = false) =>
        GetSub(GetNullValue(value.Type), value, noSignedWrap: noSignedWrap);
    public static LlvmConstant GetNot(LlvmConstant value) => GetXor(value, GetAllOnes(value.Type));

    public LlvmConstant? GetWithOperands(IReadOnlyList<LlvmConstant> operands, LlvmType? type = null,
        bool onlyIfReduced = false, LlvmType? sourceType = null)
    {
        type ??= Type;
        Debug.Assert(operands.Count == OperandCount);
        if (type == Type && operands.Select((operand, index) => ReferenceEquals(operand, GetOperand(index))).All(value => value)) return this;
        if (IsCast) return GetCast(Opcode, operands[0], type, onlyIfReduced);
        if (Opcode == LlvmOpcode.ExtractElement) return GetExtractElement(operands[0], operands[1], onlyIfReduced ? type : null);
        if (Opcode == LlvmOpcode.InsertElement) return GetInsertElement(operands[0], operands[1], operands[2], onlyIfReduced ? type : null);
        if (Opcode == LlvmOpcode.ShuffleVector) return GetShuffleVector(operands[0], operands[1], ShuffleMask, onlyIfReduced ? type : null);
        if (Opcode == LlvmOpcode.GetElementPtr)
            return GetGetElementPtr(sourceType ?? SourceElementType!, operands[0], operands.Skip(1).ToArray(),
                NoWrapFlags, InRange, onlyIfReduced ? type : null);
        return Get(Opcode, operands[0], operands[1], Flags, onlyIfReduced ? type : null);
    }

    public LlvmInstruction GetAsInstruction()
    {
        if (IsCast) return new LlvmCastInstruction(Opcode, GetConstantOperand(0), Type);
        if (Opcode == LlvmOpcode.ExtractElement) return new LlvmExtractElementInstruction(GetConstantOperand(0), GetConstantOperand(1));
        if (Opcode == LlvmOpcode.InsertElement) return new LlvmInsertElementInstruction(GetConstantOperand(0), GetConstantOperand(1), GetConstantOperand(2));
        if (Opcode == LlvmOpcode.ShuffleVector) return new LlvmShuffleVectorInstruction(GetConstantOperand(0), GetConstantOperand(1), ShuffleMask);
        if (Opcode == LlvmOpcode.GetElementPtr)
            return new LlvmGetElementPtrInstruction(SourceElementType!, PointerOperand,
                Indices.Cast<LlvmValue>().ToArray(), NoWrapFlags);
        return new LlvmBinaryOperator(Opcode, GetConstantOperand(0), GetConstantOperand(1))
        { HasNoUnsignedWrap = HasNoUnsignedWrap, HasNoSignedWrap = HasNoSignedWrap };
    }

    internal override void HandleOperandChange(LlvmValue from, LlvmValue to)
    {
        Debug.Assert(to is LlvmConstant);
        Count(Context, "constant_expr.rauw.operand_changes");
        var operands = Enumerable.Range(0, OperandCount).Select(index =>
            ReferenceEquals(GetOperand(index), from) ? (LlvmConstant)to : GetConstantOperand(index)).ToArray();
        LlvmConstant? replacement = GetWithOperands(operands, Type, true);
        LlvmConstantExprKey next = new(Type, Opcode, operands, Flags, SourceElementType, InRange, ShuffleMask);
        replacement ??= Context.ExpressionConstants.GetValueOrDefault(next);
        if (replacement is not null)
        {
            Debug.Assert(!ReferenceEquals(replacement, this));
            Count(Context, "constant_expr.rauw.canonical_replaced");
            ReplaceAllUsesWith(replacement);
            Context.ExpressionConstants.Remove(key); DropAllReferences(); NotifyValueDeletion();
            return;
        }
        Context.ExpressionConstants.Remove(key);
        for (int index = 0; index < OperandCount; ++index)
            if (ReferenceEquals(GetOperand(index), from)) GetOperandUse(index).Set(to);
        key = next; Context.ExpressionConstants.Add(key, this);
        Count(Context, "constant_expr.rauw.rekeyed");
    }
    private static void Count(LlvmContext context, string name)
    {
        if (context.ConstantExpressionMetrics is { } metrics) metrics[name] = metrics.GetValueOrDefault(name) + 1;
    }
}
