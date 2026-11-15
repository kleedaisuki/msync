using Msync.Core.Llvm;
using Msync.Optimization.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

internal static class FloatingIntrinsicProperties
{
    internal static int Run()
    {
        int cases = 0;
        foreach (var semantics in Enum.GetValues<LlvmAPFloatSemantics>())
        {
            var one = LlvmAPFloat.GetOne(semantics);
            LlvmAPFloat[] values = [LlvmAPFloat.GetZero(semantics), LlvmAPFloat.GetZero(semantics, true),
                LlvmAPFloat.GetSmallest(semantics), LlvmAPFloat.GetSmallest(semantics, true),
                LlvmAPFloat.GetSmallestNormalized(semantics), LlvmAPFloat.GetLargest(semantics),
                one, one.ChangeSign(), one.Add(one).Value, one.Scalbn(-1).Value];
            foreach (var value in values)
            {
                var fraction = value.Frexp(out int exponent);
                Program.Require(fraction.Scalbn(exponent).Value.BitwiseIsEqual(value), "frexp must reconstruct each represented finite input exactly, including signed zero.");
                if (!value.IsZero)
                {
                    Program.Require(fraction.ClearSign().Compare(one) == LlvmFloatCompare.LessThan &&
                        fraction.ClearSign().Compare(one.Scalbn(-1).Value) != LlvmFloatCompare.LessThan,
                        "frexp fraction must have magnitude in [0.5,1), independently of source precision.");
                }
                var known = new LlvmKnownFPClass(value);
                Program.Require(!known.IsKnownNever(value.IsZero ? C.Zero : value.IsDenormal ? C.Subnormal : C.Normal),
                    "KnownFPClass must use the APFloat format, not binary64 masks.");
                var bitcast = LlvmKnownFPClass.Bitcast(LlvmKnownBits.MakeConstant(value.BitcastToAPInt()), semantics);
                Program.Require(bitcast.GetKnownFPClasses() == known.GetKnownFPClasses() && bitcast.SignBit == known.SignBit,
                    "format-specific bitcast masks must agree with exact APFloat classification.");
                foreach (var destination in Enum.GetValues<LlvmAPFloatSemantics>())
                {
                    var converted = value.Convert(destination).Value;
                    var transfer = LlvmKnownFPClass.FPConvert(known, destination, semantics,
                        LlvmDenormalModeInfo.IEEE, LlvmDenormalModeInfo.IEEE);
                    Program.Require(!transfer.IsKnownNever(new LlvmKnownFPClass(converted).GetKnownFPClasses()),
                        "source fpconvert transfer must include the actual rounded result in every represented format pair.");
                }
                ++cases;
            }
            var exponentBits = LlvmAPFloat.GetInf(semantics).BitcastToAPInt().GetZExtValue();
            var signaling = LlvmAPFloat.FromBits(semantics, exponentBits | 1);
            var quiet = signaling.Frexp(out int unspecifiedExponent);
            Program.Require(quiet.IsNaN && !quiet.IsSignaling &&
                quiet.BitcastToAPInt() == signaling.MakeQuiet().BitcastToAPInt() && unspecifiedExponent == LlvmAPFloat.IEK_NaN,
                "frexp must quiet signaling NaNs using the operand's own payload and format.");
            LlvmContext context = new(); LlvmModule module = new("fp-formats", context);
            var type = LlvmConstantFP.Get(context, one).Type;
            var declaration = LlvmIntrinsic.GetDeclaration(module, LlvmIntrinsicKind.Frexp, type);
            Program.Require(declaration.ReturnType is LlvmStructType pair && pair.Elements[0] == type && pair.Elements[1].IsIntegerTy(32),
                "frexp must retain its actual aggregate result signature.");
            Program.Require(declaration.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Frexp, "frexp declarations must resolve through the actual registry.");
            var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]), LlvmLinkage.InternalLinkage, "positive-return", module);
            function.ReturnAttributes = new() { NoFPClass = C.NaN | C.Infinity | C.Negative };
            function.SetParamAttributes(0, new() { NoFPClass = C.NaN | C.Infinity | C.Negative });
            function.AddParamAttr(0, LlvmAttributeKind.NoUndef);
            LlvmBasicBlock block = new(context, "entry", function);
            var absolute = LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.FAbs, [function.Arguments[0]], module);
            block.Append(absolute); var result = new LlvmReturnInstruction(context, absolute); block.Append(result);
            var tree = new LlvmDominatorTree(function);
            using var assumptions = new LlvmAssumptionCache(function);
            LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions,
                new LlvmTargetLibraryInfo(module), new LlvmTargetTransformInfo(module.DataLayout), tree);
            Program.Require(result.ReturnValue == function.Arguments[0], "real InstCombine return nofpclass demand must remove a redundant fabs without changing unrelated uses.");
            ++cases;
        }
        return cases;
    }
}
