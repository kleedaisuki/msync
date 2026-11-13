using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private DirectoryScope EnterDeferredDirectory(NativeRuntimeContext origin)
    {
        var previous = deferredContext.Value;
        deferredContext.Value = origin;
        return new(this, previous);
    }

    private readonly struct DirectoryScope(NativeInterpreter owner, NativeRuntimeContext? previous) : IDisposable
    {
        public void Dispose() => owner.deferredContext.Value = previous;
    }

    // Iterators must restore the consumer's context before yielding, not only at disposal.
    private IAsyncEnumerable<NativeDatum> CaptureDatums(IAsyncEnumerable<NativeDatum> items) =>
        new ContextDatums(this, items, context.CaptureDirectory());

    private sealed class ContextDatums(NativeInterpreter owner, IAsyncEnumerable<NativeDatum> items,
        NativeRuntimeContext origin) : IAsyncEnumerable<NativeDatum>
    {
        public IAsyncEnumerator<NativeDatum> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new ContextIterator(owner, items.GetAsyncEnumerator(cancellationToken), origin);
    }

    private sealed class ContextIterator(NativeInterpreter owner, IAsyncEnumerator<NativeDatum> inner,
        NativeRuntimeContext origin) : IAsyncEnumerator<NativeDatum>
    {
        private readonly NativeRuntimeContext execution = origin.ForkDirectory();
        public NativeDatum Current => inner.Current;
        public async ValueTask<bool> MoveNextAsync()
        {
            using var directory = owner.EnterDeferredDirectory(execution);
            return await inner.MoveNextAsync();
        }
        public async ValueTask DisposeAsync()
        {
            using var directory = owner.EnterDeferredDirectory(execution);
            await inner.DisposeAsync();
        }
    }
}
