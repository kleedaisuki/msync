using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class NativeFixture
{
    internal static NativeConstant Constant(NativeValue value) => value.Kind switch
    {
        NativeKind.Null => new(NativeKind.Null),
        NativeKind.Bool => new(NativeKind.Bool, Boolean: value.AsBool()),
        NativeKind.Number => new(NativeKind.Number, Number: value.AsNumber()),
        NativeKind.String => new(NativeKind.String, value.AsString()),
        NativeKind.Path => new(NativeKind.Path) { Path = value.AsPath() },
        NativeKind.Type => new(NativeKind.Type, Type: value.AsType()),
        _ => throw new InvalidOperationException("This fixture requires portable literal data.")
    };

    internal static NativeProgram Codec(NativeProgram program)
    {
        using MemoryStream bytes = new();
        NativeArtifact.Write(program, bytes);
        bytes.Position = 0;
        return NativeArtifact.Read(bytes);
    }

    internal static async Task<NativeValue> Run(NativeProgram program, ExecutionOptions? options = null)
    {
        await using NativeInterpreter interpreter = new(program, options);
        return await interpreter.ExecuteAsync();
    }
}
