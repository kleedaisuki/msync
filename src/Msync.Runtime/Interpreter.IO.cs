using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private RangeValue ReadIORange(string name, IReadOnlyList<Value> arguments, CancellationToken token)
    {
        var type = name == "io.read_lines" ? TypeRef.String : TypeRef.Number;
        return new(ReadIOItems(name, arguments, context.CaptureDirectory(), token), type)
        { RetainedValues = arguments };
    }

    private async IAsyncEnumerable<Value> ReadIOItems(string name, IReadOnlyList<Value> arguments, RuntimeContext origin,
        [EnumeratorCancellation] CancellationToken token)
    {
        var resolved = await ResolveArgumentsAsync(arguments);
        Value[] paths = [origin.AsPath(resolved[0])];
        await AwaitNativeDependenciesAsync("io.read_text", paths);
        var values = (RangeValue)await NativeRegistry.InvokeAsync(name, paths, origin, NativeCallback, token);
        await foreach (var value in values.Items.WithCancellation(token)) yield return value;
    }
}
