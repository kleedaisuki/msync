using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private RangeValue ReadIORange(string name, IReadOnlyList<Value> arguments, CancellationToken token)
    {
        var type = name == "io.read_lines" ? TypeRef.String : TypeRef.Number;
        return new(ReadIOItems(name, arguments, token), type);
    }

    private async IAsyncEnumerable<Value> ReadIOItems(string name, IReadOnlyList<Value> arguments,
        [EnumeratorCancellation] CancellationToken token)
    {
        var resolved = await ResolveArgumentsAsync(arguments);
        await AwaitNativeDependenciesAsync("io.read_text", resolved);
        var values = (RangeValue)await NativeRegistry.InvokeAsync(name, resolved, context, InvokeAsync, token);
        await foreach (var value in values.Items.WithCancellation(token)) yield return value;
    }
}
