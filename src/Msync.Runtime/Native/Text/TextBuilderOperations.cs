using Msync.Core;

namespace Msync.Runtime;

internal static class TextBuilderOperations
{
    internal static ValueTask<Value> InvokeAsync(string operation, IReadOnlyList<Value> args, CancellationToken token)
    {
        if (operation is "format" or "format_fields") return TextFormatting.FormatAsync(operation, args);
        if (operation != "append_chunk") throw new MsyncException($"Unknown text native '{operation}'");
        RangeValue source = (RangeValue)args[0];
        string text = ((StringValue)args[1]).Data;
        return source.Items is TextChunks chunks
            ? ValueTask.FromResult<Value>(new RangeValue(new TextChunks(chunks, text), TypeRef.String))
            : AppendTextChunkAsync(source, text, token);
    }

    private static async ValueTask<Value> AppendTextChunkAsync(RangeValue source, string text, CancellationToken token)
    {
        source = await DeferredRangeSource.Demand(source, token);
        TextChunks? chunks = source.Items as TextChunks;
        if (chunks is not null) return new RangeValue(new TextChunks(chunks, text), TypeRef.String);
        if (source.Items is StringRangeItems.Sync strings)
        {
            foreach (var slice in strings.Read())
            {
                token.ThrowIfCancellationRequested();
                chunks = new(chunks, slice.ToOwnedString());
            }
        }
        else if (source.Items is StringRangeItems.Async stream)
        {
            await foreach (var slice in stream.Read.WithCancellation(token))
                chunks = new(chunks, slice.ToOwnedString());
        }
        else
        {
            await foreach (Value value in source.Items.WithCancellation(token))
            {
                if (value is not StringValue item)
                    throw new MsyncException($"text.append_chunk expects String elements, received {value.Type}.");
                chunks = new(chunks, item.Data);
            }
        }
        return new RangeValue(new TextChunks(chunks, text), TypeRef.String);
    }

    // Immutable nodes keep prior Range snapshots valid; iteration never recurses.
    private sealed class TextChunks(TextChunks? previous, string text) : StringRangeItems.Sync
    {
        private readonly TextChunks? previous = previous;
        private readonly string text = text;
        private readonly int count = checked((previous?.count ?? 0) + 1);

        internal override IEnumerable<TextSlice> Read()
        {
            string[] items = new string[count];
            TextChunks? node = this;
            for (int index = count - 1; index >= 0; index--)
            {
                items[index] = node!.text;
                node = node.previous;
            }
            foreach (string item in items) yield return new TextSlice(item);
        }
    }
}
