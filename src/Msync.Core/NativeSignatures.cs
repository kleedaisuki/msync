using System.Diagnostics.CodeAnalysis;

namespace Msync.Core;

public enum NativeEffect : byte { Pure, Read, Write }
[Flags]
public enum NativeBehavior : byte { None = 0, MayFail = 1, Enumerates = 2, Deferred = 4, Invokes = 8, EagerRange = 16, LocalMemory = 32 }
public enum NativeResultRule : byte
{
    Fixed, FirstArgument, RangeOfArguments, RangeItem, Transform, CastTarget,
    RangeConcat, RangeFlatten, RangeZip, RangeEnumerate, RangeNested, RangeScan, RangeRepeat
}
public sealed record NativeSignature(string Name, IReadOnlyList<TypeRef> Parameters, TypeRef Result,
    NativeEffect Effect = NativeEffect.Pure, bool Variadic = false, NativeResultRule ResultRule = NativeResultRule.Fixed,
    NativeBehavior? Behavior = null);

public static partial class NativeSignatures
{
    private static readonly IReadOnlyDictionary<string, NativeSignature> Signatures = Build();
    public static IEnumerable<NativeSignature> All => Signatures.Values;
    public static bool TryGet(string name, [NotNullWhen(true)] out NativeSignature? signature) => Signatures.TryGetValue(name, out signature);
    public static NativeSignature Get(string name) => Signatures.TryGetValue(name, out NativeSignature? value) ? value : throw new KeyNotFoundException($"Unknown native function '{name}'");

