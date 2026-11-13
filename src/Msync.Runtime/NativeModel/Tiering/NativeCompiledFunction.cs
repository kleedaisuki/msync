using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeCompiledFunction(NativeCodePage page, int bytes,
    NativeSiteShape?[] guards, NativeFastOperation[] operations, NativeTierLayout layout) : IDisposable
{
    internal int Bytes { get; } = bytes;
    internal NativeSiteShape?[] Guards { get; } = guards;
    internal NativeFastOperation[] Operations { get; } = operations;
    internal NativeTierLayout Layout { get; } = layout;

    internal static NativeCompiledFunction? Compile(NativeImage image, NativeExecutionFunction function, NativeJitPolicy policy)
    {
        if (!NativeCodePage.IsSupported || !BitConverter.IsLittleEndian || function.Metadata.Length == 0) return null;
        NativeSiteShape?[] guards = function.Profiles.Select(profile => profile?.Snapshot()).ToArray();
        NativeFastOperation[] operations = function.Instructions.Select((instruction, pc) => Select(image, function, instruction, guards[pc])).ToArray();
        if (policy == NativeJitPolicy.Auto && (!NativeMachineCode.HasDirectPaths
            || operations.All(operation => operation == NativeFastOperation.Bridge))) return null;
        NativeTierLayout layout = NativeTierLayout.Create(function, operations);
        byte[] bytes = NativeMachineCode.Emit(function, operations, RuntimeInformation.ProcessArchitecture,
            OperatingSystem.IsWindows(), NativeInterpreter.TierBridgeAddress, NativeInterpreter.TierNumberAddress,
            NativeInterpreter.TierBoolAddress, image.Program.Constants, layout);
        NativeCodePage? page = NativeCodePage.Create(bytes);
        return page is null ? null : new(page, bytes.Length, guards, operations, layout);
    }

    private static NativeFastOperation Select(NativeImage image, NativeExecutionFunction executable, NativeExecutionInstruction instruction,
        NativeSiteShape? observed)
    {
        NativeFunctionCode function = executable.Definition;
        bool Kind(int slot, TypeKind kind) => slot >= 0 && image.Program.Types[function.Slots[slot].Type].Pointee < 0
            && (image.Program.Types[function.Slots[slot].Type].Kind == kind);
        if (instruction.Op == NativeOp.Constant)
            return image.Program.Constants[instruction.A].Kind switch
            {
                NativeKind.Number => NativeFastOperation.NumberConstant,
                NativeKind.Bool => NativeFastOperation.BoolConstant,
                _ => NativeFastOperation.Bridge
            };
        if (instruction.Op == NativeOp.Copy && Kind(instruction.A, TypeKind.Number)) return NativeFastOperation.NumberCopy;
        if (instruction.Op == NativeOp.Copy && Kind(instruction.A, TypeKind.Bool)) return NativeFastOperation.BoolCopy;
        if (instruction.Op == NativeOp.Narrow && Kind(instruction.Result, TypeKind.Number)) return NativeFastOperation.NumberCopy;
        if (instruction.Op == NativeOp.Narrow && Kind(instruction.Result, TypeKind.Bool)) return NativeFastOperation.BoolCopy;
        if (instruction.Op == NativeOp.Unary)
            return (NativeUnary)instruction.Immediate switch
            {
                NativeUnary.Negate when Kind(instruction.A, TypeKind.Number) => NativeFastOperation.NumberNegate,
                NativeUnary.Not when Kind(instruction.A, TypeKind.Bool) => NativeFastOperation.BoolNot,
                _ => NativeFastOperation.Bridge
            };
        if (instruction.Op != NativeOp.Binary || !(Kind(instruction.A, TypeKind.Number) && Kind(instruction.B, TypeKind.Number)
            || observed is { First: NativeKind.Number, Second: NativeKind.Number })) return NativeFastOperation.Bridge;
        return (NativeBinary)instruction.Immediate switch
        {
            NativeBinary.Add => NativeFastOperation.NumberAdd,
            NativeBinary.Subtract => NativeFastOperation.NumberSubtract,
            NativeBinary.Multiply => NativeFastOperation.NumberMultiply,
            NativeBinary.Divide => NativeFastOperation.NumberDivide,
            NativeBinary.Equal => NativeFastOperation.NumberEqual,
            NativeBinary.NotEqual => NativeFastOperation.NumberNotEqual,
            NativeBinary.Less => NativeFastOperation.NumberLess,
            NativeBinary.LessEqual => NativeFastOperation.NumberLessEqual,
            NativeBinary.Greater => NativeFastOperation.NumberGreater,
            NativeBinary.GreaterEqual => NativeFastOperation.NumberGreaterEqual,
            _ => NativeFastOperation.Bridge
        };
    }

    internal int Run(nint context, int pc) => page.RunTier(context, pc);
    public void Dispose() => page.Dispose();
}
