using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class FloatingVectorIntrinsicProperties
{
    internal static int Run()
    {
        var context = new LlvmContext();
        var module = new LlvmModule("floating-vector-intrinsics", context);
        Dictionary<string, ulong> metrics = [];
        int checks = 0;
        foreach ((LlvmType scalar, string suffix) in new (LlvmType, string)[]
        {
            (LlvmType.GetHalfTy(context), "f16"), (LlvmType.GetBFloatTy(context), "bf16"),
            (LlvmType.GetFloatTy(context), "f32"), (LlvmType.GetDoubleTy(context), "f64"),
            (LlvmType.GetX86_FP80Ty(context), "f80"), (LlvmType.GetFP128Ty(context), "f128"),
            (LlvmType.GetPPC_FP128Ty(context), "ppcf128")
        })
        {
            Shape(scalar, suffix);
            Shape(LlvmFixedVectorType.Get(scalar, 4), "v4" + suffix);
            Shape(LlvmScalableVectorType.Get(scalar, 2), "nxv2" + suffix);
        }
        BuilderAndFolder();
        Require(metrics.GetValueOrDefault("intrinsic.declaration_reused") > 0,
            "Canonical scalar/vector overloads reuse the existing declaration through the source interning path.");
        return checks;

        void Require(bool condition, string reason)
        { Program.Require(condition, reason); ++checks; }

        void Shape(LlvmType type, string suffix)
        {
            var absolute = LlvmIntrinsic.GetDeclaration(module, LlvmIntrinsicKind.FAbs, type, metrics);
            Require(absolute.Name == "llvm.fabs." + suffix && ReferenceEquals(absolute.ReturnType, type) &&
                absolute.ArgumentCount == 1 && ReferenceEquals(absolute.FunctionType.GetParamType(0), type) &&
                absolute.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.FAbs,
                "FP unary declarations use the exact fixed/scalable vector mangling and matching return/operand type.");
            var classify = LlvmIntrinsic.GetDeclaration(module, LlvmIntrinsicKind.IsFPClass, type, metrics);
            Require(classify.Name == "llvm.is.fpclass." + suffix &&
                ReferenceEquals(classify.ReturnType, type.GetWithNewType(LlvmType.GetInt1Ty(context))) &&
                classify.FunctionType.GetParamType(1).IsIntegerTy(32) && classify.ParamHasAttr(1, LlvmAttributeKind.ImmArg),
                "is.fpclass returns scalar-or-same-vector-width i1 and retains its scalar immediate i32 class mask.");
            var power = LlvmIntrinsic.GetDeclaration(module, LlvmIntrinsicKind.PowI, type, metrics, LlvmType.GetInt16Ty(context));
            Require(power.Name == "llvm.powi." + suffix + ".i16" &&
                ReferenceEquals(power.ReturnType, type) && power.FunctionType.GetParamType(1).IsIntegerTy(16) &&
                power.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.PowI,
                "powi's independently overloaded exponent remains scalar even for vector FP inputs.");
            var exponent = type.GetWithNewType(LlvmType.GetInt32Ty(context));
            var scale = LlvmIntrinsic.GetDeclaration(module, LlvmIntrinsicKind.Ldexp, type, metrics, exponent);
            string exponentSuffix = type is LlvmVectorType vector
                ? (vector.ElementCount.IsScalable ? "nxv" : "v") + vector.ElementCount.KnownMinValue + "i32" : "i32";
            Require(scale.Name == "llvm.ldexp." + suffix + "." + exponentSuffix &&
                ReferenceEquals(scale.FunctionType.GetParamType(1), exponent) && scale.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Ldexp,
                "ldexp overload names preserve the actual scalar/fixed/scalable integer exponent type.");
            Require(ReferenceEquals(absolute, LlvmIntrinsic.GetDeclaration(module, LlvmIntrinsicKind.FAbs, type, metrics)),
                "Repeated vector declaration construction does not duplicate source intrinsic identities.");
        }

        void BuilderAndFolder()
        {
            var floating = LlvmType.GetFloatTy(context);
            var vector = LlvmFixedVectorType.Get(floating, 2);
            var exponentVector = LlvmFixedVectorType.Get(LlvmType.GetInt32Ty(context), 2);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(vector, [vector, exponentVector]), LlvmLinkage.InternalLinkage, "main", module);
            var block = new LlvmBasicBlock(context, "entry", function);
            var ret = new LlvmReturnInstruction(context, function.Arguments[0]);
            block.Append(ret);
            var builder = new LlvmTransformBuilder(ret, metrics);
            var classify = builder.CreateIsFPClass(function.Arguments[0], LlvmFPClassTest.Zero);
            Require(classify is LlvmCallInstruction classCall && ReferenceEquals(classify.Type, vector.GetWithNewType(LlvmType.GetInt1Ty(context))) &&
                classCall.GetArgument(1).Type.IsIntegerTy(32),
                "The builder infers the actual vector predicate result without emitting a scalar i1 call.");
            var scale = builder.CreateIntrinsic(LlvmIntrinsicKind.Ldexp, [function.Arguments[0], function.Arguments[1]]);
            Require(scale is LlvmCallInstruction { CalledFunction.Name: "llvm.ldexp.v2f32.v2i32" } && ReferenceEquals(scale.Type, vector),
                "The call factory passes the actual vector exponent overload rather than casting it to LlvmIntegerType.");
            var min = builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.MinNum, function.Arguments[0], function.Arguments[0]);
            Require(min is LlvmCallInstruction { CalledFunction.Name: "llvm.minnum.v2f32" } && ReferenceEquals(min.Type, vector),
                "The binary builder follows the floating vector declaration path and preserves the vector result.");
            var independent = LlvmIntrinsic.GetDeclaration(module, LlvmIntrinsicKind.Ldexp, vector, metrics, LlvmType.GetInt64Ty(context));
            Require(independent.Name == "llvm.ldexp.v2f32.i64" && independent.FunctionType.GetParamType(1).IsIntegerTy(64),
                "The pinned ldexp td leaves exponent shape independently overloaded; declaration construction must not invent a lane-count constraint.");
            var one = LlvmConstantFP.Get(floating, LlvmAPFloat.GetOne(floating.GetFltSemantics()));
            var zero = LlvmConstantFP.Get(floating, LlvmAPFloat.GetZero(floating.GetFltSemantics(), true));
            var values = LlvmConstantVector.Get([one, zero]);
            var target = new LlvmTransformBuilder(ret, metrics, targetFolder: new LlvmTargetFolder(module.DataLayout, metrics));
            var classes = target.CreateIsFPClass(values, LlvmFPClassTest.Zero);
            Require(classes is LlvmConstant && ((LlvmConstant)classes).GetAggregateElement(0) is LlvmConstantInt { IsZero: true } &&
                ((LlvmConstant)classes).GetAggregateElement(1) is LlvmConstantInt { IsOne: true },
                "TargetFolder receives the correct vector result type and folds scalar class-mask arguments per source lane.");
            var exponents = LlvmConstantVector.Get([LlvmConstantInt.GetSigned(LlvmType.GetInt32Ty(context), 2), LlvmConstantInt.GetSigned(LlvmType.GetInt32Ty(context), -3)]);
            var scaled = target.CreateIntrinsic(LlvmIntrinsicKind.Ldexp, [values, exponents]);
            Require(scaled is LlvmConstant && ((LlvmConstant)scaled).GetAggregateElement(0) is LlvmConstantFP { Value: var first } &&
                first.BitwiseIsEqual(new LlvmAPFloat(4.0).Convert(floating.GetFltSemantics()).Value) &&
                ((LlvmConstant)scaled).GetAggregateElement(1) is LlvmConstantFP { Value.IsZero: true, Value.IsNegative: true },
                "Vector ldexp folding uses each integer exponent and preserves signed-zero FP semantics.");
        }
    }
}
