using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>>? nativeCallback;

    private Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> NativeCallback =>
        nativeCallback ??= InvokeAsync;
}
