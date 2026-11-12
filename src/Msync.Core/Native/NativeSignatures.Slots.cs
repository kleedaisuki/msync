namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterSlots(IDictionary<string, NativeSignature> result)
    {
        TypeRef owner = TypeRef.Storage;
        void Add(string operation, NativeEffect effect, TypeRef output, params TypeRef[] inputs)
        {
            string name = "slots." + operation;
            result.Add(name, new(name, inputs, output, effect,
                Behavior: NativeBehavior.LocalMemory | NativeBehavior.MayFail));
        }

        Add("new", NativeEffect.Write, owner, TypeRef.Type);
        Add("allocate", NativeEffect.Write, TypeRef.Number, owner, TypeRef.Any);
        Add("get", NativeEffect.Read, TypeRef.Any, owner, TypeRef.Number);
        Add("release", NativeEffect.Write, TypeRef.Null, owner, TypeRef.Number);
        Add("count", NativeEffect.Read, TypeRef.Number, owner);
        Add("reset", NativeEffect.Write, TypeRef.Number, owner, TypeRef.Any);
    }
}
