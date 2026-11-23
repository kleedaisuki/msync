using System.Globalization;
using Msync.Core;

namespace Msync.Runtime;

internal enum NumberTextPath : byte { Invalid, ShortInteger, General }

internal static class ScalarNumberText
{
    internal static NumberTextPath Parse(ReadOnlySpan<char> text, out double number)
    {
        if (TryShortInteger(text, out number)) return NumberTextPath.ShortInteger;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return NumberTextPath.General;
        ReadOnlySpan<char> literal = text.Trim();
        bool negative = !literal.IsEmpty && literal[0] == '-';
        if (!literal.IsEmpty && literal[0] is '+' or '-') literal = literal[1..];
        if (!NumberSyntax.TryParse(literal.ToString(), out number)) return NumberTextPath.Invalid;
        if (negative) number = -number;
        return NumberTextPath.General;
    }

    private static bool TryShortInteger(ReadOnlySpan<char> text, out double number)
    {
        number = 0;
        if (text.IsEmpty || text.Length > 16) return false;
        bool negative = text[0] == '-';
        if (negative || text[0] == '+') text = text[1..];
        // Every integer with at most fifteen decimal digits is exact in binary64.
        if ((uint)(text.Length - 1) >= 15) return false;
        ulong value = 0;
        foreach (char code in text)
        {
            uint digit = (uint)(code - '0');
            if (digit > 9) return false;
            value = value * 10 + digit;
        }
        number = negative ? -(double)value : (double)value;
        return true;
    }
}
