namespace Msync.Core.Native;

internal static partial class LegacyV6NativeSignatures
{
    private static void RegisterVectorStorage(IDictionary<string, LegacyV6NativeSignature> result)
    {
        TypeRef vector = new((TypeKind)LegacyV6Reader.LegacyStorageMask), range = new(TypeKind.Range);
        void Vector(string operation, TypeRef output, params TypeRef[] parameters) => Add(result, "storage.vector_" + operation, output, parameters);
        Vector("new", vector, range); Vector("empty", vector, TypeRef.Type); Vector("empty_like", vector, vector);
        Vector("get", TypeRef.Any, vector, TypeRef.Number); Vector("set", TypeRef.Null, vector, TypeRef.Number, TypeRef.Any);
        Vector("push", TypeRef.Number, vector, TypeRef.Any); Vector("pop", TypeRef.Any, vector);
        Vector("insert", TypeRef.Null, vector, TypeRef.Number, TypeRef.Any); Vector("remove", TypeRef.Any, vector, TypeRef.Number);
        Vector("size", TypeRef.Number, vector); Vector("clear", TypeRef.Null, vector);
        Vector("snapshot", range, vector); Vector("take", range, vector);
    }
}
