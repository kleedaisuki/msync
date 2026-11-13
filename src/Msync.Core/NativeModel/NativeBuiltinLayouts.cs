namespace Msync.Core.Native;

public readonly record struct NativeBuiltinField(string Name, TypeRef Type);

public sealed class NativeBuiltinRecordSchema
{
    internal NativeBuiltinRecordSchema(params NativeBuiltinField[] fields)
    {
        Fields = Array.AsReadOnly(fields);
        Layout = new(fields.Select(field => new NativeField(field.Name, NativeType.FromTypeRef(field.Type))));
    }

    public IReadOnlyList<NativeBuiltinField> Fields { get; }
    public NativeLayout Layout { get; }
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
        new(ZipFields(first, second).Select(field => new NativeBuiltinField(field.Name, field.Type)).ToArray());
    public static NativeLayout Zip(NativeType first, NativeType second) =>
        new(ZipFields(first, second).Select(field => new NativeField(field.Name, field.Type)));
    public static NativeBuiltinRecordSchema Enumerate(TypeRef item) =>
        new(EnumerateFields(TypeRef.Number, item).Select(field => new NativeBuiltinField(field.Name, field.Type)).ToArray());
    public static NativeLayout Enumerate(NativeType item) =>
        new(EnumerateFields(NativeType.Of(NativeKind.Number), item).Select(field => new NativeField(field.Name, field.Type)));

    private static (string Name, T Type)[] ZipFields<T>(T first, T second) => [("first", first), ("second", second)];
    private static (string Name, T Type)[] EnumerateFields<T>(T index, T value) => [("index", index), ("value", value)];
}
