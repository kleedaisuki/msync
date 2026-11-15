using Msync.Core.Llvm;

internal static class ConstantElementQueryProperties
{
    internal static int Run()
    {
        int checks = 0;
        LlvmContext context = new();
        var integer = LlvmType.GetInt32Ty(context);
        var one = LlvmConstantInt.Get(integer, 1);
        var undef = LlvmUndefValue.Get(integer);
        var poison = LlvmPoisonValue.Get(integer);
        Require(!undef.ContainsUndefOrPoisonElement() && !poison.ContainsPoisonElement(),
            "Scalar undef and poison are not aggregate element queries.");
        var vector = LlvmConstantVector.Get([one, undef, poison]);
        Require(vector.ContainsUndefOrPoisonElement() && vector.ContainsUndefElement() && vector.ContainsPoisonElement(),
            "Fixed vector queries must distinguish undef and poison elements.");
        var undefVector = LlvmUndefValue.Get(vector.Type);
        var poisonVector = LlvmPoisonValue.Get(vector.Type);
        Require(undefVector.ContainsUndefElement() && !undefVector.ContainsPoisonElement() &&
            poisonVector.ContainsPoisonElement() && !poisonVector.ContainsUndefElement(),
            "Whole-vector undef and poison are distinct source classes.");
        var scalable = LlvmScalableVectorType.Get(integer, 4);
        Require(LlvmUndefValue.Get(scalable).ContainsUndefOrPoisonElement() &&
            !LlvmConstant.GetNullValue(scalable).ContainsUndefOrPoisonElement(),
            "Scalable undef is recognized without inventing fixed lanes.");
        var arrayType = LlvmArrayType.Get(integer, 2);
        var nested = LlvmConstantArray.Get(arrayType, [one, poison]);
        var structure = LlvmConstantStruct.Get(LlvmStructType.Get(context, [integer, arrayType]), [one, nested]);
        Require(structure.ContainsPoisonElement() && !structure.ContainsUndefElement(),
            "Element queries recurse through actual aggregate fields.");
        var replaced = LlvmConstant.ReplaceUndefsWith(vector, one);
        for (uint index = 0; index < 3; ++index)
            Require(ReferenceEquals(replaced.GetAggregateElement(index), one),
                "ReplaceUndefsWith replaces both undef and poison lanes.");
        Require(ReferenceEquals(LlvmConstant.ReplaceUndefsWith(poison, one), one),
            "Scalar poison matches the source undef matcher.");
        Require(ReferenceEquals(LlvmConstant.ReplaceUndefsWith(structure, one), structure),
            "A partially defined non-vector aggregate remains unchanged.");
        var undefinedStructType = LlvmStructType.Get(context, [integer, integer]);
        var undefinedStruct = LlvmConstantStruct.Get(undefinedStructType, [undef, poison]);
        var structReplacement = LlvmConstant.GetNullValue(undefinedStructType);
        Require(ReferenceEquals(LlvmConstant.ReplaceUndefsWith(undefinedStruct, structReplacement), structReplacement),
            "The source undef matcher accepts aggregates containing only undef and poison.");
        Require(((LlvmConstantInt)LlvmConstant.GetAllOnesValue(integer)).Value.IsAllOnes,
            "Integer all-ones constant retains its declared width.");
        var bytes = LlvmByteType.Get(context, 16);
        Require(((LlvmConstantByte)LlvmConstant.GetAllOnesValue(bytes)).Value.IsAllOnes,
            "Byte all-ones constant retains its declared width.");
        LlvmType[] floatingTypes = [LlvmType.GetHalfTy(context), LlvmType.GetBFloatTy(context),
            LlvmType.GetFloatTy(context), LlvmType.GetDoubleTy(context)];
        foreach (var type in floatingTypes)
            Require(((LlvmConstantFP)LlvmConstant.GetAllOnesValue(type)).Value.BitcastToAPInt().IsAllOnes,
                "Floating all-ones is a raw format-sized NaN payload, not numeric minus one.");
        var allOnesVector = LlvmConstant.GetAllOnesValue((LlvmVectorType)vector.Type);
        Require(allOnesVector.GetSplatValue() is LlvmConstantInt { IsMinusOne: true },
            "Vector all-ones broadcasts the scalar lane value.");
        var boolType = LlvmFixedVectorType.Get(LlvmType.GetInt1Ty(context), 3);
        Require(LlvmConstantInt.GetTrue(boolType).GetSplatValue() is LlvmConstantInt { IsOne: true } &&
            LlvmConstantInt.GetFalse(boolType).IsNullValue &&
            ReferenceEquals(LlvmConstantInt.GetBool(boolType, true), LlvmConstantInt.GetTrue(boolType)),
            "Typed Boolean constants preserve source scalar and vector factories.");
        var pointer = LlvmPointerType.Get(context);
        var expression = LlvmConstantExpr.GetPtrAdd(LlvmConstantPointerNull.Get(pointer),
            LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 3));
        var expressionStruct = LlvmConstantStruct.Get(LlvmStructType.Get(context, [pointer]), [expression]);
        Require(expressionStruct.ContainsConstantExpression() && !structure.ContainsConstantExpression(),
            "Constant-expression discovery visits aggregate elements, not arbitrary SSA uses.");
        Require(!LlvmConstantVector.Get([one, LlvmConstantInt.Get(integer, 2)]).ContainsUndefOrPoisonElement(),
            "Raw numeric data vectors use the source ConstantData pruning path.");
        return checks;

        void Require(bool condition, string message)
        {
            Program.Require(condition, message);
            ++checks;
        }
    }
}