    private static IReadOnlyDictionary<string, NativeSignature> Build()
    {
        Dictionary<string, NativeSignature> result = new(StringComparer.Ordinal);
        TypeRef path = TypeRef.Union(TypeRef.Path, TypeRef.String);
        TypeRef range = new(TypeKind.Range);
        TypeRef any = TypeRef.Any;
        void Add(string group, string names, TypeRef output, NativeEffect effect, params TypeRef[] inputs)
        {
            foreach (string name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result.Add($"{group}.{name}", new($"{group}.{name}", inputs, output, effect));
        }
        void Range(string names, TypeRef output, NativeResultRule rule, params TypeRef[] inputs)
        {
            foreach (string name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result.Add($"ranges.{name}", new($"ranges.{name}", inputs, output, ResultRule: rule));
        }
        Add("fs", "exists is_regular_file is_directory is_symlink is_block_file is_character_file is_fifo is_socket is_other is_empty is_readonly", TypeRef.Bool, NativeEffect.Read, path);
        Add("fs", "status symlink_status", TypeRef.String, NativeEffect.Read, path);
        Add("fs", "absolute canonical weakly_canonical read_symlink", TypeRef.Path, NativeEffect.Read, path);
        Add("fs", "temp_directory_path current_path", TypeRef.Path, NativeEffect.Read);
        Add("fs", "equivalent", TypeRef.Bool, NativeEffect.Read, path, path);
        Add("fs", "permissions", new(TypeKind.Range, TypeRef.String), NativeEffect.Read, path);
        Add("fs", "directory_iterator recursive_directory_iterator", new(TypeKind.Range, TypeRef.Path), NativeEffect.Read, path);
        Add("fs", "set_permissions", TypeRef.Null, NativeEffect.Write, path, TypeRef.String);
        Add("fs", "set_readonly", TypeRef.Null, NativeEffect.Write, path, TypeRef.Bool);
        Add("fs", "delete touch", TypeRef.Null, NativeEffect.Write, path);
        Add("fs", "create_directory create_directories remove remove_all", TypeRef.Bool, NativeEffect.Write, path);
        Add("fs", "rename create_hard_link create_symlink create_directory_symlink copy_symlink", TypeRef.Null, NativeEffect.Write, path, path);
        Add("fs", "copy_file copy", TypeRef.Bool, NativeEffect.Write, path, path);
        Add("fs", "root_name root_directory root_path relative_path parent_path filename stem extension lexically_normal remove_filename make_preferred", TypeRef.Path, NativeEffect.Pure, path);
        Add("fs", "lexically_relative lexically_proximate replace_filename replace_extension append concat relative proximate", TypeRef.Path, NativeEffect.Pure, path, path);
        Add("fs", "string generic_string native", TypeRef.String, NativeEffect.Pure, path);
        Add("fs", "has_root_name has_root_directory has_root_path has_relative_path has_parent_path has_filename has_stem has_extension is_absolute path_empty is_relative", TypeRef.Bool, NativeEffect.Pure, path);
        Add("fs", "components", new(TypeKind.Range, TypeRef.Path), NativeEffect.Pure, path);
        Add("fs", "status_known", TypeRef.Bool, NativeEffect.Pure, TypeRef.String);
        Add("string", "contains starts_with ends_with equal", TypeRef.Bool, NativeEffect.Pure, TypeRef.String, TypeRef.String);
        Add("string", "concat", TypeRef.String, NativeEffect.Pure, TypeRef.String, TypeRef.String);
        Add("string", "trim trim_start trim_end to_lowercase to_uppercase", TypeRef.String, NativeEffect.Pure, TypeRef.String);
        Add("string", "replace", TypeRef.String, NativeEffect.Pure, TypeRef.String, TypeRef.String, TypeRef.String);
        Add("string", "split", new(TypeKind.Range, TypeRef.String), NativeEffect.Pure, TypeRef.String, TypeRef.String);
        Add("string", "empty", TypeRef.Bool, NativeEffect.Pure, TypeRef.String);
        Add("bool", "identity negate", TypeRef.Bool, NativeEffect.Pure, TypeRef.Bool);
        Add("bool", "both either xor implies", TypeRef.Bool, NativeEffect.Pure, TypeRef.Bool, TypeRef.Bool);
        Add("path", "join", TypeRef.Path, NativeEffect.Pure, path, path);
        Add("path", "qualify", TypeRef.Path, NativeEffect.Pure, TypeRef.Connection, path);
        Add("path", "identity", TypeRef.Path, NativeEffect.Pure, path);
        Range("identity all as_const as_rvalue common reverse unique", range, NativeResultRule.FirstArgument, range);
        Range("take drop stride step", range, NativeResultRule.FirstArgument, range, TypeRef.Number);
        Range("filter take_while drop_while", range, NativeResultRule.FirstArgument, range, TypeRef.Function);
        Range("transform", range, NativeResultRule.Transform, range, TypeRef.Function);
        Range("front back first last", any, NativeResultRule.RangeItem, range);
        Range("find", any, NativeResultRule.RangeItem, range, any);
        Range("find_if", any, NativeResultRule.RangeItem, range, TypeRef.Function);
        Range("contains", TypeRef.Bool, NativeResultRule.Fixed, range, any);
        Range("empty", TypeRef.Bool, NativeResultRule.Fixed, range);
        Range("any any_of all_of none none_of", TypeRef.Bool, NativeResultRule.Fixed, range, TypeRef.Function);
        Range("single", range, NativeResultRule.RangeOfArguments, any);
        result.Add("ranges.of", new("ranges.of", [], range, Variadic: true, ResultRule: NativeResultRule.RangeOfArguments));
        Add("process", "stdout stderr", TypeRef.String, NativeEffect.Pure, TypeRef.Process);
        Add("process", "success", TypeRef.Bool, NativeEffect.Pure, TypeRef.Process);
        Add("process", "exit_code", TypeRef.Number, NativeEffect.Pure, TypeRef.Process);
        RegisterCast(result);
        RegisterRanges(result);
        RegisterTextPaths(result);
        RegisterIOProcess(result);
        RegisterMath(result);
        RegisterIteration(result);
        RegisterEnvironment(result);
        RegisterRegex(result);
        RegisterTextBuilder(result);
        RegisterStorage(result);
        RegisterMeta(result);
        RegisterShell(result);
        return result;
    }
}
