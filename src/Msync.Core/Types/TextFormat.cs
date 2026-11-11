using System.Buffers;
using System.Globalization;
using System.Text;

namespace Msync.Core;

/// <summary>The shared reader for ordinary brace-format Strings.</summary>
public static class TextFormat
{
    public readonly record struct Spec(Rune Fill, char Alignment, char Sign, bool Alternate, bool Zero,
        int Width, int Precision, char Presentation)
    {
        public bool Integral => Presentation is 'd' or 'x' or 'X' or 'b' or 'B' or 'o';
        public bool Numeric => Integral || Presentation is 'f' or 'F' or 'e' or 'E' or 'g' or 'G'
            || Sign != '\0' || Zero;
        public TypeKind AcceptedKinds => Numeric ? TypeKind.Number : Presentation == 's'
            ? TypeKind.String | TypeKind.Path : TypeKind.Any;
    }

    // A literal's length is its text length; a field's length is its selector length.
    public readonly record struct Part(int Offset, int Length, bool IsField = false, int Argument = -1, Spec Format = default)
    {
        public ReadOnlySpan<char> Name(string template) => template.AsSpan(Offset + 1, Length);
    }

    public struct Reader(string template, bool named = false)
    {
        private int position;
        private int automatic;
        private Indexing indexing;

        private enum Indexing : byte { Unset, Automatic, Explicit }

        public bool MoveNext(out Part part)
        {
            part = default;
            if (position == template.Length) return false;

            int start = position;
            ReadOnlySpan<char> rest = template.AsSpan(start);
            int literal = rest.IndexOfAny('{', '}');
            if (literal != 0)
            {
                int length = literal < 0 ? rest.Length : literal;
                part = new(start, length);
                position += length;
                return true;
            }
            if (rest.Length > 1 && rest[0] == rest[1])
            {
                part = new(start, 1);
                position += 2;
                return true;
            }
            if (rest[0] == '}') throw Error(template, start, "unmatched '}' (write '}}' for a literal brace)");

            int close = rest[1..].IndexOfAny('{', '}');
            if (close < 0) throw Error(template, start, "unclosed field (write '{{' for a literal brace)");
            if (rest[close + 1] == '{') throw Error(template, start, "nested format fields are not supported");
            ReadOnlySpan<char> field = rest.Slice(1, close);
            int colon = field.IndexOf(':');
            ReadOnlySpan<char> selector = colon < 0 ? field : field[..colon];
            if (named && selector.IsEmpty) throw Error(template, start, "a named field requires a Scope member name");
            int argument = named ? -1 : Argument(selector, start);
            part = new(start, selector.Length, true, argument,
                ParseSpec(template, start, colon < 0 ? [] : field[(colon + 1)..]));
            position += close + 2;
            return true;
        }

        private int Argument(ReadOnlySpan<char> selector, int offset)
        {
            var mode = selector.IsEmpty ? Indexing.Automatic : Indexing.Explicit;
            if (indexing != Indexing.Unset && indexing != mode)
                throw Error(template, offset, "automatic '{}' and explicit '{0}' indexes cannot be mixed");
            indexing = mode;
            if (mode == Indexing.Automatic) return automatic++;
            if (int.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return index;
            throw Error(template, offset, "expected a zero-based argument index; use format_fields for names");
        }
    }

    private static Spec ParseSpec(string template, int offset, ReadOnlySpan<char> spec)
    {
        Rune fill = new(' ');
        char alignment = '\0';
        if (!spec.IsEmpty && Rune.DecodeFromUtf16(spec, out Rune first, out int size) == OperationStatus.Done)
        {
            if (size < spec.Length && IsAlignment(spec[size]))
            {
                fill = first;
                alignment = spec[size];
                spec = spec[(size + 1)..];
            }
            else if (IsAlignment(spec[0]))
            {
                alignment = spec[0];
                spec = spec[1..];
            }
        }
        char sign = '\0';
        if (!spec.IsEmpty && spec[0] is '+' or '-' or ' ')
        {
            sign = spec[0];
            spec = spec[1..];
        }
        bool alternate = Take(ref spec, '#');
        bool zero = Take(ref spec, '0');
        int width = Digits(template, offset, ref spec, 0);
        int precision = Take(ref spec, '.') ? Digits(template, offset, ref spec, -1) : -1;
        char presentation = spec.IsEmpty ? '\0' : spec[0];
        if (spec.Length > 1 || !spec.IsEmpty && presentation == '\0' || presentation is not ('\0' or 's' or '?' or 'd' or 'x' or 'X' or 'b' or 'B' or 'o'
            or 'f' or 'F' or 'e' or 'E' or 'g' or 'G'))
            throw Error(template, offset, $"unknown format specifier '{spec.ToString()}'");

        Spec result = new(fill, alignment, sign, alternate, zero, width, precision, presentation);
        if (alternate && presentation is not ('x' or 'X' or 'b' or 'B' or 'o'))
            throw Error(template, offset, "'#' requires a base presentation (x, X, b, B or o)");
        if (zero && alignment != '\0')
            throw Error(template, offset, "zero padding and alignment cannot be combined; use an explicit fill such as '0>'");
        if (presentation is 's' or '?' && result.Numeric)
            throw Error(template, offset, "numeric flags cannot be applied to text");
        if (precision >= 0 && (result.Integral || presentation == '?'))
            throw Error(template, offset, "precision is not supported for integer or debug presentations");
        return result;
    }

    private static bool IsAlignment(char value) => value is '<' or '>' or '^';

    private static bool Take(ref ReadOnlySpan<char> spec, char value)
    {
        if (spec.IsEmpty || spec[0] != value) return false;
        spec = spec[1..];
        return true;
    }

    private static int Digits(string template, int offset, ref ReadOnlySpan<char> spec, int fallback)
    {
        int count = 0;
        while (count < spec.Length && char.IsAsciiDigit(spec[count])) count++;
        if (count == 0 && fallback >= 0) return fallback;
        if (!int.TryParse(spec[..count], NumberStyles.None, CultureInfo.InvariantCulture, out int result))
            throw Error(template, offset, "width or precision must be a representable nonnegative integer");
        spec = spec[count..];
        return result;
    }

    public static void CheckType(string template, Part field, TypeRef type)
    {
        if (type.Kind != TypeKind.Never && (field.Format.AcceptedKinds & type.Kind) == 0)
            throw Error(template, field.Offset, $"this field expects {(field.Format.Numeric ? "Number" : "String or Path")}, not {type}");
        if (type == TypeRef.Number && field.Format.Precision > 999_999_999)
            throw Error(template, field.Offset, "numeric precision exceeds the supported decimal precision");
    }

    public static void CheckNumber(string template, Part field, double number)
    {
        if (field.Format.Integral && (!double.IsFinite(number) || Math.Truncate(number) != number))
            throw Error(template, field.Offset,
                $"'{field.Format.Presentation}' requires a finite integral Number, not {number.ToString(CultureInfo.InvariantCulture)}");
    }

    public static MsyncException Error(string template, int offset, string detail)
    {
        int position = 1;
        foreach (var _ in template.AsSpan(0, offset).EnumerateRunes()) position++;
        return new($"Invalid format at template character {position}: {detail}.");
    }
}
