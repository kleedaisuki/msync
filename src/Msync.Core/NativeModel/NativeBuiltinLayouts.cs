namespace Msync.Core.Native;

public readonly record struct NativeBuiltinField(string Name, TypeRef Type);

public sealed class NativeBuiltinRecordSchema
{
    internal NativeBuiltinRecordSchema(params NativeBuiltinField[] fields) => Fields = Array.AsReadOnly(fields);

    public IReadOnlyList<NativeBuiltinField> Fields { get; }
}

public static class NativeBuiltinLayouts
{
    public static NativeBuiltinRecordSchema EnvironmentVariable { get; } = new(
        new("name", TypeRef.String), new("value", TypeRef.String));
    public static NativeBuiltinRecordSchema Process { get; } = new(
        new("pid", TypeRef.Number), new("name", TypeRef.String));
    public static NativeBuiltinRecordSchema RegexMatch { get; } = new(
        new("value", TypeRef.String), new("index", TypeRef.Number), new("length", TypeRef.Number),
        new("groups", new(TypeKind.Range, TypeRef.Scope)));
    public static NativeBuiltinRecordSchema RegexGroup { get; } = new(
        new("name", TypeRef.String), new("value", TypeRef.String), new("index", TypeRef.Number),
        new("length", TypeRef.Number), new("success", TypeRef.Bool));
    public static NativeBuiltinRecordSchema Signature { get; } = new(
        new("parameters", new(TypeKind.Range, TypeRef.Type)), new("arity", TypeRef.Number),
        new("result", TypeRef.Type), new("result_declared", TypeRef.Bool), new("variadic", TypeRef.Bool));
    public static NativeBuiltinRecordSchema Member { get; } = new(
        new("name", TypeRef.String), new("type", TypeRef.Type), new("storage_type", TypeRef.Type), new("admits_null", TypeRef.Bool));

    public static NativeBuiltinRecordSchema Zip(TypeRef first, TypeRef second) =>
        new(new("first", first), new("second", second));
    public static NativeBuiltinRecordSchema Enumerate(TypeRef item) =>
        new(new("index", TypeRef.Number), new("value", item));
}
