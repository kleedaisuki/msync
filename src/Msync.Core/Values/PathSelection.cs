namespace Msync.Core;

public sealed record PathSelection(PathValue Directory, string Pattern,
    Func<IAsyncEnumerable<Value>, CancellationToken, IAsyncEnumerable<Value>>? Apply = null, bool Recursive = false);
