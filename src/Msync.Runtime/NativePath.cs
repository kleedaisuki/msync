namespace Msync.Runtime;

internal sealed class NativePath(string source, bool windows)
{
    public string Source { get; } = source;
    public bool Windows { get; } = windows;
    public char Separator => Windows ? '\\' : '/';
    public string RootName { get; } = source[..RootEnd(source, windows)];
    public bool RootDirectory => Source.Length > RootName.Length && IsSeparator(Source[RootName.Length]);
    public string Relative => Source[RootName.Length..].TrimStart(Windows ? ['/', '\\'] : ['/']);
    public bool IsAbsolute => RootDirectory && (!Windows || RootName.Length > 0);
    public string Root => RootName + (RootDirectory ? Separator.ToString() : "");
    public string Filename => Relative.Split(Windows ? ['/', '\\'] : ['/']).Last();
    public string Preferred => Windows ? Source.Replace('/', '\\') : Source;
    public string RemoveFilename => Source[..(Source.Length - Filename.Length)];
    public string[] Parts => Relative.Split(Windows ? ['/', '\\'] : ['/'], StringSplitOptions.RemoveEmptyEntries);

    public string Parent
    {
        get
        {
            if (Relative.Length == 0) return Source;
            string parent = RemoveFilename.TrimEnd(Windows ? ['/', '\\'] : ['/']);
            return parent.Length < RootName.Length || parent.Length == 0 && RootDirectory ? Root : parent;
        }
    }

    public bool IsSeparator(char character) => character == '/' || Windows && character == '\\';

    public string Normal()
    {
        if (Source.Length == 0 || Relative.Length == 0) return Preferred;
        List<string> parts = [];
        foreach (string part in Parts)
        {
            if (part == ".") continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != "..") { parts.RemoveAt(parts.Count - 1); continue; }
            if (part == ".." && RootDirectory) continue;
            parts.Add(part);
        }
        string result = Root + string.Join(Separator, parts);
        if (result.Length == 0) return ".";
        bool trailing = IsSeparator(Source[^1]) || Parts.LastOrDefault() is "." or "..";
        return trailing && parts.Count > 0 && parts[^1] != ".." ? result + Separator : result;
    }

    public string Append(string right)
    {
        NativePath child = new(right, Windows);
        if (child.IsAbsolute || child.RootName.Length > 0 && child.RootName != RootName) return right;
        if (child.RootDirectory) return RootName + right[child.RootName.Length..];
        string result = Source;
        if (result.Length > 0 && !IsSeparator(result[^1]) && Relative.Length > 0) result += Separator;
        return result + right[child.RootName.Length..];
    }

    public string RelativeTo(NativePath basis)
    {
        if (RootName != basis.RootName || IsAbsolute != basis.IsAbsolute || !RootDirectory && basis.RootDirectory) return "";
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
        if (RootName.Length > 0) yield return RootName;
        if (RootDirectory) yield return Relative.Length == 0 ? Source[RootName.Length..] : Separator.ToString();
        foreach (string part in Parts) yield return part;
        if (Relative.Length > 0 && IsSeparator(Source[^1])) yield return "";
    }

    public static (string Stem, string Extension) SplitExtension(string filename)
    {
        if (filename is "." or "..") return (filename, "");
        int dot = filename.LastIndexOf('.');
        return dot > 0 ? (filename[..dot], filename[dot..]) : (filename, "");
    }

    private static int RootEnd(string text, bool windows)
    {
        if (!windows) return 0;
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':') return 2;
        if (text.Length < 3 || !SeparatorAt(0) || !SeparatorAt(1) || SeparatorAt(2)) return 0;
        int index = 2;
        while (index < text.Length && !SeparatorAt(index)) ++index;
        return index;
        bool SeparatorAt(int index) => text[index] is '/' or '\\';
    }
}
