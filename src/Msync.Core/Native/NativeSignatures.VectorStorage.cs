namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterVectorStorage(IDictionary<string, NativeSignature> result)
    {
        TypeRef vector = TypeRef.Storage;
        TypeRef range = new(TypeKind.Range);
        const NativeBehavior memory = NativeBehavior.LocalMemory | NativeBehavior.MayFail;
        void Add(string name, TypeRef output, NativeEffect effect, NativeBehavior behavior, params TypeRef[] parameters) =>
            result.Add("storage.vector_" + name, new("storage.vector_" + name, parameters, output, effect, Behavior: behavior));

        Add("new", vector, NativeEffect.Write, memory | NativeBehavior.Enumerates | NativeBehavior.EagerRange, range);
        Add("empty", vector, NativeEffect.Write, memory, TypeRef.Type);
        Add("empty_like", vector, NativeEffect.Write, memory, vector);
        Add("get", TypeRef.Any, NativeEffect.Read, memory, vector, TypeRef.Number);
        Add("set", TypeRef.Null, NativeEffect.Write, memory, vector, TypeRef.Number, TypeRef.Any);
        Add("push", TypeRef.Number, NativeEffect.Write, memory, vector, TypeRef.Any);
        Add("pop", TypeRef.Any, NativeEffect.Write, memory, vector);
        Add("insert", TypeRef.Null, NativeEffect.Write, memory, vector, TypeRef.Number, TypeRef.Any);
        Add("remove", TypeRef.Any, NativeEffect.Write, memory, vector, TypeRef.Number);
        Add("size", TypeRef.Number, NativeEffect.Read, memory, vector);
        Add("clear", TypeRef.Null, NativeEffect.Write, memory, vector);
        Add("snapshot", range, NativeEffect.Read, memory | NativeBehavior.EagerRange, vector);
        Add("take", range, NativeEffect.Write, memory | NativeBehavior.EagerRange, vector);
        foreach (var name in new[] { "storage.vector_get", "storage.vector_pop", "storage.vector_remove" })
            result[name] = result[name] with { ResultRule = NativeResultRule.RangeItem };
    }
}
