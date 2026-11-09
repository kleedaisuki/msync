using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Msync.Core;

public static partial class NumberSyntax
{
    [GeneratedRegex(@"\G(?:0[xX][0-9a-fA-F](?:_?[0-9a-fA-F])*|0[bB][01](?:_?[01])*|0[oO][0-7](?:_?[0-7])*|(?:[0-9](?:_?[0-9])*(?:\.(?:[0-9](?:_?[0-9])*)?)?|\.[0-9](?:_?[0-9])*)(?:[eE][+-]?[0-9](?:_?[0-9])*)?)", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralPattern();

    public static int LengthAt(string source, int start)
    {
        Match match = LiteralPattern().Match(source, start);
        if (!match.Success) return 0;
        int end = start + match.Length;
        if (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] is '_' or '.' or '\\')) return 0;
        if (end + 1 < source.Length && source[end] == '-' && (char.IsLetter(source[end + 1]) || source[end + 1] == '_')) return 0;
        return match.Length;
    }

    public static bool TryParse(string text, out double value)
    {
        value = 0;
        if (text.Length == 0 || LengthAt(text, 0) != text.Length) return false;
        string digits = text.Replace("_", "", StringComparison.Ordinal);
        if (digits.Length < 2 || digits[0] != '0' || digits[1] is not ('x' or 'X' or 'b' or 'B' or 'o' or 'O'))
            return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        int radix = char.ToLowerInvariant(digits[1]) switch { 'x' => 16, 'b' => 2, _ => 8 };
        ulong integral = 0;
        for (int index = 2; index < digits.Length; index++)
        {
            int digit = Digit(digits[index]);
            if (integral > (ulong.MaxValue - (uint)digit) / (uint)radix)
            {
                BigInteger large = integral;
                for (; index < digits.Length; index++) large = large * radix + Digit(digits[index]);
                value = (double)large;
                return true;
            }
            integral = integral * (uint)radix + (uint)digit;
        }
        value = integral;
        return true;
    }

    private static int Digit(char digit) => digit <= '9' ? digit - '0' : char.ToLowerInvariant(digit) - 'a' + 10;
}
