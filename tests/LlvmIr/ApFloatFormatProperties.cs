using Msync.Core.Llvm;

internal static class ApFloatFormatProperties
{
    internal static int Run()
    {
        int checks = 0;
        LlvmAPFloatSemantics[] formats = [LlvmAPFloatSemantics.IEEEhalf, LlvmAPFloatSemantics.BFloat,
            LlvmAPFloatSemantics.IEEEsingle, LlvmAPFloatSemantics.IEEEdouble];
        Require(LlvmAPFloat.IsLosslesslyConvertibleTo(LlvmAPFloatSemantics.IEEEhalf,
            LlvmAPFloatSemantics.IEEEsingle, ignoreNaNs: true) &&
            !LlvmAPFloat.IsLosslesslyConvertibleTo(LlvmAPFloatSemantics.BFloat,
                LlvmAPFloatSemantics.IEEEhalf, ignoreNaNs: true) &&
            !LlvmAPFloat.IsLosslesslyConvertibleTo(LlvmAPFloatSemantics.IEEEhalf,
                LlvmAPFloatSemantics.IEEEsingle),
            "Format containment accounts for exponent range and source signaling-NaN conversion.");
        foreach (var semantics in formats)
        {
            var one = LlvmAPFloat.GetOne(semantics);
            var zero = LlvmAPFloat.GetZero(semantics);
            var negativeZero = LlvmAPFloat.GetZero(semantics, true);
            var smallest = LlvmAPFloat.GetSmallest(semantics);
            var normalized = LlvmAPFloat.GetSmallestNormalized(semantics);
            var largest = LlvmAPFloat.GetLargest(semantics);
            Require(one.Semantics == semantics && one.IsNormal && one.ILogB() == 0, "Each format has its own exact one.");
            Require(one.IsInteger() && !one.Scalbn(-1).Value.IsInteger() &&
                !LlvmAPFloat.GetInf(semantics).IsInteger(), "Integer predicate follows finite source rounding semantics.");
            Require(smallest.IsDenormal && !smallest.IsNormal && normalized.IsNormal && normalized.IsSmallestNormalized,
                "Public IEEE normality distinguishes subnormal storage category.");
            Require(zero.Next(false).Value.Equals(smallest) && negativeZero.Next(false).Value.Equals(smallest),
                "nextUp both zero signs is the format's smallest positive subnormal.");
            Require(smallest.ChangeSign().Next(false).Value.Equals(negativeZero), "nextUp negative smallest preserves negative zero.");
            Require(largest.Next(false).Value.IsInfinity && LlvmAPFloat.GetInf(semantics, true).Next(false).Value.Equals(largest.ChangeSign()),
                "Next handles both finite/infinity boundaries.");
            Require(one.GetExactInverse(out var reciprocal) && reciprocal.Equals(one) && !smallest.GetExactInverse(out _),
                "Exact inverse keeps source power-of-two and normal-result restrictions.");
            Require(one.Scalbn(2).Value.GetExactInverse(out reciprocal) && reciprocal.Equals(one.Scalbn(-2).Value),
                "Exact inverse uses source scalbn rather than host division.");
            Require(one.Add(one).Value.Equals(one.Scalbn(1).Value) && one.Subtract(one).Value.Equals(zero),
                "Arithmetic packs results using the operand semantics.");
            Require(one.FusedMultiplyAdd(one, one).Value.Equals(one.Scalbn(1).Value), "FMA is format preserving.");
            Require(LlvmAPFloat.Minimum(zero, negativeZero).Equals(negativeZero) && LlvmAPFloat.Maximum(negativeZero, zero).Equals(zero),
                "Minimum/maximum use the source signed-zero ordering.");
            ulong quietBit = 1UL << ((int)LlvmAPFloat.SemanticsPrecision(semantics) - 2);
            var quietNaN = LlvmAPFloat.FromBits(semantics, LlvmAPFloat.GetInf(semantics).BitcastToAPInt().GetZExtValue() | quietBit | 1);
            var signalingNaN = LlvmAPFloat.FromBits(semantics, LlvmAPFloat.GetInf(semantics).BitcastToAPInt().GetZExtValue() | 1);
            Require(LlvmAPFloat.MinNum(quietNaN, one).Equals(one) && LlvmAPFloat.Minimum(quietNaN, one).Equals(quietNaN),
                "minNum and minimum have distinct NaN propagation contracts.");
            Require(LlvmAPFloat.MinNum(signalingNaN, one).Equals(signalingNaN.MakeQuiet()) &&
                LlvmAPFloat.MinimumNum(signalingNaN, one).Equals(one), "Signaling NaNs distinguish IEEE 2008 and 2019 number minimum.");
            Require(signalingNaN.Next(false).Status == LlvmFloatStatus.InvalidOp && quietNaN.Next(false).Value.Equals(quietNaN),
                "Next quiets signaling NaN and preserves quiet NaN payloads.");
        }
        foreach (var semantics in new[] { LlvmAPFloatSemantics.IEEEhalf, LlvmAPFloatSemantics.BFloat })
            for (uint raw = 0; raw <= ushort.MaxValue; ++raw)
            {
                var source = LlvmAPFloat.FromBits(semantics, raw);
                var extended = source.Convert(LlvmAPFloatSemantics.IEEEsingle);
                var narrowed = extended.Value.Convert(semantics);
                Require(narrowed.Value.Equals(source.IsNaN ? source.MakeQuiet() : source), "Every 16-bit value has an exact finite/payload roundtrip through single.");
                Require(extended.Status == (source.IsSignaling ? LlvmFloatStatus.InvalidOp : LlvmFloatStatus.OK),
                    "Extension status only reports signaling NaNs.");
            }
        ulong[] doubles = [0, 0x8000000000000000, 1, 0x0010000000000000, 0x3ff0000010000000,
            0x3ff0000000000001, 0xbff0000010000000, 0x47efffffe0000000, 0x7fefffffffffffff];
        foreach (ulong raw in doubles)
        {
            double number = BitConverter.UInt64BitsToDouble(raw);
            var source = LlvmAPFloat.FromBits(raw);
            Require(source.Convert(LlvmAPFloatSemantics.IEEEsingle).Value.BitcastToAPInt().GetZExtValue() == BitConverter.SingleToUInt32Bits((float)number),
                "Single conversion matches independent host IEEE nearest-even conversion.");
            Require(source.Convert(LlvmAPFloatSemantics.IEEEhalf).Value.BitcastToAPInt().GetZExtValue() == BitConverter.HalfToUInt16Bits((Half)number),
                "Half conversion matches independent host IEEE nearest-even conversion.");
        }
        var tie = LlvmAPFloat.FromBits(0x3ff0000010000000);
        Require(tie.Convert(LlvmAPFloatSemantics.IEEEsingle).Value.BitcastToAPInt().GetZExtValue() == 0x3f800000 &&
            tie.Convert(LlvmAPFloatSemantics.IEEEsingle, LlvmRoundingMode.NearestTiesToAway).Value.BitcastToAPInt().GetZExtValue() == 0x3f800001,
            "Format conversion preserves halfway even/away distinctions.");
        Require(tie.Convert(LlvmAPFloatSemantics.IEEEsingle).Status == LlvmFloatStatus.Inexact &&
            tie.Convert(LlvmAPFloatSemantics.IEEEsingle).LosesInfo, "Narrowing reports status and information loss separately.");
        Random random = new(0x4150464d);
        for (int index = 0; index < 300; ++index)
        {
            byte[] data = new byte[24]; random.NextBytes(data);
            double left = BitConverter.ToDouble(data, 0), right = BitConverter.ToDouble(data, 8), extra = BitConverter.ToDouble(data, 16);
            var result = new LlvmAPFloat(left).FusedMultiplyAdd(new(right), new(extra));
            double expected = Math.FusedMultiplyAdd(left, right, extra);
            Require(double.IsNaN(expected) ? result.Value.IsNaN : result.Value.BitcastToAPInt().GetZExtValue() == BitConverter.DoubleToUInt64Bits(expected),
                "Full-precision FMA agrees with independent hardware/library IEEE operation.");
        }
        var a = LlvmAPFloat.FromBits(0x3ff0000002000000);
        var b = LlvmAPFloat.FromBits(0x3feffffffc000000);
        Require(a.FusedMultiplyAdd(b, new(-1d)).Value.Equals(new LlvmAPFloat(-1d).Scalbn(-54).Value),
            "FMA retains product low bits lost by separate multiply and add.");
        return checks;

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }
}
