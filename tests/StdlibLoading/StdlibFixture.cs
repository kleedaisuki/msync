using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class StdlibFixture
{
    internal static async Task<NativeValue> Execute(NativeProgram program)
    {
        await using NativeInterpreter interpreter = new(program);
        return await interpreter.ExecuteAsync();
    }
}
