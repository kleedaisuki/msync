using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static async ValueTask<Value> FileSystemAsync(string operation, IReadOnlyList<Value> args, RuntimeContext context, CancellationToken token, NativeEffect effect)
    {
        if (operation == "temp_directory_path") return new PathValue(Path.GetTempPath(), context.WorkingDirectory);
        if (operation == "current_path") return new PathValue(context.WorkingDirectory, context.WorkingDirectory);
        if (operation == "status_known") return Bool(Text(args[0]) != "none");
        PathValue path = context.AsPath(args[0]);
        if (effect == NativeEffect.Pure)
        {
            Value? lexical = LexicalOperation(operation, args, path, context);
            if (lexical is not null) return lexical;
        }
        if (operation is "copy" or "copy_file") return Bool(await TransferEngine.CopyAsync(operation == "copy_file" ? context.AsPath(args[0]) : args[0], context.AsPath(args[1]), context, token));
        if (path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl)
            return await RemoteFileSystemAsync(operation, args, path, context, token);
        string local = context.LocalPath(path);
        if (operation is "directory_iterator" or "recursive_directory_iterator")
            return new RangeValue(EnumerateDirectory(path, local, operation.StartsWith("recursive", StringComparison.Ordinal), token), TypeRef.Path);
        return LocalFileSystem(operation, args, path, local, context);
    }

    private static Value? LexicalOperation(string name, IReadOnlyList<Value> args, PathValue path, RuntimeContext context)
    {
        bool projected = path.Connection is null && LocalWindows(path);
        NativePath value = new(path.Raw, LocalWindows(path), projected ? '/' : null);
        bool binaryText = name is "concat" or "append" or "replace_filename" or "replace_extension";
        string? rawArgument = binaryText ? args[1] is PathValue argumentPath ? argumentPath.Raw : Text(args[1]) : null;
        string? nativeArgument = projected && binaryText ? Text(args[1]) : rawArgument;
        string? text = LexicalText(name, value, rawArgument);
        if (text is not null) return path.WithRaw(text, projected ? LexicalText(name, new(path.NativeText, true), nativeArgument) : null);
        if (name is "lexically_relative" or "lexically_proximate")
        {
            PathValue basis = context.AsPath(args[1]);
            string relative = path.Connection == basis.Connection ? value.RelativeTo(new NativePath(basis.Raw, value.Windows)) : "";
            string? nativeRelative = projected && path.Connection == basis.Connection ? new NativePath(path.NativeText, true).RelativeTo(new NativePath(basis.NativeText, true)) : null;
            bool original = name == "lexically_proximate" && relative.Length == 0;
            return original ? path : path.WithRaw(relative, nativeRelative);
        }
        if (name == "components") return new RangeValue(projected
            ? value.Components().Zip(new NativePath(path.NativeText, true).Components(), (raw, local) => (Value)path.WithRaw(raw, local))
            : value.Components().Select(component => (Value)path.WithRaw(component)), TypeRef.Path);
        if (name is "string" or "native" or "generic_string")
            return new StringValue(name == "generic_string" && value.Windows ? path.Raw.Replace('\\', '/') : name == "generic_string" ? path.Raw : path.NativeText);
        bool? result = name switch
        {
            "has_root_name" => value.RootName.Length > 0,
            "has_root_directory" => value.RootDirectory,
            "has_root_path" => value.Root.Length > 0,
            "has_relative_path" => value.Relative.Length > 0,
            "has_parent_path" => value.Parent.Length > 0,
            "has_filename" => value.Filename.Length > 0,
            "has_stem" => NativePath.SplitExtension(value.Filename).Stem.Length > 0,
            "has_extension" => NativePath.SplitExtension(value.Filename).Extension.Length > 0,
            "is_absolute" => value.IsAbsolute,
            "is_relative" => !value.IsAbsolute,
            "path_empty" => path.Raw.Length == 0,
            _ => null
        };
        return result is bool answer ? Bool(answer) : null;
    }

    private static string? LexicalText(string name, NativePath value, string? argument) => name switch
    {
        "root_name" => value.RootName,
        "root_directory" => value.RootDirectory ? value.Separator.ToString() : "",
        "root_path" => value.Root,
        "relative_path" => value.Relative,
        "parent_path" => value.Parent,
        "filename" => value.Filename,
        "stem" => NativePath.SplitExtension(value.Filename).Stem,
        "extension" => NativePath.SplitExtension(value.Filename).Extension,
        "lexically_normal" => value.Normal(),
        "remove_filename" => value.RemoveFilename,
        "make_preferred" => value.Preferred,
        "concat" => value.Source + argument,
        "append" => value.Append(argument!),
        "replace_filename" => new NativePath(value.RemoveFilename, value.Windows, value.Separator).Append(argument!),
        "replace_extension" => value.Source[..(value.Source.Length - NativePath.SplitExtension(value.Filename).Extension.Length)] + Extension(argument!),
        _ => null
    };

    private static string Extension(string extension) => extension.Length == 0 || extension.StartsWith('.') ? extension : "." + extension;

    private static Value LocalFileSystem(string name, IReadOnlyList<Value> args, PathValue path, string local, RuntimeContext context)
    {
        if (name == "exists") return Bool(Status(local, false) != "not_found");
        if (name is "status" or "symlink_status") return new StringValue(Status(local, name == "symlink_status"));
        if (name == "is_other") return Bool(Status(local, false) is not ("regular" or "directory" or "symlink" or "not_found"));
        if (name.StartsWith("is_", StringComparison.Ordinal) && name is not ("is_empty" or "is_readonly"))
            return Bool(Status(local, name == "is_symlink") == (name switch { "is_regular_file" => "regular", "is_block_file" => "block", "is_character_file" => "character", _ => name[3..] }));
        if (name == "absolute") return path.WithRaw(local) with { WorkingDirectory = context.WorkingDirectory };
        if (name is "canonical" or "weakly_canonical")
            return path.WithRaw(Canonical(local, name == "canonical")) with { WorkingDirectory = context.WorkingDirectory };
        if (name is "relative" or "proximate")
        {
            string first = Canonical(local, false), second = Canonical(context.LocalPath(args[1]), false);
            return path.WithRaw(Path.GetRelativePath(second, first));
        }
        if (name == "read_symlink") return path.WithRaw(new FileInfo(local).LinkTarget ?? new DirectoryInfo(local).LinkTarget ?? throw new IOException($"'{local}' is not a symbolic link"));
        if (name == "is_empty")
        {
            string kind = Status(local, false);
            return kind switch
            {
                "directory" => Bool(!Directory.EnumerateFileSystemEntries(local).Any()),
                "regular" => Bool(new FileInfo(local).Length == 0),
                _ => throw new IOException($"fs.is_empty requires an existing regular file or directory: '{local}' ({kind})")
            };
        }
        if (name == "permissions") return new RangeValue(Permissions(local).Select(permission => (Value)new StringValue(permission)), TypeRef.String);
        if (name == "is_readonly") return Bool(OperatingSystem.IsWindows() ? (File.GetAttributes(local) & FileAttributes.ReadOnly) != 0 : (File.GetUnixFileMode(local) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0);
        if (name == "set_readonly")
        {
            bool readOnly = Boolean(args[1]);
            if (OperatingSystem.IsWindows())
            {
                FileAttributes attributes = File.GetAttributes(local);
                File.SetAttributes(local, readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
            }
            else
            {
                UnixFileMode mode = File.GetUnixFileMode(local);
                UnixFileMode writes = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
                File.SetUnixFileMode(local, readOnly ? mode & ~writes : mode | writes);
            }
            return Value.Null;
        }
        if (name == "set_permissions") { SetPermissions(local, Text(args[1])); return Value.Null; }
        if (name == "touch")
        {
            DateTime timestamp = DateTime.UtcNow;
            if (Directory.Exists(local))
            {
                Directory.SetLastAccessTimeUtc(local, timestamp);
                Directory.SetLastWriteTimeUtc(local, timestamp);
            }
            else if (File.Exists(local))
            {
                File.SetLastAccessTimeUtc(local, timestamp);
                File.SetLastWriteTimeUtc(local, timestamp);
            }
            else
            {
                using var file = File.OpenHandle(local, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                File.SetLastAccessTimeUtc(file, timestamp);
                File.SetLastWriteTimeUtc(file, timestamp);
            }
            return Value.Null;
        }
        if (name == "equivalent") return Bool(NativeFileIdentity.Equivalent(local, context.LocalPath(args[1])));
        if (name is "create_directory" or "create_directories")
        {
            bool exists = Directory.Exists(local);
            if (name == "create_directory" && Path.GetDirectoryName(local) is string parent && !Directory.Exists(parent))
                throw new DirectoryNotFoundException($"Parent directory '{parent}' does not exist");
            Directory.CreateDirectory(local);
            return Bool(!exists);
        }
        if (name is "remove" or "remove_all" or "delete")
        {
            FileAttributes? attributes = Attributes(local);
            if (attributes is null) return name == "delete" ? Value.Null : Value.False;
            bool directory = (attributes.Value & FileAttributes.Directory) != 0 && (attributes.Value & FileAttributes.ReparsePoint) == 0;
            if (name == "delete" && directory) throw new IOException($"fs.delete does not recursively remove directory '{local}'; use fs.remove_all");
            if ((attributes.Value & FileAttributes.Directory) != 0) Directory.Delete(local, name == "remove_all" && directory); else File.Delete(local);
            return name == "delete" ? Value.Null : Value.True;
        }
        string destination = args.Count > 1 ? context.LocalPath(args[1]) : "";
        switch (name)
        {
            case "rename":
                if (Directory.Exists(local) && (File.GetAttributes(local) & FileAttributes.ReparsePoint) == 0) Directory.Move(local, destination);
                else File.Move(local, destination, true);
                return Value.Null;
            case "create_hard_link": NativeFileIdentity.CreateHardLink(destination, local); return Value.Null;
            case "create_symlink": File.CreateSymbolicLink(destination, path.NativeText); return Value.Null;
            case "create_directory_symlink": Directory.CreateSymbolicLink(destination, path.NativeText); return Value.Null;
            case "copy_symlink":
                string target = new FileInfo(local).LinkTarget ?? new DirectoryInfo(local).LinkTarget ?? throw new IOException($"'{local}' is not a symbolic link");
                File.CreateSymbolicLink(destination, target); return Value.Null;
            default: throw new MsyncException($"Unknown filesystem native '{name}'");
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string Status(string path, bool noFollow)
    {
        if (!OperatingSystem.IsWindows()) return NativeFileIdentity.Kind(path, noFollow);
        FileAttributes? attributes = Attributes(path);
        if (attributes is null) return "not_found";
        if (noFollow && (attributes.Value & FileAttributes.ReparsePoint) != 0) return "symlink";
        if (!noFollow && (attributes.Value & FileAttributes.ReparsePoint) != 0 && File.ResolveLinkTarget(path, true)?.Exists == false) return "not_found";
        if ((attributes.Value & FileAttributes.Directory) != 0) return "directory";
        return "regular";
    }

    private static string Canonical(string local, bool required)
    {
        string root = Path.GetPathRoot(local)!;
        string current = root;
        foreach (string component in local[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileAttributes? attributes = Attributes(current);
            if (attributes is null) { if (required) throw new FileNotFoundException($"Cannot canonicalize missing path '{current}'", current); continue; }
            if ((attributes.Value & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo info = (attributes.Value & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            current = info.ResolveLinkTarget(true)?.FullName ?? current;
        }
        return Path.GetFullPath(current);
    }

    private static IEnumerable<string> Permissions(string local)
    {
        if (OperatingSystem.IsWindows()) return (File.GetAttributes(local) & FileAttributes.ReadOnly) != 0 ? ["owner_read", "group_read", "others_read"] : ["owner_read", "owner_write", "group_read", "others_read"];
        UnixFileMode mode = File.GetUnixFileMode(local);
        return PermissionBits.Where(pair => (mode & pair.Value) != 0).Select(pair => pair.Key);
    }

    private static readonly IReadOnlyDictionary<string, UnixFileMode> PermissionBits = new Dictionary<string, UnixFileMode>(StringComparer.Ordinal)
    {
        ["owner_read"] = UnixFileMode.UserRead,
        ["owner_write"] = UnixFileMode.UserWrite,
        ["owner_exec"] = UnixFileMode.UserExecute,
        ["group_read"] = UnixFileMode.GroupRead,
        ["group_write"] = UnixFileMode.GroupWrite,
        ["group_exec"] = UnixFileMode.GroupExecute,
        ["others_read"] = UnixFileMode.OtherRead,
        ["others_write"] = UnixFileMode.OtherWrite,
        ["others_exec"] = UnixFileMode.OtherExecute,
        ["set_uid"] = UnixFileMode.SetUser,
        ["set_gid"] = UnixFileMode.SetGroup,
        ["sticky_bit"] = UnixFileMode.StickyBit
    };

    private static void SetPermissions(string local, string flags)
    {
        if (OperatingSystem.IsWindows())
        {
            bool write = (PermissionMode(flags) & 0x92) != 0;
            FileAttributes attributes = File.GetAttributes(local);
            File.SetAttributes(local, write ? attributes & ~FileAttributes.ReadOnly : attributes | FileAttributes.ReadOnly);
            return;
        }
        File.SetUnixFileMode(local, (UnixFileMode)PermissionMode(flags));
    }

    private static int PermissionMode(string flags)
    {
        string octal = flags.StartsWith("0o", StringComparison.Ordinal) ? flags[2..] : flags;
        if (octal.Length > 0 && octal.All(character => character is >= '0' and <= '7'))
        {
            int parsed = Convert.ToInt32(octal, 8);
            return parsed <= 0xfff ? parsed : throw new MsyncException($"Permission mode outside filesystem flag range: '{flags}'");
        }
        int mode = 0;
        foreach (string flag in flags.Split([' ', '|', ','], StringSplitOptions.RemoveEmptyEntries))
            mode |= flag switch
            {
                "none" => 0,
                "owner_all" => 0x1c0,
                "group_all" => 0x38,
                "others_all" => 7,
                "all" => 0x1ff,
                _ => PermissionBits.TryGetValue(flag, out UnixFileMode bit) ? (int)bit : throw new MsyncException($"Unknown permission flag '{flag}'")
            };
        return mode;
    }

    private static async IAsyncEnumerable<Value> EnumerateDirectory(PathValue path, string local, bool recursive,
        [EnumeratorCancellation] CancellationToken token)
    {
        string[] entries = Directory.GetFileSystemEntries(local);
        Array.Sort(entries, TextSemantics.Ordinal);
        foreach (string entry in entries)
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            bool projected = path.Connection is null && LocalWindows(path);
            string raw = new NativePath(path.Raw, LocalWindows(path), projected ? '/' : null).Append(name);
            PathValue child = path.WithRaw(raw, projected ? new NativePath(path.NativeText, true).Append(name) : null);
            yield return child;
            FileAttributes attributes = File.GetAttributes(entry);
            if (!recursive || (attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) continue;
            await foreach (Value value in EnumerateDirectory(child, entry, true, token)) yield return value;
        }
    }
}
