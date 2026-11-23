using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly bool hasLineInput = graph.NativeCalls.Values.Any(static name => name is "io.read_line" or "io.read_stdin");

        internal bool HasLineInput => hasLineInput;
        internal bool HasFileText => graph.FileTextSources != 0;

        internal int FileTextImport(bool fromText)
        {
            _ = TextLayout();
            int layout = FutureLayout(-(int)HpirService.ReadLineText, HpirTextInputImports.Completion());
            return FutureImport(HpirTextInputImports.ReadFile(layout, fromText));
        }
        internal HpirService? StandardTextTarget(LlvmFunction target) => graph.NativeCalls.GetValueOrDefault(target) switch
        {
            "io.read_line" => HpirService.ReadLineText,
            "io.read_stdin" => HpirService.ReadStdinText,
            _ => null
        };

        internal bool ObservedTextArgument(LlvmCallInstruction call, int index) =>
            call.CalledFunction is { } target && graph.ObservedTextArguments?.TryGetValue(target, out int mask) == true
                && (mask & (1 << index)) != 0;
        internal HpirService? ObservedTextParse(LlvmFunction target) =>
            graph.RuntimeConversions.GetValueOrDefault(target)?.Operation switch
            {
                LlvmRuntimeConversionOperation.ObservedTextNumber => HpirService.ParseF64,
                LlvmRuntimeConversionOperation.ObservedTextBool => HpirService.ParseBool,
                _ => null
            };

        internal bool NarrowsText(LlvmCallInstruction call) =>
            call.CalledFunction is { } target && graph.RuntimeConversions.TryGetValue(target, out var conversion)
            && conversion.Operation == LlvmRuntimeConversionOperation.Represent
            && ConversionResult(call, conversion) == TypeKind.String;

        internal HpirService? TextProjection(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target || !graph.RuntimeConversions.TryGetValue(target, out var conversion)) return null;
            if (conversion.Operation == LlvmRuntimeConversionOperation.IsNull) return HpirService.TextIsNull;
            if (conversion.Operation == LlvmRuntimeConversionOperation.Convert && ConversionResult(call, conversion) == TypeKind.Number)
                return HpirService.TextParseNumber;
            return null;
        }

        private TypeKind? ConversionResult(LlvmCallInstruction call, LlvmRuntimeConversionBinding binding)
        {
            int type = binding.TargetType >= 0 ? binding.TargetType : call.LogicalType;
            return type < 0 ? null : graph.Types[type].Kind;
        }

        internal int TextInputImport(HpirService service)
        {
            _ = TextLayout();
            int key = -(int)service;
            HpirImport import = service switch
            {
                HpirService.ReadLineText => HpirTextInputImports.ReadLine(FutureLayout(key, HpirTextInputImports.Completion())),
                HpirService.ReadStdinText => HpirTextInputImports.ReadStdin(
                    FutureLayout(-(int)HpirService.ReadLineText, HpirTextInputImports.Completion())),
                HpirService.ProcessStdout or HpirService.ProcessStderr => HpirTextInputImports.ProcessOutput(
                    FutureLayout(-(int)HpirService.ReadLineText, HpirTextInputImports.Completion()), service),
                HpirService.DemandText => HpirTextInputImports.Demand(),
                HpirService.TextIsNull => HpirTextInputImports.IsNull(FutureLayout(key, HpirTextInputImports.NullProjection())),
                HpirService.TextParseNumber => HpirTextInputImports.ParseNumber(FutureLayout(key, HpirTextInputImports.NumberProjection())),
                _ => throw new ArgumentOutOfRangeException(nameof(service))
            };
            return FutureImport(import);
        }
    }
}
