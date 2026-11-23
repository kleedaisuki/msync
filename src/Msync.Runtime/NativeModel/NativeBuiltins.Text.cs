using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static void AppendText(StringBuilder output, ReadOnlySpan<char> text, TextFormat.Spec spec)
    {
        if (spec.Width == 0 && spec.Precision < 0)
        {
            output.Append(text);
            return;
        }

        int length = 0;
        int scalars = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (scalars == spec.Precision) break;
            length += rune.Utf16SequenceLength;
            scalars++;
        }
        AppendPadded(output, text[..length], spec, Math.Max(0, spec.Width - scalars), '<');
    }

    internal static void AppendNumber(StringBuilder output, string template, TextFormat.Part part, double value)
    {
        TextFormat.CheckNumber(template, part, value);
        var spec = part.Format;
        char sign = !double.IsNaN(value) && double.IsNegative(value) ? '-'
            : spec.Sign is '+' or ' ' ? spec.Sign : '\0';
        double magnitude = Math.Abs(value);
        Span<char> digits = stackalloc char[128];
        if (spec.Integral)
        {
            if (magnitude < 18446744073709551616.0)
            {
                var integer = (ulong)magnitude;
                if (spec.Presentation == 'o')
                {
                    int start = digits.Length;
                    do
                    {
                        digits[--start] = (char)('0' + (integer & 7));
                        integer >>= 3;
                    } while (integer != 0);
                    AppendNumeric(output, digits[start..], spec, sign, true);
                    return;
                }
                Span<char> radixFormat = stackalloc char[1] { spec.Presentation };
                _ = integer.TryFormat(digits, out int written, radixFormat, Invariant);
                AppendNumeric(output, digits[..written], spec, sign, true);
                return;
            }
            AppendNumeric(output, LargeInteger(magnitude, spec.Presentation), spec, sign, true);
            return;
        }

        Span<char> format = stackalloc char[11];
        int length = 0;
        if (spec.Presentation != '\0' || spec.Precision >= 0)
        {
            format[0] = spec.Presentation == '\0' ? 'g' : spec.Presentation;
            length = 1;
            if (spec.Precision >= 0)
            {
                _ = spec.Precision.TryFormat(format[1..], out int count, provider: Invariant);
                length += count;
            }
        }
        if (magnitude.TryFormat(digits, out int size, format[..length], Invariant))
            AppendNumeric(output, digits[..size], spec, sign, double.IsFinite(magnitude));
        else
            AppendNumeric(output, magnitude.ToString(new string(format[..length]), Invariant), spec, sign, double.IsFinite(magnitude));
    }

    private static string LargeInteger(double value, char presentation)
    {
        var integer = new BigInteger(value);
        if (presentation == 'd') return integer.ToString(Invariant);
        if (presentation != 'o') return integer.ToString(presentation.ToString(), Invariant).TrimStart('0');

        // A finite binary64 magnitude has at most 1024 bits.
        Span<char> digits = stackalloc char[342];
        int start = digits.Length;
        do
        {
            integer = BigInteger.DivRem(integer, 8, out var digit);
            digits[--start] = (char)('0' + (int)digit);
        } while (!integer.IsZero);
        return new string(digits[start..]);
    }

    private static void AppendNumeric(StringBuilder output, ReadOnlySpan<char> text, TextFormat.Spec spec,
        char sign, bool finite)
    {
        string prefix = spec.Alternate ? spec.Presentation switch
        {
            'x' => "0x",
            'X' => "0X",
            'b' => "0b",
            'B' => "0B",
            _ => "0o"
        } : "";
        int padding = Math.Max(0, spec.Width - text.Length - prefix.Length - (sign == '\0' ? 0 : 1));
        char alignment = spec.Alignment == '\0' ? '>' : spec.Alignment;
        bool zero = spec.Zero && finite;
        int left = zero ? 0 : LeftPadding(alignment, padding);
        AppendFill(output, spec.Fill, left);
        if (sign != '\0') output.Append(sign);
        output.Append(prefix);
        if (zero) output.Append('0', padding);
        output.Append(text);
        if (!zero) AppendFill(output, spec.Fill, padding - left);
    }

    private static void AppendPadded(StringBuilder output, ReadOnlySpan<char> text, TextFormat.Spec spec,
        int padding, char defaultAlignment)
    {
        int left = LeftPadding(spec.Alignment == '\0' ? defaultAlignment : spec.Alignment, padding);
        AppendFill(output, spec.Fill, left);
        output.Append(text);
        AppendFill(output, spec.Fill, padding - left);
    }

    private static int LeftPadding(char alignment, int padding) => alignment switch
    {
        '<' => 0,
        '^' => padding / 2,
        _ => padding
    };

    private static void AppendFill(StringBuilder output, Rune fill, int count)
    {
        if (count == 0) return;
        if (fill.IsBmp) output.Append((char)fill.Value, count);
        else
        {
            Span<char> text = stackalloc char[2];
            fill.EncodeToUtf16(text);
            for (int index = 0; index < count; index++) output.Append(text);
        }
    }

    internal static string Quote(ReadOnlySpan<char> value)
    {
        StringBuilder output = new(value.Length + 2);
        output.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (char.IsControl(character)) output.Append("\\u").Append(((int)character).ToString("x4", Invariant));
                    else output.Append(character);
                    break;
            }
        }
        return output.Append('"').ToString();
    }
}
