using System.Numerics;
using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private readonly Dictionary<(int Type, bool Nullable), LlvmType> types = [];
        internal LlvmType Type(HirTypeId id, bool admitsNull = false)
        {
            var key = (id.Value, admitsNull);
            if (types.TryGetValue(key, out var type)) return type;
            TypeKind kinds = hir.Types[id.Value].RuntimeType.Kind;
            if (hir.Types[id.Value].Nullable || admitsNull) kinds |= TypeKind.Null;
            type = RepresentationType(kinds);
            types.Add(key, type);
            return type;
        }
        internal LlvmType RepresentationType(TypeKind kinds)
        {
            if ((kinds & ~TypeKind.Any) != 0)
                throw new LlvmHirLoweringException(default, $"internal storage type {kinds}");
            if (BitOperations.PopCount((uint)kinds) > 1) return UnionType(kinds);
            return kinds switch
            {
                TypeKind.Never or TypeKind.Null => NullType,
                TypeKind.Bool => LlvmIntegerType.Get(context, 1),
                TypeKind.Number => context.GetPrimitiveType(LlvmTypeId.DoubleTyID)!,
                TypeKind.Scope or TypeKind.String or TypeKind.Range or TypeKind.Type or
                    TypeKind.Connection or TypeKind.Process => DataPointer,
                TypeKind.Path => PathType(),
                TypeKind.Function => FunctionType(),
                _ => throw new LlvmHirLoweringException(default, $"type {kinds}")
            };
        }
        private LlvmStructType UnionType(TypeKind kinds)
        {
            string name = "msync.union." + ((ushort)kinds).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            if (LlvmStructType.GetTypeByName(context, name) is { } existing) return existing;
            // Payload capacity follows the actual LLVM builtin carriers. Native2
            // has a different compact representation, adapted at target lowering.
            int rawBits = (kinds & TypeKind.Number) != 0 ? 64 : (kinds & TypeKind.Path) != 0 ? 16
                : (kinds & TypeKind.Bool) != 0 ? 8 : 0;
            uint roots = (kinds & TypeKind.Path) != 0 ? 5U : (kinds & TypeKind.Function) != 0 ? 3U
                : (kinds & ~(TypeKind.Null | TypeKind.Bool | TypeKind.Number)) != 0 ? 1U : 0U;
            LlvmType raw = rawBits == 0 ? LlvmArrayType.Get(LlvmIntegerType.Get(context, 8), 0)
                : LlvmIntegerType.Get(context, (uint)rawBits);
            return LlvmStructType.Create(context,
                [LlvmIntegerType.Get(context, 8), raw, LlvmArrayType.Get(DataPointer, roots)], name);
        }
    }
}
