using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    public static string ToJson(NativeProgram program)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter output = new(stream, new JsonWriterOptions { Indented = true, MaxDepth = int.MaxValue }))
        {
            output.WriteStartObject();
            output.WriteString("representation", NativeProgram.Representation);
            output.WriteNumber("formatVersion", FormatVersion); output.WriteString("languageVersion", LanguageVersion);
            output.WriteString("stage", program.Stage.ToString()); output.WriteString("semantics", program.Semantics.ToString());
            output.WriteNumber("optimizationLevel", program.OptimizationLevel); output.WriteNumber("entryPoint", program.EntryPoint);
            if (program.Context is { } context)
            {
                output.WriteStartObject("context"); output.WriteString("workingDirectory", context.WorkingDirectory);
                output.WriteString("homeDirectory", context.HomeDirectory); output.WriteString("sourceFile", context.SourceFile); output.WriteEndObject();
            }
            output.WriteStartArray("types");
            for (int index = 0; index < program.Types.Count; index++)
            {
                NativeCodeType type = program.Types[index];
                output.WriteStartObject(); output.WriteNumber("id", index); output.WriteString("kind", type.Kind.ToString());
                if (type.Machine is { } machine)
                {
                    output.WriteString("machineClass", machine.Class.ToString());
                    output.WriteNumber("machineBits", machine.BitWidth);
                    output.WriteNumber("machineAlignment", machine.Alignment);
                    if (machine.IsFloating) output.WriteString("machineFormat", machine.FloatFormat.ToString());
                }
                output.WriteNumber("element", type.Element); output.WriteNumber("pointee", type.Pointee);
                WriteJsonInts(output, "layouts", type.Layouts ?? []); output.WriteEndObject();
            }
            output.WriteEndArray(); output.WriteStartArray("layouts");
            for (int index = 0; index < program.Layouts.Count; index++)
            {
                output.WriteStartObject(); output.WriteNumber("id", index); output.WriteStartArray("fields");
                foreach (NativeCodeField field in program.Layouts[index].Fields)
                { output.WriteStartObject(); output.WriteString("name", field.Name); output.WriteNumber("type", field.Type); output.WriteEndObject(); }
                output.WriteEndArray(); output.WriteEndObject();
            }
            output.WriteEndArray(); output.WriteStartArray("projections");
            foreach (NativeProjection projection in program.Projections)
            {
                output.WriteStartArray();
                foreach (NativeProjectionCase item in projection.Cases)
                { output.WriteStartObject(); output.WriteNumber("layout", item.Layout); output.WriteNumber("field", item.Field); output.WriteEndObject(); }
                output.WriteEndArray();
            }
            output.WriteEndArray(); output.WriteStartArray("statics");
            foreach (NativeStatic item in program.Statics)
            { output.WriteStartObject(); output.WriteNumber("layout", item.Layout); output.WriteNumber("declaration", item.Declaration); output.WriteEndObject(); }
            output.WriteEndArray(); output.WriteStartArray("constants");
            foreach (NativeConstant constant in program.Constants) WriteJsonConstant(output, constant);
            output.WriteEndArray(); output.WriteStartArray("functions");
            for (int index = 0; index < program.Functions.Count; index++) WriteJsonFunction(output, index, program.Functions[index]);
            output.WriteEndArray(); output.WriteStartArray("allocations");
            foreach (NativeAllocationPlan plan in program.Allocations)
            {
                output.WriteStartObject(); output.WriteNumber("function", plan.Function); output.WriteNumber("instruction", plan.Instruction);
                output.WriteNumber("layout", plan.Layout); output.WriteString("placement", plan.Placement.ToString()); output.WriteEndObject();
            }
            output.WriteEndArray(); WriteJsonPlans(output, program.ExecutionPlans ?? []);
            if (program.Backend is { } backend) WriteJsonBackend(output, backend);
            output.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void WriteJsonBackend(Utf8JsonWriter output, NativeBackendCode backend)
    {
        output.WriteStartArray("backend");
        for (int index = 0; index < backend.Functions.Count; index++)
        {
            var body = backend.Functions[index];
            output.WriteStartObject();
            output.WriteNumber("function", index);
            output.WriteStartArray("instructions");
            for (int pc = 0; pc < body.Instructions.Count;)
            {
                var instruction = body.Instructions[pc];
                output.WriteStartObject();
                output.WriteNumber("pc", pc);
                output.WriteNumber("span", instruction.Span);
                output.WriteString("op", instruction.Op.ToString());
                output.WriteString("handler", instruction.Handler.ToString());
                output.WriteNumber("result", instruction.Result);
                output.WriteNumber("a", instruction.A);
                output.WriteNumber("b", instruction.B);
                output.WriteNumber("c", instruction.C);
                output.WriteNumber("operand", instruction.Operand);
                output.WriteEndObject();
                pc += Math.Max(instruction.Span, 1);
            }
            output.WriteEndArray();
            output.WriteEndObject();
        }
        output.WriteEndArray();
    }

    public static string Disassemble(NativeProgram program)
    {
        StringBuilder text = new();
        text.Append(NativeProgram.Representation).Append(' ').Append(program.Stage).Append(" entry=").Append(program.EntryPoint).AppendLine();
        for (int index = 0; index < program.Layouts.Count; index++)
        {
            text.Append("layout ").Append(index).Append(" { ");
            text.AppendJoin(", ", program.Layouts[index].Fields.Select(field => $"{field.Name}:t{field.Type}"));
            text.AppendLine(" }");
        }
        for (int index = 0; index < program.Functions.Count; index++)
        {
            NativeFunctionCode function = program.Functions[index];
            text.Append("fn ").Append(index).Append(' ').Append(function.Name).Append(" -> t").Append(function.Signature.Result).AppendLine();
            for (int pc = 0; pc < function.Instructions.Count; pc++)
            {
                NativeInstruction instruction = function.Instructions[pc];
                text.Append("  ").Append(pc.ToString("D4", CultureInfo.InvariantCulture)).Append(": ");
                if (instruction.Result >= 0) text.Append('%').Append(instruction.Result).Append(" = ");
                text.Append(instruction.Op).Append(" a=").Append(instruction.A).Append(" b=").Append(instruction.B).Append(" c=").Append(instruction.C);
                if (instruction.Type >= 0) text.Append(" type=t").Append(instruction.Type);
                if (instruction.Immediate != 0) text.Append(" immediate=").Append(instruction.Immediate);
                if (instruction.Symbol is not null) text.Append(" symbol=\"").Append(JsonEncodedText.Encode(instruction.Symbol).ToString()).Append('"');
                if (instruction.Inputs is { } inputs) text.Append(" inputs=[").AppendJoin(",", inputs).Append(']');
                if (instruction.Source is { } origin) text.Append(" @").Append(origin.Source).Append(':').Append(origin.Start).Append('+').Append(origin.Length);
                text.AppendLine();
            }
        }
        return text.ToString();
    }

    private static void WriteJsonFunction(Utf8JsonWriter output, int index, NativeFunctionCode function)
    {
        output.WriteStartObject(); output.WriteNumber("id", index); output.WriteString("name", function.Name);
        output.WriteNumber("parameterCount", function.ParameterCount); output.WriteNumber("captureCount", function.CaptureCount);
        output.WriteNumber("iteratorCount", function.IteratorCount); output.WriteBoolean("isTemplate", function.IsTemplate);
        output.WriteStartObject("signature"); WriteJsonInts(output, "parameters", function.Signature.Parameters);
        output.WriteNumber("result", function.Signature.Result); output.WriteStartArray("captures");
        foreach (NativeCaptureContract capture in function.Signature.Captures)
        { output.WriteStartObject(); output.WriteNumber("type", capture.Type); output.WriteString("mode", capture.Mode.ToString()); output.WriteEndObject(); }
        output.WriteEndArray(); output.WriteEndObject();
        if (function.ReflectionSignature is { } reflection)
        {
            output.WriteStartObject("reflectionSignature");
            output.WriteStartArray("parameters");
            foreach (TypeRef parameter in reflection.Parameters) WriteJsonTypeValue(output, parameter);
            output.WriteEndArray(); WriteJsonType(output, "result", reflection.Result);
            output.WriteEndObject();
        }
        output.WriteStartArray("slots");
        foreach (NativeSlot slot in function.Slots)
        {
            output.WriteStartObject(); output.WriteNumber("type", slot.Type); output.WriteString("role", slot.Role.ToString());
            output.WriteBoolean("escapes", slot.Escapes); output.WriteString("name", slot.Name); output.WriteEndObject();
        }
        output.WriteEndArray(); output.WriteStartArray("instructions");
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            output.WriteStartObject(); output.WriteNumber("pc", pc); output.WriteString("op", instruction.Op.ToString());
            output.WriteNumber("result", instruction.Result); output.WriteNumber("a", instruction.A); output.WriteNumber("b", instruction.B);
            output.WriteNumber("c", instruction.C); output.WriteNumber("type", instruction.Type); output.WriteNumber("immediate", instruction.Immediate);
            output.WriteString("symbol", instruction.Symbol); WriteJsonInts(output, "inputs", instruction.Inputs ?? []);
            WriteJsonSpan(output, instruction.Source); output.WriteEndObject();
        }
        output.WriteEndArray(); output.WriteEndObject();
    }

    private static void WriteJsonConstant(Utf8JsonWriter output, NativeConstant constant)
    {
        output.WriteStartObject(); output.WriteString("kind", constant.Kind.ToString()); output.WriteString("text", constant.Text);
        if (constant.Text is { } text && InvalidUtf16(text))
            output.WriteString("textCodeUnits", string.Join(" ", text.Select(character => ((ushort)character).ToString("X4", CultureInfo.InvariantCulture))));
        output.WriteString("numberBits", BitConverter.DoubleToUInt64Bits(constant.Number).ToString("X16", CultureInfo.InvariantCulture));
        output.WriteBoolean("boolean", constant.Boolean); output.WriteString("connectionKind", constant.ConnectionKind.ToString());
        output.WriteNumber("originId", constant.OriginId); output.WriteNumber("originOffset", constant.OriginOffset);
        if (constant.Type is { } type) WriteJsonType(output, "valueType", type);
        if (constant.Path is { } path) { output.WritePropertyName("capturedPath"); WriteJsonPath(output, path); }
        output.WriteEndObject();
    }

    private static bool InvalidUtf16(string value)
    {
        for (int index = 0; index < value.Length; index++)
            if (char.IsSurrogate(value[index]) && (!char.IsHighSurrogate(value[index]) || ++index >= value.Length || !char.IsLowSurrogate(value[index])))
                return true;
        return false;
    }

    private static void WriteJsonType(Utf8JsonWriter output, string name, TypeRef type)
    {
        output.WritePropertyName(name);
        WriteJsonTypeValue(output, type);
    }

    private static void WriteJsonTypeValue(Utf8JsonWriter output, TypeRef type)
    {
        output.WriteStartArray();
        for (TypeRef? current = type; current is not null; current = current.Element) output.WriteStringValue(current.Kind.ToString());
        output.WriteEndArray();
    }

    private static void WriteJsonSpan(Utf8JsonWriter output, SourceSpan? span)
    {
        if (span is not { } source) return;
        output.WriteStartObject("source"); output.WriteString("file", source.Source);
        output.WriteNumber("start", source.Start); output.WriteNumber("length", source.Length); output.WriteEndObject();
    }

    private static void WriteJsonInts(Utf8JsonWriter output, string name, IReadOnlyList<int> values)
    { output.WriteStartArray(name); foreach (int value in values) output.WriteNumberValue(value); output.WriteEndArray(); }

    private static void WriteJsonPlans(Utf8JsonWriter output, IReadOnlyList<NativeExecutionPlan> plans)
    {
        output.WriteStartArray("executionPlans");
        foreach (NativeExecutionPlan plan in plans)
        {
            output.WriteStartObject(); output.WriteNumber("function", plan.Function); output.WriteNumber("start", plan.Start);
            output.WriteNumber("end", plan.End); output.WriteNumber("eliminatedTransfers", plan.EliminatedTransfers);
            WriteJsonInts(output, "resultTransfers", plan.ResultTransfers ?? []); output.WriteStartArray("transfers");
            foreach (NativePlannedTransfer transfer in plan.Transfers)
            {
                output.WriteStartObject(); output.WriteNumber("id", transfer.Id); output.WriteString("kind", transfer.Kind.ToString());
                output.WriteString("mode", transfer.Mode.ToString()); output.WritePropertyName("input"); WriteJsonPlanValue(output, transfer.Source);
                output.WritePropertyName("destination"); WriteJsonPath(output, transfer.Destination);
                WriteJsonResources(output, "reads", transfer.Reads); WriteJsonResources(output, "writes", transfer.Writes);
                WriteJsonInts(output, "predecessors", transfer.Predecessors); WriteJsonSpan(output, transfer.Span); output.WriteEndObject();
            }
            output.WriteEndArray(); output.WriteStartArray("batches");
            foreach (ExecutionBatch batch in plan.Batches)
            { output.WriteStartObject(); output.WriteString("endpoint", batch.Endpoint); WriteJsonInts(output, "transfers", batch.Transfers); output.WriteEndObject(); }
            output.WriteEndArray(); output.WriteEndObject();
        }
        output.WriteEndArray();
    }

    private static void WriteJsonPlanValue(Utf8JsonWriter output, NativePlanValue root)
    {
        Stack<NativePlanValue?> pending = new();
        pending.Push(root);
        while (pending.TryPop(out NativePlanValue? value))
        {
            if (value is null) { output.WriteEndArray(); output.WriteEndObject(); continue; }
            if (value.Kind == NativeKind.Path) { WriteJsonPath(output, value.Path!.Value); continue; }
            if (value.Kind != NativeKind.Range) { WriteJsonConstant(output, value.Literal!); continue; }
            output.WriteStartObject(); output.WriteString("kind", "Range"); WriteJsonType(output, "itemType", value.ItemType!);
            output.WriteStartArray("items"); pending.Push(null);
            for (int index = value.Items!.Count - 1; index >= 0; index--) pending.Push(value.Items[index]);
        }
    }

    private static void WriteJsonPath(Utf8JsonWriter output, NativePath path)
    {
        output.WriteStartObject(); output.WriteString("kind", "Path"); output.WriteString("raw", path.Raw);
        output.WriteString("workingDirectory", path.WorkingDirectory); output.WriteString("homeDirectory", path.HomeDirectory);
        output.WriteString("localSpelling", path.LocalSpelling);
        if (path.Connection is { } connection)
        {
            output.WriteStartObject("connection"); output.WriteString("kind", connection.Kind.ToString());
            output.WriteString("address", connection.Address); output.WriteEndObject();
        }
        output.WriteEndObject();
    }

    private static void WriteJsonResources(Utf8JsonWriter output, string name, IReadOnlyList<PlanResource> resources)
    {
        output.WriteStartArray(name);
        foreach (PlanResource resource in resources)
        {
            output.WriteStartObject(); output.WriteString("endpoint", resource.Endpoint); output.WriteString("path", resource.Path);
            output.WriteBoolean("subtree", resource.Subtree); output.WriteEndObject();
        }
        output.WriteEndArray();
    }
}
