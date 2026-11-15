namespace Msync.Core.Native;

internal static partial class LegacyV6NativeSignatures
{
    private static void RegisterArena(IDictionary<string, LegacyV6NativeSignature> result)
    {
        TypeRef owner = new((TypeKind)(ushort)TypeKind.Storage), index = TypeRef.Number;
        void Arena(string operation, TypeRef output, params TypeRef[] parameters) => Add(result, "arena." + operation, output, parameters);
        Arena("new", owner, TypeRef.Type); Arena("root", index, owner); Arena("size", index, owner);
        Arena("key", TypeRef.Any, owner, index); Arena("child", index, owner, index, TypeRef.Bool);
        Arena("parent", index, owner, index); Arena("red", TypeRef.Bool, owner, index);
        Arena("allocate", index, owner, TypeRef.Any, index); Arena("release", TypeRef.Null, owner, index);
        Arena("clear", TypeRef.Null, owner); Arena("set_root", TypeRef.Null, owner, index);
        Arena("set_child", TypeRef.Null, owner, index, TypeRef.Bool, index);
        Arena("set_parent", TypeRef.Null, owner, index, index); Arena("set_red", TypeRef.Null, owner, index, TypeRef.Bool);
    }
}
