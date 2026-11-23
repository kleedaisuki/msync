using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<LlvmFunction, int> scalarImports = [];
        private readonly Dictionary<string, int> textLiterals = new(StringComparer.Ordinal);
        internal readonly List<string> TextLiterals = [];
        private int textLayout = graph.SessionGeneration?.HpirLayouts?.TextLayout ?? -1;
        internal int TextLayoutIndex => textLayout;

        internal bool TryTextGlobal(LlvmGlobalVariable global, out string text)
        {
            if (graph.Constants.TryGetValue(global, out var constant) && constant.Kind == NativeKind.String)
            {
                text = constant.Text ?? "";
                return true;
            }
            text = null!;
            return false;
        }

        internal int TextLiteral(string value)
        {
            if (textLiterals.TryGetValue(value, out int index)) return index;
            index = TextLiterals.Count;
            TextLiterals.Add(value);
            textLiterals.Add(value, index);
            return index;
        }

        internal bool TryScalarImport(LlvmFunction target, out int index)
        {
            if (scalarImports.TryGetValue(target, out index)) return true;
            HpirImport? import = ScalarBinding(target);
            if (import is null) return false;
            index = Imports.Count;
            Imports.Add(import);
            scalarImports.Add(target, index);
            return true;
        }

        private HpirImport? ScalarBinding(LlvmFunction target)
        {
            LlvmFunctionType signature = target.FunctionType;
            if (graph.NativeCalls.TryGetValue(target, out string? name))
            {
                if (HpirWord32Imports.TryCreate(name, out var word)
                    && signature.NumParams == word.Arguments.Length && signature.ReturnType.IsDoubleTy()
                    && signature.Parameters.All(static type => type.IsDoubleTy()))
                    return word;
                if (name.StartsWith("math.", StringComparison.Ordinal)
                    && HpirScalarImports.TryMath(name.AsSpan("math.".Length), out HpirMathOp operation))
                    return MathBinding(operation, signature);
                if (HpirScalarImports.TryTextOutput(name, out HpirTextOutput output)
                    && (HpirScalarImports.FlushOnly(output) ? signature.NumParams == 0
                        : signature.NumParams == 1 && IsGuestPointer(signature.Parameters[0]))
                    && (signature.ReturnType.IsVoidTy() || signature.ReturnType is LlvmStructType { Elements.Count: 0 }))
                    return HpirScalarImports.OutputCall(output);
                return null;
            }
            if (graph.RuntimeConversions.TryGetValue(target, out var conversion)
                && conversion.Operation == LlvmRuntimeConversionOperation.Convert && conversion.TargetType >= 0
                && graph.Types[conversion.TargetType].Kind == TypeKind.String && signature.NumParams == 1
                && signature.Parameters[0].IsDoubleTy() && IsGuestPointer(signature.ReturnType))
                return HpirScalarImports.NumberTextCall(TextLayout());
            if (graph.RuntimeCalls.TryGetValue(target, out var binary) && binary == NativeBinary.Power)
                return MathBinding(HpirMathOp.Pow, signature);
            if (target.IntrinsicDescriptor is not { } intrinsic) return null;
            return MathBinding(IntrinsicMath(intrinsic.Kind), signature);
        }

        private static HpirImport? MathBinding(HpirMathOp operation, LlvmFunctionType signature)
        {
            if (operation == HpirMathOp.Invalid || !signature.Parameters.All(static type => type.IsDoubleTy())) return null;
            HpirImport import = HpirScalarImports.MathCall(operation);
            if (signature.NumParams != import.Arguments.Length) return null;
            return (import.Result == HpirWidth.U64
                ? signature.ReturnType.IsDoubleTy()
                : signature.ReturnType is LlvmIntegerType { BitWidth: 1 }) ? import : null;
        }

        private static HpirMathOp IntrinsicMath(LlvmIntrinsicKind kind)
        {
            if (kind == LlvmIntrinsicKind.FAbs) return HpirMathOp.Abs;
            if (kind == LlvmIntrinsicKind.Sqrt) return HpirMathOp.Sqrt;
            if (kind == LlvmIntrinsicKind.Floor) return HpirMathOp.Floor;
            if (kind == LlvmIntrinsicKind.Ceil) return HpirMathOp.Ceil;
            if (kind == LlvmIntrinsicKind.Trunc) return HpirMathOp.Trunc;
            if (kind == LlvmIntrinsicKind.Round) return HpirMathOp.Round;
            if (kind == LlvmIntrinsicKind.RoundEven) return HpirMathOp.RoundEven;
            if (kind == LlvmIntrinsicKind.Pow) return HpirMathOp.Pow;
            if (kind == LlvmIntrinsicKind.Exp) return HpirMathOp.Exp;
            if (kind == LlvmIntrinsicKind.Exp2) return HpirMathOp.Exp2;
            if (kind == LlvmIntrinsicKind.Log) return HpirMathOp.Log;
            if (kind == LlvmIntrinsicKind.Log2) return HpirMathOp.Log2;
            if (kind == LlvmIntrinsicKind.Log10) return HpirMathOp.Log10;
            if (kind == LlvmIntrinsicKind.Sin) return HpirMathOp.Sin;
            if (kind == LlvmIntrinsicKind.Cos) return HpirMathOp.Cos;
            if (kind == LlvmIntrinsicKind.FMA) return HpirMathOp.FusedMultiplyAdd;
            return HpirMathOp.Invalid;
        }

        internal int TextLayout()
        {
            if (textLayout >= 0) return textLayout;
            textLayout = Layouts.Count;
            Layouts.Add(new(HpirScalarImports.TextElementBytes, HpirScalarImports.TextElementBytes, []));
            return textLayout;
        }
    }

    private sealed partial class Body
    {
        private bool TryTextLiteral(LlvmLoadInstruction load)
        {
            if (!geometry.TryContextualLiteral(load, out var literal) || literal.Kind != NativeKind.String) return false;
            Add(new(HpirOp.AddressOfStatic, Location(load), Immediate: geometry.TextLiteral(literal.Text ?? "")), load.Source);
            return true;
        }

        private bool TryScalarImport(LlvmCallInstruction call)
        {
            if (TryReadyString(call) || TryReadyScalarCast(call)) return true;
            if (call.CalledFunction is not { } target || !geometry.TryScalarImport(target, out int import)) return false;
            EmitImportCall(call, import, call.Arguments.Select(Location).ToArray());
            return true;
        }
    }
}
