namespace Msync.Core;

internal readonly record struct TextSlice(string Text, int Offset, int Length)
{
    internal TextSlice(string text) : this(text, 0, text.Length) { }
    internal ReadOnlySpan<char> Span => Text.AsSpan(Offset, Length);
    internal string ToOwnedString() => Text.Substring(Offset, Length);
}
