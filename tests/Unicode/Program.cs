using System.Globalization;
using System.Text;
using Msync.Runtime;

static string Hex(string value) => string.Join(' ', value.EnumerateRunes().Select(rune => rune.Value.ToString("X", CultureInfo.InvariantCulture)));
static void Write(string value)
{
    Console.WriteLine($"{Hex(UnicodeCasing.ToLower(value))};{Hex(UnicodeCasing.ToUpper(value))}");
}

if (args is ["--scalars"])
{
    for (int scalar = 0; scalar <= 0x10FFFF; scalar++)
    {
        if (scalar is >= 0xD800 and <= 0xDFFF) continue;
        string value = char.ConvertFromUtf32(scalar);
        string lower = UnicodeCasing.ToLower(value), upper = UnicodeCasing.ToUpper(value);
        if (value != lower || value != upper)
            Console.WriteLine($"{scalar:X};{Hex(lower)};{Hex(upper)}");
    }
    return;
}

string? line;
while ((line = Console.ReadLine()) is not null)
{
    StringBuilder input = new();
    foreach (string scalar in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        input.Append(char.ConvertFromUtf32(int.Parse(scalar, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
    Write(input.ToString());
}
