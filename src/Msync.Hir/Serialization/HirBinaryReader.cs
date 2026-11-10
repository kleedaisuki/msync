using System.Buffers;
using System.Collections.Immutable;
using Msync.Core;

namespace Msync.Hir;

internal sealed class HirBinaryReader(BinaryReader reader)
{
    private string section = "header";

    internal string String()
    {
        int length = reader.Read7BitEncodedInt();
        CheckLength(length, 1, "UTF-8 string");
        if (length == 0) return string.Empty;
        byte[]? rented = null;
        Span<byte> bytes = length <= 256 ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            reader.BaseStream.ReadExactly(bytes);
            return HirArtifact.Utf8.GetString(bytes);
        }
        finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented); }
    }

    internal HirModule Module()
    {
        HirFunctionId entry = new(reader.ReadInt32());
        bool session = Boolean();
        HirModule result = new(
            Array("sources", 2, Source),
            Array("types", 31, Type),
            Array("locations", 28, Location),
            Array("functions", 22, Function),
            Array("templates", 25, Template),
            Array("nodes", 32, Node),
            Array("edges", 4, () => new HirNodeId(reader.ReadInt32())),
            Array("literals", 13, Literal),
            Array("scope layouts", 4, () => new HirScopeLayout(Array("fields", 6, Field))),
            Array("signatures", 9, Signature),
            Array("calls", 18, Call),
            Array("closures", 8, () => new HirClosure(new(reader.ReadInt32()), Array("captures", 13, Capture))),
            Array("scope constructions", 20, Scope),
            Array("for loops", 8, () => new HirFor(new(reader.ReadInt32()), new(reader.ReadInt32()))),
            Array("matches", 4, () => new HirMatch(Array("match arms", 14, () => new HirMatchArm(Pattern(), new(reader.ReadInt32()))))),
            Array("barriers", 2, () => new HirBarrier(Boolean(), Boolean())),
            Array("ranges", 4, () => new HirRange(Array("range deferred items", 1, Boolean))),
            Array("function adaptations", 8, () => new HirFunctionAdaptation(Array("adaptation targets", 9, Target)) { SessionSlot = reader.ReadInt32() }),
            entry,
            Array("exports", 5, () => new HirExport(String(), new(reader.ReadInt32()))), session,
            Array("template closures", 8, () => new HirTemplateClosure(new(reader.ReadInt32()), Array("template captures", 13, Capture))),
            Array("concepts", 25, Concept));
        return result with
        {
            UnresolvedImports = Array("unresolved imports", 4, () => new HirLocationId(reader.ReadInt32())),
            SessionDispatches = Array("session dispatches", 16, () => new HirSessionDispatch(reader.ReadInt32(),
                Array("session parameters", 4, () => new HirTypeId(reader.ReadInt32())), new(reader.ReadInt32()),
                Array("session targets", 9, Target)))
        };
    }

    private ImmutableArray<T> Array<T>(string name, int minimumBytes, Func<T> read)
    {
        string previous = section;
        section = name;
        try
        {
            int count = reader.ReadInt32();
            CheckLength(count, minimumBytes, name);
            var result = reader.BaseStream.CanSeek
                ? ImmutableArray.CreateBuilder<T>(count) : ImmutableArray.CreateBuilder<T>();
            for (int index = 0; index < count; index++) result.Add(read());
            return result.Count == result.Capacity ? result.MoveToImmutable() : result.ToImmutable();
        }
        catch (EndOfStreamException error)
        { throw new EndOfStreamException($"Truncated {name} table in Typed HIR.", error); }
        finally { section = previous; }
    }

    private void CheckLength(int count, int minimumBytes, string what)
    {
        if (count < 0) throw new MsyncException($"Negative {what} count in Typed HIR.");
        Stream stream = reader.BaseStream;
        if (stream.CanSeek && count > (stream.Length - stream.Position) / minimumBytes)
            throw new EndOfStreamException($"The {what} count exceeds the remaining Typed HIR bytes.");
    }

    private bool Boolean() => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        byte value => throw new MsyncException($"Invalid boolean tag {value} in Typed HIR {section}.")
    };

    private T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value
        : throw new MsyncException($"Unknown {typeof(T).Name} tag {value} in Typed HIR {section}.");

    private string? Optional() => Boolean() ? String() : null;
    private HirSource Source() => new(String(), Optional());
    private HirSourceRange Range() => new(new(reader.ReadInt32()), reader.ReadInt32(), reader.ReadInt32());

    private TypeRef RuntimeType()
    {
        int count = reader.ReadInt32();
        CheckLength(count, 2, "runtime type components");
        if (count == 0) throw new MsyncException($"A Typed HIR runtime type in {section} has no root kind.");
        TypeKind[] kinds = new TypeKind[count];
        for (int index = 0; index < count; index++)
        {
            TypeKind kind = (TypeKind)reader.ReadUInt16();
            if ((kind & ~TypeKind.Any) != 0) throw new MsyncException($"Unknown runtime type bits {(ushort)kind} in Typed HIR {section}.");
            kinds[index] = kind;
        }
        TypeRef? result = null;
        for (int index = count - 1; index >= 0; index--) result = new(kinds[index], result);
        return result!;
    }

    private HirType Type() => new(RuntimeType(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
        Boolean(), Array("type alternatives", 4, () => new HirTypeId(reader.ReadInt32())));
    private HirLocation Location()
    {
        string name = String();
        HirTypeId type = new(reader.ReadInt32());
        HirSourceRange source = Range();
        HirFunctionId owner = new(reader.ReadInt32());
        HirLocationKind kind = Defined((HirLocationKind)reader.ReadByte());
        bool mutable = Boolean(), nullable = Boolean();
        int template = reader.ReadInt32();
        return new(name, type, source, owner, kind, mutable, nullable, template == -1 ? null : new HirTemplateId(template));
    }
    private HirCapture Capture() => new(new(reader.ReadInt32()), Defined((HirCaptureMode)reader.ReadByte()),
        new(reader.ReadInt32()), new(reader.ReadInt32()));

    private HirFunction Function()
    {
        string name = String();
        var parameters = Array("function parameters", 4, () => new HirLocationId(reader.ReadInt32()));
        var captures = Array("function captures", 13, Capture);
        HirTypeId result = new(reader.ReadInt32());
        HirNodeId body = new(reader.ReadInt32());
        int template = reader.ReadInt32();
        return new(name, parameters, captures, result, body, template == -1 ? null : new HirTemplateId(template), Boolean());
    }

    private HirTemplate Template()
    {
        string name = String();
        var names = Array("type parameters", 1, String);
        var parameters = Array("template parameters", 4, () => new HirLocationId(reader.ReadInt32()));
        HirNodeId body = new(reader.ReadInt32());
        HirSourceRange source = Range();
        var captures = Array("template captures", 13, Capture);
        int result = reader.ReadInt32();
        var types = Array("type parameter types", 4, () => new HirTypeId(reader.ReadInt32()));
        return new(name, names, parameters, body, source, captures, result == -1 ? null : new HirTypeId(result), types);
    }

    private HirNode Node()
    {
        HirKind kind = Defined((HirKind)reader.ReadByte());
        HirTypeId type = new(reader.ReadInt32());
        HirSourceRange source = Range();
        HirEdgeRange children = new(reader.ReadInt32(), reader.ReadInt32());
        int data = reader.ReadInt32();
        string? text = Optional();
        HirEffects effects = (HirEffects)reader.ReadUInt16();
        const HirEffects allowed = HirEffects.ReadLocation | HirEffects.WriteLocation | HirEffects.ReadMemory | HirEffects.WriteMemory
            | HirEffects.ReadExternal | HirEffects.WriteExternal | HirEffects.Invoke | HirEffects.Enumerate
            | HirEffects.MayFail | HirEffects.Synchronize | HirEffects.Spawn;
        if ((effects & ~allowed) != 0) throw new MsyncException($"Unknown effect bits {(ushort)effects} in Typed HIR nodes.");
        return new(kind, type, source, children, data, text, effects);
    }

    private HirLiteral Literal()
    {
        HirLiteralKind kind = Defined((HirLiteralKind)reader.ReadByte());
        string? text = Optional();
        double number = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        bool boolean = Boolean();
        TypeRef? type = Boolean() ? RuntimeType() : null;
        ConnectionKind connection = Defined((ConnectionKind)reader.ReadByte());
        return new(kind, text, number, boolean, type, connection);
    }

    private HirField Field() => new(String(), new(reader.ReadInt32()), Boolean());
    private HirSignature Signature() => new(Array("signature parameters", 4, () => new HirTypeId(reader.ReadInt32())),
        new(reader.ReadInt32()), Boolean());
    private HirCall Call() => new(Defined((HirCallKind)reader.ReadByte()), new(reader.ReadInt32()), Optional(),
        Array("dispatch targets", 9, Target), reader.ReadInt32())
    { SessionSlot = reader.ReadInt32() };
    private HirDispatchTarget Target() => new(new(reader.ReadInt32(), Boolean()), new(reader.ReadInt32()));
    private HirScopeConstruction Scope()
    {
        int layout = reader.ReadInt32();
        var fields = Array("scope fields", 4, () => new HirLocationId(reader.ReadInt32()));
        int singleton = reader.ReadInt32();
        int self = reader.ReadInt32();
        return new(layout, fields, singleton, self == -1 ? null : new HirLocationId(self), new(reader.ReadInt32()));
    }
    private HirPattern Pattern()
    {
        HirPatternKind kind = Defined((HirPatternKind)reader.ReadByte());
        int data = reader.ReadInt32();
        string? text = Optional();
        int binding = reader.ReadInt32();
        return new(kind, data, text, binding == -1 ? null : new HirLocationId(binding));
    }

    private HirConcept Concept()
    {
        string name = String();
        HirSourceRange source = Range();
        var bases = Array("concept bases", 4, reader.ReadInt32);
        var parameters = Array("concept type parameters", 4, () => new HirTypeId(reader.ReadInt32()));
        var members = Array("concept members", 22, () => new HirConceptMember(String(), new(reader.ReadInt32()),
            Range(), reader.ReadInt32(), Boolean()));
        return new(name, source, bases, parameters, members);
    }
}
