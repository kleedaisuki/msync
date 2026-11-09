namespace Msync.Core;

public sealed class MsyncException : Exception
{
    public SourceSpan? Span { get; }
    public MsyncException(string message, SourceSpan? span = null, Exception? inner = null) : base(message, inner) => Span = span;
}
