// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Constants.cpp element queries,
// getAllOnesValue/replaceUndefsWith and PatternMatch.cpp undef_match.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public abstract partial class LlvmConstant
{
    public bool ContainsUndefOrPoisonElement() => ContainsUndefinedElement(this, ElementMatch.Any);
    public bool ContainsPoisonElement() => ContainsUndefinedElement(this, ElementMatch.Poison);
    public bool ContainsUndefElement() => ContainsUndefinedElement(this, ElementMatch.Undef);
    public bool ContainsConstantExpression() => this is not (LlvmConstantInt or LlvmConstantFP) &&
        ContainsMatchingElement(this, ElementMatch.Expression);

    private enum ElementMatch : byte { Any, Poison, Undef, Expression }

    private static bool MatchesElement(LlvmConstant value, ElementMatch kind) => kind switch
    {
        ElementMatch.Any => value is LlvmUndefValue,
        ElementMatch.Poison => value is LlvmPoisonValue,
        ElementMatch.Undef => value is LlvmUndefValue and not LlvmPoisonValue,
        _ => value is LlvmConstantExpr
    };

    private static bool ContainsUndefinedElement(LlvmConstant value, ElementMatch kind)
    {
        if (!value.Type.IsVectorTy() && !value.Type.IsAggregateType()) return false;
        return MatchesElement(value, kind) || ContainsMatchingElement(value, kind);
    }

    private static bool ContainsMatchingElement(LlvmConstant value, ElementMatch kind)
    {
        if (value is LlvmConstantData) return false;
        uint? count = value.Type switch
        {
            LlvmFixedVectorType vector => vector.NumElements,
            LlvmStructType structure => (uint)structure.NumElements,
            LlvmArrayType array => unchecked((uint)array.NumElements),
            _ => null
        };
        if (count is null) return false;
        for (uint index = 0; index != count.Value; ++index)
        {
            var element = value.GetAggregateElement(index);
            if (element is not null && (MatchesElement(element, kind) || ContainsMatchingElement(element, kind)))
                return true;
        }
        return false;
    }

    public static LlvmConstant GetAllOnesValue(LlvmType type) => type switch
    {
        LlvmIntegerType integer => LlvmConstantInt.Get(type.Context, LlvmAPInt.GetAllOnes(integer.BitWidth)),
        LlvmByteType bytes => LlvmConstantByte.Get(type.Context, LlvmAPInt.GetAllOnes(bytes.BitWidth)),
        LlvmVectorType vector => LlvmConstantVector.GetSplat(vector.ElementCount, GetAllOnesValue(vector.ElementType)),
        _ when type.IsFloatingPointTy() => LlvmConstantFP.Get(type.Context, LlvmAPFloat.GetAllOnesValue(type.GetFltSemantics())),
        _ => throw new UnreachableException()
    };

    public static LlvmConstant ReplaceUndefsWith(LlvmConstant constant, LlvmConstant replacement)
    {
        if (MatchesUndef(constant))
        {
            Debug.Assert(ReferenceEquals(constant.Type, replacement.Type));
            return replacement;
        }
        if (constant.Type is not LlvmFixedVectorType vector) return constant;
        var elements = new LlvmConstant[vector.NumElements];
        for (uint index = 0; index < vector.NumElements; ++index)
        {
            var element = constant.GetAggregateElement(index);
            Debug.Assert(element is null || ReferenceEquals(element.Type, replacement.Type));
            elements[index] = element is not null && MatchesUndef(element) ? replacement : element!;
        }
        return LlvmConstantVector.Get(elements);
    }

    private static bool MatchesUndef(LlvmConstant value)
    {
        if (value is LlvmUndefValue) return true;
        if (value is not LlvmConstantAggregate aggregate) return false;
        HashSet<LlvmConstantAggregate> seen = new();
        List<LlvmConstantAggregate> worklist = new();
        if (!CheckAggregate(aggregate)) return false;
        while (worklist.Count != 0)
        {
            var next = worklist[^1];
            worklist.RemoveAt(worklist.Count - 1);
            if (!CheckAggregate(next)) return false;
        }
        return true;

        bool CheckAggregate(LlvmConstantAggregate current)
        {
            foreach (var operand in current.Operands)
            {
                if (operand is LlvmUndefValue) continue;
                if (operand is not LlvmConstantAggregate nested) return false;
                if (seen.Add(nested)) worklist.Add(nested);
            }
            return true;
        }
    }
}

public sealed partial class LlvmConstantInt
{
    public static LlvmConstant GetTrue(LlvmType type)
    {
        Debug.Assert(type.IsIntOrIntVectorTy(1));
        var scalar = GetTrue(type.Context);
        return type is LlvmVectorType vector ? LlvmConstantVector.GetSplat(vector.ElementCount, scalar) : scalar;
    }
    public static LlvmConstant GetFalse(LlvmType type)
    {
        Debug.Assert(type.IsIntOrIntVectorTy(1));
        var scalar = GetFalse(type.Context);
        return type is LlvmVectorType vector ? LlvmConstantVector.GetSplat(vector.ElementCount, scalar) : scalar;
    }
    public static LlvmConstant GetBool(LlvmType type, bool value) => value ? GetTrue(type) : GetFalse(type);
}

public readonly partial struct LlvmAPFloat
{
    public static LlvmAPFloat GetAllOnesValue(LlvmAPFloatSemantics semantics) =>
        new(semantics, LlvmAPInt.GetAllOnes(SemanticsSizeInBits(semantics)));
}
