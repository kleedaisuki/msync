// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// FAddendCoef / FAddend. Managed APFloat value storage replaces the C++ buffer.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private struct FAddendCoef
        {
            private bool isFloat;
            private short integer;
            private LlvmAPFloat floating;
            internal readonly bool IsZero => isFloat ? floating.IsZero : integer == 0;
            internal readonly bool IsOne => !isFloat && integer == 1;
            internal readonly bool IsTwo => !isFloat && integer == 2;
            internal readonly bool IsMinusOne => !isFloat && integer == -1;
            internal readonly bool IsMinusTwo => !isFloat && integer == -2;
            internal void Set(short value) { Debug.Assert(value is >= -4 and <= 4); isFloat = false; integer = value; }
            internal void Set(LlvmAPFloat value) { floating = value; isFloat = true; }
            private static LlvmAPFloat FromInteger(LlvmAPFloatSemantics semantics, int value)
            {
                var result = LlvmAPFloat.FromAPInt(semantics, new LlvmAPInt(32, (ulong)Math.Abs(value)), false).Value;
                return value < 0 ? result.ChangeSign() : result;
            }
            private void ConvertToFloat(LlvmAPFloatSemantics semantics)
            {
                if (isFloat) return;
                floating = FromInteger(semantics, Math.Abs(integer));
                if (integer <= 0) floating = floating.ChangeSign();
                isFloat = true;
            }
            internal void Add(FAddendCoef other)
            {
                if (isFloat == other.isFloat)
                {
                    if (!isFloat) integer += other.integer;
                    else floating = floating.Add(other.floating).Value;
                    return;
                }
                if (!isFloat)
                { ConvertToFloat(other.floating.Semantics); floating = floating.Add(other.floating).Value; return; }
                floating = floating.Add(FromInteger(floating.Semantics, other.integer)).Value;
            }
            internal void Multiply(FAddendCoef other)
            {
                if (other.IsOne) return;
                if (other.IsMinusOne) { Negate(); return; }
                if (!isFloat && !other.isFloat)
                {
                    int product = integer * other.integer;
                    Debug.Assert(product is >= -4 and <= 4);
                    integer = (short)product;
                    return;
                }
                var semantics = isFloat ? floating.Semantics : other.floating.Semantics;
                ConvertToFloat(semantics);
                floating = floating.Multiply(other.isFloat ? other.floating : FromInteger(semantics, other.integer)).Value;
            }
            internal void Negate()
            { if (isFloat) floating = floating.ChangeSign(); else integer = (short)-integer; }
            internal readonly LlvmValue GetValue(LlvmType type) => LlvmConstantFP.Get(type,
                isFloat ? floating : FromInteger(type.GetFltSemantics(), integer));
        }

        private struct FAddend
        {
            internal LlvmValue? Symbol;
            internal FAddendCoef Coefficient;
            internal readonly bool IsConstant => Symbol is null;
            internal readonly bool IsZero => Coefficient.IsZero;
            internal void Set(short coefficient, LlvmValue? symbol) { Coefficient.Set(coefficient); Symbol = symbol; }
            internal void Set(LlvmAPFloat coefficient, LlvmValue? symbol) { Coefficient.Set(coefficient); Symbol = symbol; }
            internal void Add(FAddend other) { Debug.Assert(ReferenceEquals(Symbol, other.Symbol)); Coefficient.Add(other.Coefficient); }
            internal void Negate() => Coefficient.Negate();
            internal static uint DrillValue(LlvmValue? value, out FAddend first, out FAddend second)
            {
                first = second = default;
                if (value is not LlvmBinaryOperator operation) return 0;
                if (operation.Opcode is LlvmOpcode.FAdd or LlvmOpcode.FSub)
                {
                    var leftConstant = operation.Left as LlvmConstantFP;
                    var rightConstant = operation.Right as LlvmConstantFP;
                    LlvmValue? left = leftConstant is { IsZero: true } ? null : operation.Left;
                    LlvmValue? right = rightConstant is { IsZero: true } ? null : operation.Right;
                    if (left is not null)
                    {
                        if (leftConstant is null) first.Set(1, left);
                        else first.Set(leftConstant.Value, null);
                    }
                    if (right is not null)
                    {
                        FAddend addend = default;
                        if (rightConstant is null) addend.Set(1, right);
                        else addend.Set(rightConstant.Value, null);
                        if (operation.Opcode == LlvmOpcode.FSub) addend.Negate();
                        if (left is not null) second = addend; else first = addend;
                    }
                    if (left is not null || right is not null) return left is not null && right is not null ? 2U : 1U;
                    first.Set(LlvmAPFloat.GetZero(leftConstant!.Value.Semantics), null);
                    return 1;
                }
                if (operation.Opcode != LlvmOpcode.FMul) return 0;
                if (operation.Left is LlvmConstantFP leftCoefficient) { first.Set(leftCoefficient.Value, operation.Right); return 1; }
                if (operation.Right is LlvmConstantFP rightCoefficient) { first.Set(rightCoefficient.Value, operation.Left); return 1; }
                return 0;
            }
            internal readonly uint Drill(out FAddend first, out FAddend second)
            {
                first = second = default;
                if (IsConstant) return 0;
                uint count = DrillValue(Symbol, out first, out second);
                if (count == 0 || Coefficient.IsOne) return count;
                first.Coefficient.Multiply(Coefficient);
                if (count == 2) second.Coefficient.Multiply(Coefficient);
                return count;
            }
        }
    }
}
