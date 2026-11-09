using System.Text;
using System.Text.RegularExpressions;

namespace Msync.Cli;

internal readonly record struct SshHostCandidate(string Host, string Origin);

internal static class SshHostCatalog
{
    public static IReadOnlyList<SshHostCandidate> Read(string homeDirectory)
    {
        Reader reader = new(homeDirectory);
        string userDirectory = Path.Combine(homeDirectory, ".ssh");
        string systemDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetEnvironmentVariable("ProgramData")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh")
            : "/etc/ssh";
        if (Environment.GetEnvironmentVariable("MSYNC_SSH_CONFIG") is { Length: > 0 } selected)
        {
            if (selected != "none") reader.Config(selected, userDirectory);
        }
        else
        {
            reader.Config(Path.Combine(userDirectory, "config"), userDirectory);
            reader.Config(Path.Combine(systemDirectory, "ssh_config"), systemDirectory);
        }
        reader.KnownHosts(Path.Combine(userDirectory, "known_hosts"));
        reader.KnownHosts(Path.Combine(userDirectory, "known_hosts2"));
        reader.KnownHosts(Path.Combine(systemDirectory, "ssh_known_hosts"));
        reader.KnownHosts(Path.Combine(systemDirectory, "ssh_known_hosts2"));
        return reader.Candidates.Values.ToArray();
    }

    private sealed class Reader(string homeDirectory)
    {
        private readonly HashSet<string> visited = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        public Dictionary<string, SshHostCandidate> Candidates { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Config(string path, string baseDirectory)
        {
            path = Path.GetFullPath(path);
            if (!visited.Add(path)) return;
            foreach (string line in Lines(path))
            {
                string[] fields = Fields(line);
                if (fields.Length == 0) continue;
                if (fields[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string host in fields.Skip(1)) Add(host, path);
                }
                else if (fields[0].Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    // OpenSSH resolves relative includes from the user/system SSH directory,
                    // not from the directory containing the currently included file.
                    foreach (string pattern in fields.Skip(1))
                        foreach (string include in Expand(pattern, baseDirectory)) Config(include, baseDirectory);
                }
                else if (fields[0].Equals("UserKnownHostsFile", StringComparison.OrdinalIgnoreCase)
                    || fields[0].Equals("GlobalKnownHostsFile", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string pattern in fields.Skip(1).Where(field => field != "none"))
                        foreach (string file in Expand(pattern, baseDirectory)) KnownHosts(file);
                }
            }
        }

        public void KnownHosts(string path)
        {
            foreach (string line in Lines(path))
            {
                string[] fields = Fields(line);
                int hosts = fields.Length > 0 && fields[0].StartsWith('@') ? 1 : 0;
                if (fields.Length < hosts + 3) continue;
                foreach (string spelling in fields[hosts].Split(','))
                {
                    if (spelling.StartsWith('|')) continue;
                    string host = spelling;
                    if (spelling.StartsWith('[') && spelling.IndexOf("]:", StringComparison.Ordinal) is > 0 and var close)
                    {
                        string address = spelling[1..close];
                        host = address.Contains(':', StringComparison.Ordinal) ? spelling : address + spelling[(close + 1)..];
                    }
                    else if (spelling.Contains(':', StringComparison.Ordinal)) host = $"[{spelling}]";
                    Add(host, path);
                }
            }
        }

        private void Add(string host, string origin)
        {
            if (host.Length == 0 || host[0] is '!' or '|' || host.IndexOfAny(['*', '?']) >= 0) return;
            Candidates.TryAdd(host, new(host, origin));
        }

        private IEnumerable<string> Expand(string spelling, string baseDirectory)
        {
            string expanded = ExpandVariables(spelling, homeDirectory);
            if (expanded == "~") expanded = homeDirectory;
            else if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
                expanded = Path.Combine(homeDirectory, expanded[2..]);
            string pattern = Path.GetFullPath(expanded, baseDirectory);
            return Glob(pattern).Order(StringComparer.Ordinal);
        }
    }

    private static IEnumerable<string> Lines(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (string line in lines) yield return line;
    }

    private static string[] Fields(string line)
    {
        List<string> fields = [];
        StringBuilder word = new();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"') { quoted = !quoted; continue; }
            if (character == '#' && !quoted) break;
            if (character == '\\' && index + 1 < line.Length && (line[index + 1] is '"' or '\\' or '#' || char.IsWhiteSpace(line[index + 1])))
            { word.Append(line[++index]); continue; }
            if (!quoted && character == '=' && fields.Count == 1 && word.Length == 0) continue;
            if (!quoted && (char.IsWhiteSpace(character) || fields.Count == 0 && character == '='))
            {
                if (word.Length != 0) { fields.Add(word.ToString()); word.Clear(); }
                continue;
            }
            word.Append(character);
        }
        if (word.Length != 0) fields.Add(word.ToString());
        return fields.ToArray();
    }

    private static string ExpandVariables(string text, string homeDirectory)
    {
        StringBuilder result = new();
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '$' && index + 1 < text.Length && text[index + 1] == '{')
            {
                int end = text.IndexOf('}', index + 2);
                if (end >= 0)
                {
                    result.Append(Environment.GetEnvironmentVariable(text[(index + 2)..end]) ?? text[index..(end + 1)]);
                    index = end; continue;
                }
            }
            if (text[index] == '%' && index + 1 < text.Length && text[index + 1] is 'd' or 'u' or '%')
            {
                result.Append(text[++index] switch { 'd' => homeDirectory, 'u' => Environment.UserName, _ => "%" });
                continue;
            }
            result.Append(text[index]);
        }
        return result.ToString();
    }

    private static IEnumerable<string> Glob(string pattern)
    {
        string root = Path.GetPathRoot(pattern)!;
        IEnumerable<string> paths = [root];
        foreach (string component in pattern[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component.IndexOfAny(['*', '?', '[']) < 0)
            { paths = paths.Select(path => Path.Combine(path, component)); continue; }
            Regex matcher = GlobPattern(component);
            paths = paths.SelectMany(Entries).Where(path =>
                (!Path.GetFileName(path).StartsWith('.') || component.StartsWith('.')) && matcher.IsMatch(Path.GetFileName(path))).ToArray();
        }
        return paths;
    }

    private static IEnumerable<string> Entries(string directory)
    {
        try { return Directory.GetFileSystemEntries(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static Regex GlobPattern(string glob)
    {
        StringBuilder pattern = new("\\A");
        for (int index = 0; index < glob.Length; index++)
        {
            char character = glob[index];
            if (character == '\\' && !OperatingSystem.IsWindows() && index + 1 < glob.Length)
                pattern.Append(Regex.Escape(glob[++index].ToString()));
            else if (character == '*') pattern.Append(".*");
            else if (character == '?') pattern.Append('.');
            else if (character == '[' && ClosingBracket(glob, index) is > 0 and var end)
            {
                string contents = glob[(index + 1)..end];
                pattern.Append('[');
                if (contents.StartsWith('!')) { pattern.Append('^'); contents = contents[1..]; }
                foreach (char item in contents)
                {
                    if (item is '\\' or '[' or ']' or '^') pattern.Append('\\');
                    pattern.Append(item);
                }
                pattern.Append(']');
                index = end;
            }
            else pattern.Append(Regex.Escape(character.ToString()));
        }
        pattern.Append("\\z");
        return new(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
            | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));
    }

    private static int ClosingBracket(string glob, int opening)
    {
        int first = opening + 1;
        if (first < glob.Length && glob[first] == '!') first++;
        if (first < glob.Length && glob[first] == ']') first++;
        return glob.IndexOf(']', first);
    }
}
