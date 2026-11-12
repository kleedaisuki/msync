namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterArena(IDictionary<string, NativeSignature> result)
    {
        TypeRef owner = new(TypeKind.Storage);
        TypeRef index = TypeRef.Number;
        void Add(string name, ArenaOperation operation, NativeEffect effect, TypeRef output, params TypeRef[] parameters) =>
            result.Add("arena." + name, new("arena." + name, parameters, output, effect,
                Behavior: NativeBehavior.LocalMemory | NativeBehavior.MayFail) { Arena = operation });

        Add("new", ArenaOperation.New, NativeEffect.Write, owner, TypeRef.Type);
        Add("root", ArenaOperation.Root, NativeEffect.Read, index, owner);
        Add("size", ArenaOperation.Size, NativeEffect.Read, index, owner);
        Add("key", ArenaOperation.Key, NativeEffect.Read, TypeRef.Any, owner, index);
        Add("child", ArenaOperation.Child, NativeEffect.Read, index, owner, index, TypeRef.Bool);
        Add("parent", ArenaOperation.Parent, NativeEffect.Read, index, owner, index);
        Add("red", ArenaOperation.Red, NativeEffect.Read, TypeRef.Bool, owner, index);
        Add("allocate", ArenaOperation.Allocate, NativeEffect.Write, index, owner, TypeRef.Any, index);
        Add("release", ArenaOperation.Release, NativeEffect.Write, TypeRef.Null, owner, index);
        Add("clear", ArenaOperation.Clear, NativeEffect.Write, TypeRef.Null, owner);
        Add("set_root", ArenaOperation.SetRoot, NativeEffect.Write, TypeRef.Null, owner, index);
        Add("set_child", ArenaOperation.SetChild, NativeEffect.Write, TypeRef.Null, owner, index, TypeRef.Bool, index);
        Add("set_parent", ArenaOperation.SetParent, NativeEffect.Write, TypeRef.Null, owner, index, index);
        Add("set_red", ArenaOperation.SetRed, NativeEffect.Write, TypeRef.Null, owner, index, TypeRef.Bool);
    }
}
