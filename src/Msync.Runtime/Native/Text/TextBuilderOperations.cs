using Msync.Core;

namespace Msync.Runtime;

internal static class TextBuilderOperations
{
    internal static ValueTask<Value> InvokeAsync(string operation, IReadOnlyList<Value> args, CancellationToken token)
    {
        if (operation != "append_chunk") throw new MsyncException($"Unknown text native '{operation}'");
        RangeValue source = (RangeValue)args[0];
        StringValue text = (StringValue)args[1];
        return source.Items is TextChunks chunks
            ? ValueTask.FromResult<Value>(new RangeValue(new TextChunks(chunks, text), TypeRef.String))
            : AppendTextChunkAsync(source, text, token);
    }

    private static async ValueTask<Value> AppendTextChunkAsync(RangeValue source, StringValue text, CancellationToken token)
    {
        TextChunks? chunks = null;
        await foreach (Value value in source.Items.WithCancellation(token))
        {
            if (value is not StringValue item)
                throw new MsyncException($"text.append_chunk expects String elements, received {value.Type}.");
            chunks = new(chunks, item);
        }
        return new RangeValue(new TextChunks(chunks, text), TypeRef.String);
    }

    // Immutable nodes keep prior Range snapshots valid; iteration never recurses.
    private sealed class TextChunks(TextChunks? previous, StringValue text) : IAsyncEnumerable<Value>
    {
        private readonly TextChunks? previous = previous;
        private readonly StringValue text = text;
        private readonly int count = checked((previous?.count ?? 0) + 1);

        public async IAsyncEnumerator<Value> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Value[] items = new Value[count];
            TextChunks? node = this;
            for (int index = count - 1; index >= 0; index--)
            {
                items[index] = node!.text;
                node = node.previous;
            }
            foreach (Value item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
            await Task.CompletedTask;
        }
    }
}
