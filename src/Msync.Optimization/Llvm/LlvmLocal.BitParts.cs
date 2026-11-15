// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/Local.cpp collectBitParts and recognizeBSwapOrBitReverseIdiom.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmLocal
{
    private sealed class BitPart(LlvmValue provider, int width)
    {
        internal readonly LlvmValue Provider = provider;
        internal readonly sbyte[] Provenance = new sbyte[width];
        internal BitPart Copy()
        {
            var copy = new BitPart(Provider, Provenance.Length);
            Provenance.CopyTo(copy.Provenance, 0);
            return copy;
        }
    }

    public static bool RecognizeBSwapOrBitReverseIdiom(LlvmInstruction instruction, bool matchBSwaps,
        bool matchBitReversals, List<LlvmInstruction> inserted, Dictionary<string, ulong>? metrics = null)
    {
        BitPartCount(metrics, "bit_parts.queries");
        var kind = (instruction as LlvmCallBase)?.CalledFunction?.IntrinsicDescriptor?.Kind;
        if (instruction.Opcode != LlvmOpcode.Or && kind != LlvmIntrinsicKind.FShl && kind != LlvmIntrinsicKind.FShr && kind != LlvmIntrinsicKind.BSwap) return false;
        if (!matchBSwaps && !matchBitReversals) return false;
        var type = instruction.Type;
        uint width = type.GetScalarSizeInBits();
        if (!type.IsIntOrIntVectorTy() || width == 1 || width > 128) return false;
        bool foundRoot = false;
        Dictionary<LlvmValue, BitPart?> cache = [];
        var parts = CollectBitParts(instruction, matchBSwaps, matchBitReversals, cache, 0, ref foundRoot, metrics);
        if (parts is null) return false;
        int demandedWidth = parts.Provenance.Length;
        while (demandedWidth != 0 && parts.Provenance[demandedWidth - 1] == -1) --demandedWidth;
        if (demandedWidth == 0) return false;
        LlvmType demandedType = type;
        if ((uint)demandedWidth != width)
        {
            demandedType = LlvmIntegerType.Get(instruction.Context, (uint)demandedWidth);
            if (type is LlvmVectorType vector) demandedType = LlvmVectorType.Get(demandedType, vector.ElementCount);
        }
        if (demandedWidth > width) return false;
        var demanded = LlvmAPInt.GetAllOnes((uint)demandedWidth);
        bool bswap = matchBSwaps && demandedWidth % 16 == 0, reverse = matchBitReversals;
        for (int index = 0; index < demandedWidth && (bswap || reverse); ++index)
        {
            int source = parts.Provenance[index];
            if (source == -1) { demanded = demanded.ClearBits((uint)index, (uint)index + 1); continue; }
            bswap &= source % 8 == index % 8 && (source >> 3) == (demandedWidth >> 3) - (index >> 3) - 1;
            reverse &= source == demandedWidth - index - 1;
        }
        if (!bswap && !reverse) return false;
        var intrinsic = bswap ? LlvmIntrinsicKind.BSwap : LlvmIntrinsicKind.BitReverse;
        LlvmValue provider = parts.Provider;
        if (demandedType != provider.Type)
        {
            var cast = new LlvmCastInstruction(provider.Type.GetScalarSizeInBits() > demandedType.GetScalarSizeInBits() ? LlvmOpcode.Trunc : LlvmOpcode.ZExt, provider, demandedType);
            cast.SetName("trunc"); instruction.Parent!.InsertBefore(cast, instruction); inserted.Add(cast); provider = cast;
        }
        LlvmInstruction result = LlvmIntrinsic.CreateIntrinsic(intrinsic, [provider], demandedType, instruction.Parent!.Module!, metrics);
        result.SetName("rev"); instruction.Parent.InsertBefore(result, instruction); inserted.Add(result);
        if (!demanded.IsAllOnes)
        {
            result = new LlvmBinaryOperator(LlvmOpcode.And, result, LlvmConstantInt.Get(demandedType, demanded));
            result.SetName("mask"); instruction.Parent.InsertBefore(result, instruction); inserted.Add(result);
        }
        if (type != result.Type)
        {
            var extend = new LlvmCastInstruction(result.Type.GetScalarSizeInBits() < type.GetScalarSizeInBits() ? LlvmOpcode.ZExt : LlvmOpcode.Trunc, result, type);
            extend.SetName("zext"); instruction.Parent.InsertBefore(extend, instruction); inserted.Add(extend);
        }
        BitPartCount(metrics, bswap ? "bit_parts.bswap" : "bit_parts.bitreverse");
        BitPartCount(metrics, "bit_parts.instructions_inserted", (ulong)inserted.Count);
        return true;
    }

    private static BitPart? CollectBitParts(LlvmValue value, bool swaps, bool reversals,
        Dictionary<LlvmValue, BitPart?> cache, int depth, ref bool foundRoot, Dictionary<string, ulong>? metrics)
    {
        if (cache.TryGetValue(value, out var cached)) { BitPartCount(metrics, "bit_parts.cache_hits"); return cached; }
        cache.Add(value, null);
        BitPartCount(metrics, "bit_parts.visits");
        int width = (int)value.Type.GetScalarSizeInBits();
        if (width > 128) { BitPartCount(metrics, "bit_parts.width_limit"); return null; }
        if (depth == 48) { BitPartCount(metrics, "bit_parts.depth_limit"); return null; }
        BitPart? result;
        if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } orExpression)
        {
            var left = CollectBitParts(orExpression.Left, swaps, reversals, cache, depth + 1, ref foundRoot, metrics);
            if (left is null) return null;
            var right = CollectBitParts(orExpression.Right, swaps, reversals, cache, depth + 1, ref foundRoot, metrics);
            if (right is null || left.Provider != right.Provider) return null;
            result = new(left.Provider, width);
            for (int index = 0; index < width; ++index)
            {
                int a = left.Provenance[index], b = right.Provenance[index];
                if (a != -1 && b != -1 && a != b) { BitPartCount(metrics, "bit_parts.provenance_conflicts"); return null; }
                result.Provenance[index] = a == -1 ? (sbyte)b : (sbyte)a;
            }
            return cache[value] = result;
        }
        if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl or LlvmOpcode.LShr } shift && BitPartInteger(shift.Right) is { } amount)
        {
            if (amount.Value.GetLimitedValue() >= (uint)width || !reversals && amount.Value.GetZExtValue() % 8 != 0) return null;
            var input = CollectBitParts(shift.Left, swaps, reversals, cache, depth + 1, ref foundRoot, metrics);
            if (input is null) return null;
            int count = (int)amount.Value.GetZExtValue();
            result = new(input.Provider, width);
            Array.Fill(result.Provenance, (sbyte)-1);
            if (shift.Opcode == LlvmOpcode.Shl) Array.Copy(input.Provenance, 0, result.Provenance, count, width - count);
            else Array.Copy(input.Provenance, count, result.Provenance, 0, width - count);
            return cache[value] = result;
        }
        if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.And } andExpression && BitPartInteger(andExpression.Right) is { } mask)
        {
            if (!reversals && mask.Value.PopCount() % 8 != 0) return null;
            var input = CollectBitParts(andExpression.Left, swaps, reversals, cache, depth + 1, ref foundRoot, metrics);
            if (input is null) return null;
            result = input.Copy();
            for (int index = 0; index < width; ++index)
                if ((mask.Value & LlvmAPInt.GetOneBitSet(mask.BitWidth, (uint)index)).IsZero) result.Provenance[index] = -1;
            return cache[value] = result;
        }
        if (value is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.Trunc } cast)
        {
            var input = CollectBitParts(cast.Operand, swaps, reversals, cache, depth + 1, ref foundRoot, metrics);
            if (input is null) return null;
            result = new(input.Provider, width);
            if (cast.Opcode == LlvmOpcode.ZExt) Array.Fill(result.Provenance, (sbyte)-1);
            Array.Copy(input.Provenance, result.Provenance, Math.Min(width, input.Provenance.Length));
            return cache[value] = result;
        }
        if (value is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } call)
        {
            var intrinsic = descriptor.Kind;
            if (intrinsic == LlvmIntrinsicKind.BitReverse || intrinsic == LlvmIntrinsicKind.BSwap)
            {
                var input = CollectBitParts(call.GetArgument(0), swaps, reversals, cache, depth + 1, ref foundRoot, metrics);
                if (input is null) return null;
                result = new(input.Provider, width);
                for (int index = 0; index < width; ++index)
                {
                    int destination = intrinsic == LlvmIntrinsicKind.BitReverse ? width - 1 - index : width - 8 - index / 8 * 8 + index % 8;
                    result.Provenance[destination] = input.Provenance[index];
                }
                return cache[value] = result;
            }
            if ((intrinsic == LlvmIntrinsicKind.FShl || intrinsic == LlvmIntrinsicKind.FShr) && BitPartInteger(call.GetArgument(2)) is { } shiftAmount)
            {
                int moduloAmount = (int)shiftAmount.Value.URem(new LlvmAPInt(shiftAmount.BitWidth, (uint)width)).GetZExtValue();
                if (intrinsic == LlvmIntrinsicKind.FShr) moduloAmount = width - moduloAmount;
                if (!reversals && moduloAmount % 8 != 0) return null;
                var left = CollectBitParts(call.GetArgument(0), swaps, reversals, cache, depth + 1, ref foundRoot, metrics);
                if (left is null) return null;
                var right = CollectBitParts(call.GetArgument(1), swaps, reversals, cache, depth + 1, ref foundRoot, metrics);
                if (right is null || left.Provider != right.Provider) return null;
                result = new(left.Provider, width);
                Array.Copy(left.Provenance, 0, result.Provenance, moduloAmount, width - moduloAmount);
                Array.Copy(right.Provenance, width - moduloAmount, result.Provenance, 0, moduloAmount);
                return cache[value] = result;
            }
        }
        if (foundRoot) { BitPartCount(metrics, "bit_parts.additional_root"); return null; }
        foundRoot = true;
        result = new(value, width);
        for (int index = 0; index < width; ++index) result.Provenance[index] = (sbyte)index;
        return cache[value] = result;
    }

    private static LlvmConstantInt? BitPartInteger(LlvmValue value) => value as LlvmConstantInt ??
        (value.Type.IsVectorTy() && value is LlvmConstant constant ? constant.GetSplatValue() as LlvmConstantInt : null);
    private static void BitPartCount(Dictionary<string, ulong>? metrics, string key, ulong amount = 1)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + amount; }
}
