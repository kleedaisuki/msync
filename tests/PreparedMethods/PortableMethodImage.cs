using Msync.Core;
using Msync.Optimization;

internal static class PortableMethodImage
{
    private const int Null = 0, Number = 1, Bool = 2, Storage = 3;
    private static readonly CodeType[] Types =
    [
        new(TypeKind.Null), new(TypeKind.Number), new(TypeKind.Bool), new(TypeKind.Storage)
    ];

    internal static Module Portable(string name, TypeRef[] inputs, int locals, TypeRef result,
        Value[] constants, Instruction[] instructions) =>
        RoundTrip(new(constants,
        [
            new FunctionCode("entry", 0, 0, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null)
                { CodeSignature = new([], Null, []) },
            new FunctionCode(name, inputs.Length, locals, instructions, result, ParameterTypes: inputs)
                { CodeSignature = new(inputs.Select(CodeTypeId).ToArray(), CodeTypeId(result), []) }
        ], OptimizationLevel: 0) { CodeTypes = Types });

    internal static Module Bind(Module portable)
    {
        string cwd = Environment.CurrentDirectory;
        return RoundTrip(Binding.Relocate(portable, new(cwd, cwd)));
    }

    private static Module RoundTrip(Module image)
    {
        using MemoryStream bytes = new();
        Artifact.Write(image, bytes);
        bytes.Position = 0;
        return Artifact.Read(bytes, formBlocks: false);
    }

    private static int CodeTypeId(TypeRef type) => type.Kind switch
    {
        TypeKind.Null => Null,
        TypeKind.Number => Number,
        TypeKind.Bool => Bool,
        TypeKind.Storage => Storage,
        _ => throw new ArgumentException($"No fixture code type for {type}.", nameof(type))
    };
}
