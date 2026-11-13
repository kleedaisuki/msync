namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private static string Word(string text, int offset)
    {
        int start = offset, end = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        return text[start..end];
    }
}
