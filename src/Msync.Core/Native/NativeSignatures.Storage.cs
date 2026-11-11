namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterStorage(IDictionary<string, NativeSignature> result)
    {
        TypeRef range = new(TypeKind.Range);
        const NativeBehavior memory = NativeBehavior.LocalMemory | NativeBehavior.MayFail;
        result.Add("storage._leaf_callable", new("storage._leaf_callable", [TypeRef.Function],
            TypeRef.Bool, Behavior: NativeBehavior.None));
        void Add(string name, TypeRef output, NativeEffect effect, NativeResultRule rule, params TypeRef[] parameters) =>
            result.Add("storage." + name, new("storage." + name, parameters, output, effect,
                ResultRule: rule, Behavior: memory));
        Add("buffer_new", range, NativeEffect.Write, NativeResultRule.FirstArgument, range);
        result["storage.buffer_new"] = result["storage.buffer_new"] with
        { Behavior = memory | NativeBehavior.Enumerates | NativeBehavior.EagerRange };
        Add("buffer_get", TypeRef.Any, NativeEffect.Read, NativeResultRule.RangeItem, range, TypeRef.Number);
        Add("buffer_set", TypeRef.Null, NativeEffect.Write, NativeResultRule.Fixed, range, TypeRef.Number, TypeRef.Any);
        Add("buffer_push", TypeRef.Number, NativeEffect.Write, NativeResultRule.Fixed, range, TypeRef.Any);
        Add("buffer_pop", TypeRef.Any, NativeEffect.Write, NativeResultRule.RangeItem, range);
        Add("buffer_insert", TypeRef.Null, NativeEffect.Write, NativeResultRule.Fixed, range, TypeRef.Number, TypeRef.Any);
        Add("buffer_remove", TypeRef.Any, NativeEffect.Write, NativeResultRule.RangeItem, range, TypeRef.Number);
        Add("buffer_swap", TypeRef.Null, NativeEffect.Write, NativeResultRule.Fixed, range, TypeRef.Number, TypeRef.Number);
        Add("buffer_size", TypeRef.Number, NativeEffect.Read, NativeResultRule.Fixed, range);
        Add("buffer_clear", TypeRef.Null, NativeEffect.Write, NativeResultRule.Fixed, range);
        Add("buffer_snapshot", range, NativeEffect.Read, NativeResultRule.FirstArgument, range);
        result["storage.buffer_snapshot"] = result["storage.buffer_snapshot"] with
        { Behavior = memory | NativeBehavior.EagerRange };
    }
}
