using System.Text;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal bool TextFormatCall(LlvmCallInstruction call, out bool named)
        {
            string? target = call.CalledFunction is { } declaration ? graph.NativeCalls.GetValueOrDefault(declaration) : null;
            named = target == "text.format_fields";
            return target is "text.format" or "text.format_fields";
        }

        internal int TextFormatImport(string schema, int fields)
        {
            _ = TextLayout();
            return FutureImport(HpirFormatImports.Create(TextLiteral(schema), fields));
        }

        internal readonly record struct FormatField(string Name, char Kind, int Offset);

        internal FormatField[]? FormatFields(LlvmCallInstruction call, string template)
        {
            int id = call.GetOperandUse(1).LogicalType;
            if (id < 0) id = call.GetArgument(1).LogicalType;
            if (id < 0 || graph.Types[id].Layouts is not { Count: 1 } layouts
                || !structs.TryGetValue(layouts[0], out var record)) return null;
            var fields = graph.Layouts[layouts[0]].Fields;
            var selected = new Dictionary<string, FormatField>(StringComparer.Ordinal);
            var reader = new TextFormat.Reader(template, named: true);
            while (reader.MoveNext(out var part))
            {
                if (!part.IsField) continue;
                string name = part.Name(template).ToString();
                if (selected.ContainsKey(name)) continue;
                int ordinal = 0;
                while (ordinal < fields.Count && fields[ordinal].Name != name) ++ordinal;
                if (ordinal == fields.Count) return null;
                char kind = FormatKind(graph.Types[fields[ordinal].Type].Kind);
                if (SelectedTextField(layouts[0], ordinal)) kind = HpirFormatImports.CompletedText;
                if (graph.FutureFields.Contains(new(layouts[0], ordinal))
                    || kind is not (HpirFormatImports.Text or HpirFormatImports.CompletedText or HpirFormatImports.Number
                        or HpirFormatImports.Bool or HpirFormatImports.Null))
                    return null;
                int offset = FieldOffset(record, (uint)ordinal);
                if (kind == HpirFormatImports.CompletedText) offset += TextFieldPayload(layouts[0], ordinal);
                selected.Add(name, new(name, kind, offset));
            }
            return selected.Values.ToArray();
        }

        internal static char FormatKind(TypeKind kind) => kind switch
        {
            TypeKind.String => HpirFormatImports.Text,
            TypeKind.Number => HpirFormatImports.Number,
            TypeKind.Bool => HpirFormatImports.Bool,
            TypeKind.Null => HpirFormatImports.Null,
            TypeKind.Range => HpirFormatImports.RangeDisplay,
            TypeKind.Process => HpirFormatImports.ProcessDisplay,
            _ => '\0'
        };
    }

    private sealed partial class Body
    {
        private int formatSites, namedFormatSites, formatTextDemands;

        private bool FormatObservesText(LlvmCallInstruction call, LlvmValue input)
        {
            if (!geometry.TextFormatCall(call, out bool named)) return false;
            if (!named) return call.ArgumentCount > 1 && call.Arguments.Contains(input);
            return call.ArgumentCount == 2 && call.GetArgument(0) == input;
        }

        private bool TryTextFormat(LlvmCallInstruction call)
        {
            if (!geometry.TextFormatCall(call, out bool named) || call.ArgumentCount == 0) return false;
            var template = call.GetArgument(0);
            if (TextCell(template) && !named && call.ArgumentCount == 1) return false;
            var operands = new List<int> { FormatTextOperand(call, template) };
            var schema = new StringBuilder().Append(named ? HpirFormatImports.Named : HpirFormatImports.Positional);
            if (named)
            {
                if (call.ArgumentCount != 2 || !geometry.TryContextualLiteral(template, out var literal)
                    || literal.Kind != NativeKind.String || geometry.FormatFields(call, literal.Text ?? "") is not { } fields)
                    return false;
                int owner = Location(call.GetArgument(1));
                foreach (var field in fields)
                {
                    schema.Append(field.Kind).Append(field.Name).Append('\0');
                    operands.Add(field.Kind == HpirFormatImports.Null ? FormatImmediate(call, 0)
                        : Indirect(owner, field.Offset, call));
                }
                ++namedFormatSites;
            }
            else for (int index = 1; index < call.ArgumentCount; ++index)
            {
                var value = call.GetArgument(index);
                var type = geometry.ReflectedType(call, index);
                if (type is null) return false;
                char kind = Geometry.FormatKind(type.Kind);
                int operand = FormatOperand(call, value, type, kind);
                if (operand < 0) return false;
                schema.Append(kind);
                operands.Add(operand);
            }
            // A Bool lives in a byte; each variadic ABI operand is one full word.
            string shape = schema.ToString();
            if (named)
            {
                for (int index = 1, offset = 1; index < operands.Count; ++index)
                {
                    if (shape[offset] == HpirFormatImports.Bool) operands[index] = FormatBool(call, operands[index]);
                    offset = shape.IndexOf('\0', offset) + 1;
                }
            }
            EmitImportCall(call, geometry.TextFormatImport(shape, operands.Count - 1), operands.ToArray());
            ++formatSites;
            return true;
        }

        private int FormatOperand(LlvmCallInstruction call, LlvmValue value, TypeRef type, char kind)
        {
            switch (kind)
            {
                case HpirFormatImports.Text: return FormatTextOperand(call, value);
                case HpirFormatImports.Number:
                    if (!FutureCell(value)) return Location(value);
                    int payload = Reserve(8, 8);
                    Demand(call, value, payload);
                    return payload;
                case HpirFormatImports.Bool:
                    if (!FutureCell(value)) return FormatBool(call, Location(value));
                    int boolean = Reserve(8, 8);
                    Add(new(HpirOp.ConstU64, boolean), call.Source);
                    Demand(call, value, boolean);
                    return boolean;
                case HpirFormatImports.Null: return FormatImmediate(call, 0);
                case HpirFormatImports.RangeDisplay:
                    return type.Element is not null && !FutureRoot(value) ? FormatDisplay(call, type.ToString()) : -1;
                case HpirFormatImports.ProcessDisplay:
                    return value is LlvmCallInstruction { CalledFunction: { } target }
                        && geometry.ProcessStart(target, out int scheduling) && (scheduling & 1) != 0
                        ? FormatDisplay(call, "Process(deferred)") : -1;
                default: return -1;
            }
        }

        private int FormatTextOperand(LlvmInstruction site, LlvmValue value)
        {
            if (!TextCell(value)) return Location(value);
            ++formatTextDemands;
            return ObserveText(site, value);
        }

        private int FormatImmediate(LlvmInstruction site, long value)
        {
            int operand = Reserve(8, 8);
            Add(new(HpirOp.ConstU64, operand, Immediate: value), site.Source);
            return operand;
        }

        private int FormatBool(LlvmInstruction site, int byteLocation)
        {
            int operand = FormatImmediate(site, 0);
            Copy(HpirWidth.U8, operand, byteLocation, site.Source);
            return operand;
        }

        private int FormatDisplay(LlvmInstruction site, string text)
        {
            int operand = Reserve(8, 8);
            Add(new(HpirOp.AddressOfStatic, operand, Immediate: geometry.TextLiteral(text)), site.Source);
            return operand;
        }

        private void ReportTextFormats(CompilationTrace? trace)
        {
            trace?.Metric("range.pack.constant_sizes", fixedPackSizes);
            trace?.Metric("strings.format.sites", formatSites);
            trace?.Metric("strings.format.named", namedFormatSites);
            trace?.Metric("strings.format.text_demands", formatTextDemands);
            trace?.Metric("strings.concat.completions", textConcatCompletions);
            trace?.Metric("text.completion_field_stores", textFieldStores);
            trace?.Metric("text.completion_field_ready_wraps", textFieldWraps);
        }
    }
}
