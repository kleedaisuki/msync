using Msync.Core.Llvm;

namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static void ValidateFunctionPart(NativeProgram program, NativeFunctionCode function, NativeInstruction instruction)
    {
        NativeCodeType Type(int slot)
        {
            Register(function, instruction, slot);
            return program.Types[function.Slots[slot].Type];
        }
        bool Code(NativeCodeType type) => type.Machine is { IsInteger: true, BitWidth: 64 };
        bool Reference(NativeCodeType type, TypeKind kind) => !type.IsMachine && type.Pointee < 0 &&
            (type.Kind & ~(kind | TypeKind.Null)) == 0 && type.Kind != TypeKind.Never;
        void Require(bool condition, string cause) { if (!condition) throw Error(instruction, cause); }

        if (instruction.Op == NativeOp.FunctionComponent)
        {
            RequireKind(program, function, instruction, instruction.A, TypeKind.Function);
            var result = Type(instruction.Result);
            Require(instruction.Immediate switch
            {
                0 => Code(result),
                1 => Reference(result, TypeKind.Scope),
                2 => Reference(result, TypeKind.String),
                _ => false
            }, "Function component disagrees with its compiled scalar representation");
            return;
        }
        RequireResult(program, function, instruction, TypeKind.Function);
        IReadOnlyList<int> inputs = instruction.Inputs ?? [];
        Require(inputs.Count == 3, "Function composition requires code, environment and native name operands");
        Require(Code(Type(inputs[0])), "Function code token requires machine i64");
        Require(Reference(Type(inputs[1]), TypeKind.Scope), "Function environment requires a nullable object address");
        Require(Reference(Type(inputs[2]), TypeKind.String), "Function native name requires a nullable string");
    }
}
