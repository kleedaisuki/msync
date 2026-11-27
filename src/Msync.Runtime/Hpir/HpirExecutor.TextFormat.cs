using System.Globalization;
using System.Text;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallTextFormat(HpirProgram program, ref HpirHeap? heap, HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc,
        HpirImport import, HpirCall call, byte* frame, int destination, CancellationToken token)
    {
        nint templateAddress = Read<nint>(frame, call.Arguments[0]);
        ReadOnlySpan<char> input = HpirTextStorage.Read(templateAddress);
        string schema = program.TextLiterals[import.Binding];
        bool named = schema[0] == HpirFormatImports.Named;
        var allocation = new TextAllocation(program, ref heap, active, diagnostics, counts, pc);
        if (input.IndexOfAny('{', '}') < 0)
        {
            Write(frame, destination, allocation.Reuse(templateAddress));
            return;
        }

        string template = input.ToString();
        StringBuilder output = new(template.Length);
        var reader = new TextFormat.Reader(template, named);
        int fields = 0;
        try
        {
            while (reader.MoveNext(out var part))
            {
                if (!part.IsField)
                {
                    output.Append(input.Slice(part.Offset, part.Length));
                    continue;
                }
                var (kind, index) = named ? NamedFormatOperand(schema, part.Name(template))
                    : part.Argument < call.Arguments.Count - 1 ? (schema[part.Argument + 1], part.Argument + 1) : default;
                if (index == 0)
                    throw TextFormat.Error(template, part.Offset, named ? $"Object has no member '{part.Name(template).ToString()}'"
                        : $"argument {part.Argument} is missing; supplied {call.Arguments.Count - 1}");
                int argument = call.Arguments[index];
                if (kind == HpirFormatImports.CompletedText)
                {
                    var context = new HpirTextDemandContext(program, heap!, active, pc, diagnostics, counts);
                    nint text = context.Demand(Read<nint>(frame, argument), work, null, token);
                    AppendFormatText(output, template, part, text, text == 0 ? TypeRef.Null : TypeRef.String);
                }
                else AppendFormatOperand(output, template, part, kind, frame, argument);
                ++fields;
            }
            nint result = allocation.Reserve(output.Length, out Span<char> resultText);
            output.CopyTo(0, resultText, output.Length);
            counts?.Formatted(fields, output.Length);
            Write(frame, destination, result);
        }
        catch (MsyncException error) when (error.Span is null)
        {
            throw new MsyncException(error.Message, diagnostics.Source(active->Function, pc), error);
        }
    }

    private static (char Kind, int Index) NamedFormatOperand(string schema, ReadOnlySpan<char> name)
    {
        ReadOnlySpan<char> operands = schema.AsSpan(1);
        for (int index = 1; !operands.IsEmpty; ++index)
        {
            char kind = operands[0];
            int end = operands.IndexOf('\0');
            if (operands[1..end].SequenceEqual(name)) return (kind, index);
            operands = operands[(end + 1)..];
        }
        return default;
    }

    private static void AppendFormatOperand(StringBuilder output, string template, TextFormat.Part part, char kind, byte* frame, int argument)
    {
        TypeRef type = HpirFormatImports.Type(kind)!;
        TextFormat.CheckType(template, part, type);
        switch (kind)
        {
            case HpirFormatImports.Number:
                double number = Read<double>(frame, argument);
                if (part.Format.Presentation != '?') NativeBuiltins.AppendNumber(output, template, part, number);
                else NativeBuiltins.AppendText(output, number.ToString(CultureInfo.InvariantCulture), part.Format);
                return;
            case HpirFormatImports.Bool:
                NativeBuiltins.AppendText(output, Read<byte>(frame, argument) == 0 ? "false" : "true", part.Format);
                return;
            case HpirFormatImports.Null:
                NativeBuiltins.AppendText(output, "null", part.Format);
                return;
            default:
                ReadOnlySpan<char> text = HpirTextStorage.Read(Read<nint>(frame, argument));
                NativeBuiltins.AppendText(output, kind == HpirFormatImports.Text && part.Format.Presentation == '?'
                    ? NativeBuiltins.Quote(text) : text, part.Format);
                return;
        }
    }

    private static void AppendFormatText(StringBuilder output, string template, TextFormat.Part part, nint text, TypeRef type)
    {
        TextFormat.CheckType(template, part, type);
        ReadOnlySpan<char> value = text == 0 ? "null" : HpirTextStorage.Read(text);
        NativeBuiltins.AppendText(output, text != 0 && part.Format.Presentation == '?' ? NativeBuiltins.Quote(value) : value, part.Format);
    }
}
