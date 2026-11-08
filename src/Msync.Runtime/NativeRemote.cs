using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private const string RemotePrelude = """
        set -eu
        expand() { case "$1" in '~') printf '%s' "$HOME";; '~/'*) printf '%s/%s' "$HOME" "${1#??}";; *) printf '%s' "$1";; esac; }
        p=$(expand "$1"; printf .); p=${p%.}
        kind() {
            if value=$(LC_ALL=C stat "$@" -c %F -- "$p" 2>&1); then
                case "$value" in
                    regular*) printf regular_file;; directory) printf directory;; 'symbolic link') printf symlink;;
                    'block special file') printf block_file;; 'character special file') printf character_file;;
                    fifo) printf fifo;; socket) printf socket;; *) printf other;;
                esac
            else
                case "$value" in
                    *': No such file or directory'|*': Not a directory') printf not_found;;
                    *) printf '%s\n' "$value" >&2; return 1;;
                esac
            fi
        }
        """;

    private static async ValueTask<Value> RemoteFileSystemAsync(string name, IReadOnlyList<Value> args, PathValue path,
        RuntimeContext context, CancellationToken token)
    {
        ConnectionValue connection = path.Connection!;
        if (name is "directory_iterator" or "recursive_directory_iterator")
            return new RangeValue(RemoteDirectory(path, name == "recursive_directory_iterator", token), TypeRef.Path);
        List<string> rawArgs = args.Select(value => value is BoolValue boolean ? boolean.Data ? "true" : "false" : Text(value)).ToList();
        if (name == "set_permissions") rawArgs[1] = Convert.ToString(PermissionMode(rawArgs[1]), 8);
        if (args.Count > 1 && args[1] is PathValue second && second.Connection is not null && second.Connection != connection)
            throw new MsyncException($"fs.{name} requires paths on the same connection, received '{connection}' and '{second.Connection}'");
        string script = RemotePrelude + "\n" + RemoteScript(name);
        string output = await TransferEngine.RunCommandAsync(connection, script, rawArgs, token);
        bool lineTerminated = name is "canonical" or "weakly_canonical" or "read_symlink" or "relative" or "proximate" or "permissions";
        string result = lineTerminated && output.EndsWith('\n') ? output[..^1] : output;
        if (name is "status" or "symlink_status") return new StringValue(result);
        if (name == "permissions") return new RangeValue(RemotePermissions(result).Select(flag => (Value)new StringValue(flag)), TypeRef.String);
        if (name is "absolute" or "canonical" or "weakly_canonical" or "read_symlink" or "relative" or "proximate") return path with { Raw = result };
        if (name is "exists" or "is_regular_file" or "is_directory" or "is_symlink" or "is_block_file" or "is_character_file" or "is_fifo" or "is_socket" or "is_other" or "is_empty" or "is_readonly" or "equivalent" or "create_directory" or "create_directories" or "remove" or "remove_all")
            return Bool(result == "true");
        return Value.Null;
    }

    private static string RemoteScript(string name) => name switch
    {
        "exists" => "value=$(kind -L); if [ \"$value\" = not_found ]; then printf false; else printf true; fi",
        "is_regular_file" => Predicate("regular_file"),
        "is_directory" => Predicate("directory"),
        "is_symlink" => Predicate("symlink"),
        "is_block_file" => Predicate("block_file"),
        "is_character_file" => Predicate("character_file"),
        "is_fifo" => Predicate("fifo"),
        "is_socket" => Predicate("socket"),
        "is_other" => "value=$(kind -L); case \"$value\" in block_file|character_file|fifo|socket|other) printf true;; *) printf false;; esac",
        "is_readonly" => "mode=$(stat -L -c %a -- \"$p\"); if [ $((0$mode & 0222)) -eq 0 ]; then printf true; else printf false; fi",
        "status" => RemoteStatus(false),
        "symlink_status" => RemoteStatus(true),
        "is_empty" => "if [ -d \"$p\" ]; then value=$(find \"$p\" -mindepth 1 -maxdepth 1 -printf x -quit); [ -z \"$value\" ] && printf true || printf false; elif [ -f \"$p\" ]; then [ ! -s \"$p\" ] && printf true || printf false; else stat -- \"$p\" >&2; exit 1; fi",
        "absolute" => "case \"$p\" in /*) printf '%s' \"$p\";; *) printf '%s/%s' \"$PWD\" \"$p\";; esac",
        "canonical" => "realpath -e -- \"$p\"",
        "weakly_canonical" => "realpath -m -- \"$p\"",
        "relative" or "proximate" => "b=$(expand \"$2\"; printf .); b=${b%.}; realpath -m --relative-to=\"$b\" -- \"$p\"",
        "read_symlink" => "readlink -- \"$p\"",
        "permissions" => "stat -L -c %a -- \"$p\"",
        "set_permissions" => "chmod -- \"$2\" \"$p\"",
        "set_readonly" => "if [ \"$2\" = true ]; then chmod a-w -- \"$p\"; else chmod u+w -- \"$p\"; fi",
        "equivalent" => "q=$(expand \"$2\"; printf .); q=${q%.}; a=$(stat -L -c '%d:%i' -- \"$p\"); b=$(stat -L -c '%d:%i' -- \"$q\"); [ \"$a\" = \"$b\" ] && printf true || printf false",
        "create_directory" => "if [ -d \"$p\" ]; then printf false; else mkdir -- \"$p\"; printf true; fi",
        "create_directories" => "if [ -d \"$p\" ]; then printf false; else mkdir -p -- \"$p\"; printf true; fi",
        "delete" => "if [ -d \"$p\" ] && ! [ -L \"$p\" ]; then printf 'fs.delete requires a file or symlink: %s\\n' \"$p\" >&2; exit 1; fi; rm -f -- \"$p\"",
        "remove" => "value=$(kind); if [ \"$value\" = not_found ]; then printf false; else if [ \"$value\" = directory ]; then rmdir -- \"$p\"; else rm -- \"$p\"; fi; printf true; fi",
        "remove_all" => "value=$(kind); if [ \"$value\" = not_found ]; then printf false; else rm -rf -- \"$p\"; printf true; fi",
        "rename" => "q=$(expand \"$2\"; printf .); q=${q%.}; mv -f -- \"$p\" \"$q\"",
        "create_hard_link" => "q=$(expand \"$2\"; printf .); q=${q%.}; ln -- \"$p\" \"$q\"",
        "create_symlink" or "create_directory_symlink" => "q=$(expand \"$2\"; printf .); q=${q%.}; ln -s -- \"$1\" \"$q\"",
        "copy_symlink" => "q=$(expand \"$2\"; printf .); q=${q%.}; t=$(readlink -n -- \"$p\"; printf .); t=${t%.}; ln -s -- \"$t\" \"$q\"",
        _ => throw new MsyncException($"Unknown remote filesystem native '{name}'")
    };

    private static string Predicate(string type) => $"value=$(kind {(type == "symlink" ? "" : "-L")}); if [ \"$value\" = {type} ]; then printf true; else printf false; fi";
    private static string RemoteStatus(bool noFollow) => noFollow ? "kind" : "kind -L";

    private static IEnumerable<string> RemotePermissions(string octal)
    {
        int mode = Convert.ToInt32(octal, 8);
        return PermissionBits.Where(pair => (mode & (int)pair.Value) != 0).Select(pair => pair.Key);
    }

    private static async IAsyncEnumerable<Value> RemoteDirectory(PathValue path, bool recursive,
        [EnumeratorCancellation] CancellationToken token)
    {
        string script = RemotePrelude + "\n" + (recursive ? "find \"$p\" -mindepth 1 -print0" : "find \"$p\" -mindepth 1 -maxdepth 1 -print0");
        string output = await TransferEngine.RunCommandAsync(path.Connection!, script, [path.Raw], token);
        foreach (string name in output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            yield return path with { Raw = name };
        }
    }
}
