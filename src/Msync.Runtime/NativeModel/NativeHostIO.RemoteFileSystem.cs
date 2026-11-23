using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static string RemotePrelude => field ??= ShellProgram.Source("""
        set -eu
        expand() { case "$1" in '~') printf '%s' "$HOME";; '~/'*) printf '%s/%s' "$HOME" "${1#??}";; *) printf '%s' "$1";; esac; }
        p=$(expand "$1"; printf .); p=${p%.}
        kind() {
            if value=$(LC_ALL=C stat "$@" -c %F -- "$p" 2>&1); then
                case "$value" in
                    regular*) printf regular;; directory) printf directory;; 'symbolic link') printf symlink;;
                    'block special file') printf block;; 'character special file') printf character;;
                    fifo) printf fifo;; socket) printf socket;; *) printf unknown;;
                esac
            else
                case "$value" in
                    *': No such file or directory'|*': Not a directory') printf not_found;;
                    *) printf '%s\n' "$value" >&2; return 1;;
                esac
            fi
        }
        """);

    private static string RemoteScript(string name) => name switch
    {
        "exists" => "value=$(kind -L); if [ \"$value\" = not_found ]; then printf false; else printf true; fi",
        "is_regular_file" => Predicate("regular"),
        "is_directory" => Predicate("directory"),
        "is_symlink" => Predicate("symlink"),
        "is_block_file" => Predicate("block"),
        "is_character_file" => Predicate("character"),
        "is_fifo" => Predicate("fifo"),
        "is_socket" => Predicate("socket"),
        "is_other" => "value=$(kind -L); case \"$value\" in block|character|fifo|socket|unknown) printf true;; *) printf false;; esac",
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
        "set_readonly" => "if [ \"$2\" = true ]; then chmod a-w -- \"$p\"; else chmod a+w -- \"$p\"; fi",
        "equivalent" => "q=$(expand \"$2\"; printf .); q=${q%.}; a=$(stat -L -c '%d:%i' -- \"$p\"); b=$(stat -L -c '%d:%i' -- \"$q\"); [ \"$a\" = \"$b\" ] && printf true || printf false",
        "create_directory" => "if [ -d \"$p\" ]; then printf false; else mkdir -- \"$p\"; printf true; fi",
        "create_directories" => "if [ -d \"$p\" ]; then printf false; else mkdir -p -- \"$p\"; printf true; fi",
        "delete" => "if [ -d \"$p\" ] && ! [ -L \"$p\" ]; then printf 'fs.delete requires a file or symlink: %s\\n' \"$p\" >&2; exit 1; fi; rm -f -- \"$p\"",
        "touch" => "touch -- \"$p\"",
        "remove" => "value=$(kind); if [ \"$value\" = not_found ]; then printf false; else if [ \"$value\" = directory ]; then rmdir -- \"$p\"; else rm -- \"$p\"; fi; printf true; fi",
        "remove_all" => "value=$(kind); if [ \"$value\" = not_found ]; then printf false; else rm -rf -- \"$p\"; printf true; fi",
        "remove_directory" => "value=$(kind); if [ \"$value\" = not_found ]; then printf false; else rmdir -- \"$p\"; printf true; fi",
        "rename" => "q=$(expand \"$2\"; printf .); q=${q%.}; mv -f -- \"$p\" \"$q\"",
        "create_hard_link" => "q=$(expand \"$2\"; printf .); q=${q%.}; ln -- \"$p\" \"$q\"",
        "create_symlink" or "create_directory_symlink" => "q=$(expand \"$2\"; printf .); q=${q%.}; ln -s -- \"$1\" \"$q\"",
        "copy_symlink" => "q=$(expand \"$2\"; printf .); q=${q%.}; t=$(readlink -n -- \"$p\"; printf .); t=${t%.}; ln -s -- \"$t\" \"$q\"",
        _ => throw new MsyncException($"Unknown remote filesystem native '{name}'")
    };

    private static string Predicate(string type) => $"value=$(kind {(type == "symlink" ? "" : "-L")}); if [ \"$value\" = {type} ]; then printf true; else printf false; fi";
    private static string RemoteStatus(bool noFollow) => noFollow ? "kind" : "kind -L";

    private static async IAsyncEnumerable<NativePathValue> RemoteDirectory(NativePathValue path, bool recursive, NativeRuntimeContext context,
        [EnumeratorCancellation] CancellationToken token)
    {
        string find = recursive ? "find -H \"$p\"" : "find -H \"$p\" -maxdepth 1";
        string script = RemotePrelude + "\n" + """
            printf '%s\0' "$p"
            if [ -n "${MSYNC_OWNED_DIRECTORY_FD:-}" ]; then
            """ + "\n" + find + " -samefile \"/proc/self/fd/$MSYNC_OWNED_DIRECTORY_FD\" -prune -o -print0\n"
            + "else\n" + find + " -print0\nfi";
        string output = await NativeHostIO.RunCommandAsync(path.Connection!.Value, script, [path.Raw], context, token);
        string[] names = output.Split('\0');
        foreach (string name in names.Skip(1).Where(name => name.Length != 0 && name != names[0]).Order(TextSemantics.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            yield return path.WithRaw(name);
        }
    }
}
