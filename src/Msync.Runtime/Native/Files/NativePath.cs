namespace Msync.Runtime;

internal enum NativePathComponent : byte { Filename, Stem, Extension }

internal readonly struct NativePath(string source, bool windows, char? separator = null)
{
    private readonly int rootLength = RootEnd(source, windows);
    public string Source { get; } = source;
    public bool Windows { get; } = windows;
    public char Separator => separator ?? (Windows ? '\\' : '/');
    private ReadOnlySpan<char> Separators => Windows ? "/\\" : "/";
    public ReadOnlySpan<char> RootNameSpan => Source.AsSpan(0, rootLength);
    public string RootName => Source[..rootLength];
    public bool RootDirectory => Source.Length > rootLength && IsSeparator(Source[rootLength]);
    public ReadOnlySpan<char> RelativeSpan => Source.AsSpan(rootLength).TrimStart(Separators);
    public string Relative => Source[(Source.Length - RelativeSpan.Length)..];
    public bool IsAbsolute => RootDirectory && (!Windows || rootLength > 0);
    public string Root => RootDirectory ? RootName + Separator : RootName;
    public string Preferred => Windows ? Source.Replace('/', Separator).Replace('\\', Separator) : Source;
    public string[] Parts => Relative.Split(Windows ? ['/', '\\'] : ['/'], StringSplitOptions.RemoveEmptyEntries);

    private int FilenameStart
    {
        get
        {
            var rest = Source.AsSpan(rootLength);
            int lastSeparator = Windows ? rest.LastIndexOfAny('/', '\\') : rest.LastIndexOf('/');
            return rootLength + lastSeparator + 1;
        }
    }

    public ReadOnlySpan<char> FilenameSpan => Source.AsSpan(FilenameStart);
    public string Filename => Source[FilenameStart..];
    public string RemoveFilename => Source[..FilenameStart];
    public ReadOnlySpan<char> StemSpan
    {
        get
        {
            var filename = FilenameSpan;
            return filename[..ExtensionStart(filename)];
        }
    }

    public string Stem
    {
        get
        {
            int start = FilenameStart;
            return Source.Substring(start, ExtensionStart(Source.AsSpan(start)));
        }
    }
    public ReadOnlySpan<char> ExtensionSpan
    {
        get
        {
            var filename = FilenameSpan;
            return filename[ExtensionStart(filename)..];
        }
    }

    public string Extension => Source[(Source.Length - ExtensionSpan.Length)..];

    public ReadOnlySpan<char> BorrowComponent(NativePathComponent component) => component switch
    {
        NativePathComponent.Filename => FilenameSpan,
        NativePathComponent.Stem => StemSpan,
        _ => ExtensionSpan,
    };

    public string MaterializeComponent(NativePathComponent component) => component switch
    {
        NativePathComponent.Filename => Filename,
        NativePathComponent.Stem => Stem,
        _ => Extension,
    };

    public string Parent
    {
        get
        {
            if (RelativeSpan.IsEmpty) return Source;
            int length = Source.AsSpan(0, FilenameStart).TrimEnd(Separators).Length;
            return length < rootLength || length == 0 && RootDirectory ? Root : Source[..length];
        }
    }

    public bool IsSeparator(char character) => character == '/' || Windows && character == '\\';

    public string Normal()
    {
        if (RelativeSpan.IsEmpty) return Preferred;
        string[] sourceParts = Parts;
        List<string> parts = [];
        foreach (string part in sourceParts)
        {
            if (part == ".") continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != "..") { parts.RemoveAt(parts.Count - 1); continue; }
            if (part == ".." && RootDirectory) continue;
            parts.Add(part);
        }
        string result = Root + string.Join(Separator, parts);
        if (result.Length == 0) return ".";
        bool trailing = IsSeparator(Source[^1]) || sourceParts.LastOrDefault() is "." or "..";
        return trailing && parts.Count > 0 && parts[^1] != ".." ? result + Separator : result;
    }

    public string Append(string right) => Append(right.AsSpan(), right);
    public string Append(ReadOnlySpan<char> right) => Append(right, null);

    private string Append(ReadOnlySpan<char> right, string? owned)
    {
        int childRoot = RootEnd(right, Windows);
        bool rooted = right.Length > childRoot && IsSeparator(right[childRoot]);
        bool absolute = rooted && (!Windows || childRoot > 0);
        if (absolute || childRoot > 0 && !right[..childRoot].SequenceEqual(RootNameSpan))
            return owned ?? right.ToString();
        var suffix = right[childRoot..];
        if (rooted) return string.Concat(RootNameSpan, suffix);
        if (Source.Length == 0 || IsSeparator(Source[^1]) || RelativeSpan.IsEmpty)
            return string.Concat(Source, suffix);
        ReadOnlySpan<char> delimiter = stackalloc char[] { Separator };
        return string.Concat(Source, delimiter, suffix);
    }

    public string RelativeTo(NativePath basis)
    {
        if (!RootNameSpan.SequenceEqual(basis.RootNameSpan) || IsAbsolute != basis.IsAbsolute || !RootDirectory && basis.RootDirectory) return "";
        string[] left = Parts;
        string[] right = basis.Parts;
        int common = 0;
        while (common < Math.Min(left.Length, right.Length) && left[common] == right[common]) ++common;
        int count = right[common..].Sum(part => part switch { "." => 0, ".." => -1, _ => 1 });
        if (count < 0) return "";
        string[] result = [.. Enumerable.Repeat("..", count), .. left[common..]];
        if (result.Length == 0) return ".";
        string text = string.Join(Separator, result);
        return Source.Length > 0 && IsSeparator(Source[^1]) ? text + Separator : text;
    }

    public IEnumerable<string> Components()
    {
        if (rootLength > 0) yield return RootName;
        if (RootDirectory) yield return RelativeSpan.IsEmpty ? Source[rootLength..] : Separator.ToString();
        foreach (string part in Parts) yield return part;
        if (!RelativeSpan.IsEmpty && IsSeparator(Source[^1])) yield return "";
    }

    private static int ExtensionStart(ReadOnlySpan<char> filename)
    {
        int dot = filename.LastIndexOf('.');
        return dot > 0 && !filename.SequenceEqual("..") ? dot : filename.Length;
    }

    private static int RootEnd(ReadOnlySpan<char> text, bool windows)
    {
        if (!windows) return 0;
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':') return 2;
        if (text.Length < 3 || !WindowsSeparator(text[0]) || !WindowsSeparator(text[1]) || WindowsSeparator(text[2])) return 0;
        int index = 2;
        while (index < text.Length && !WindowsSeparator(text[index])) ++index;
        return index;
    }

    private static bool WindowsSeparator(char character) => character is '/' or '\\';
}
