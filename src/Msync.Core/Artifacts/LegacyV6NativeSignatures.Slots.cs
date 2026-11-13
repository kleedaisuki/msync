namespace Msync.Core.Native;

internal static partial class LegacyV6NativeSignatures
{
    private static void RegisterSlots(IDictionary<string, LegacyV6NativeSignature> result)
    {
        TypeRef owner = new((TypeKind)LegacyV6Reader.LegacyStorageMask);
        void Slots(string operation, TypeRef output, params TypeRef[] parameters) => Add(result, "slots." + operation, output, parameters);
        Slots("new", owner, TypeRef.Type); Slots("allocate", TypeRef.Number, owner, TypeRef.Any);
        Slots("get", TypeRef.Any, owner, TypeRef.Number); Slots("release", TypeRef.Null, owner, TypeRef.Number);
        Slots("count", TypeRef.Number, owner); Slots("reset", TypeRef.Number, owner, TypeRef.Any);
    }
}
