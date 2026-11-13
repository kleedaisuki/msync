using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

namespace Msync.Probes;

internal static class NativeProbe
{
    internal static NativeProgram Prepare(NativeProgram program, BindingContext context) =>
        NativePreparation.Prepare(program, context);

    internal static async Task<NativeValue> RunAsync(NativeProgram program, ExecutionOptions? options = null)
    {
        NativeProgram executable = NativePreparation.Prepare(program, options);
        await using NativeInterpreter interpreter = new(executable, options);
        return await interpreter.ExecuteAsync();
    }

    internal static byte[] Encode(NativeProgram program)
    {
        using MemoryStream stream = new();
        NativeArtifact.Write(program, stream);
        return stream.ToArray();
    }

    internal static NativeProgram Decode(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        return NativeArtifact.Read(stream);
    }
}
