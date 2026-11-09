using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msync.Cli;

internal sealed class JsonRpcTransport
{
    private readonly Stream input = new BufferedStream(Console.OpenStandardInput(), 16384);
    private readonly Stream output = Console.OpenStandardOutput();

    internal JsonDocument? Read()
    {
        int length = -1;
        while (true)
        {
            StringBuilder line = new();
            int value;
            while ((value = input.ReadByte()) != -1 && value != '\n') if (value != '\r') line.Append((char)value);
            if (value == -1) return null;
            if (line.Length == 0) break;
            string header = line.ToString();
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.AsSpan(15), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (length < 0) throw new InvalidDataException("LSP message has no Content-Length header.");
        byte[] bytes = new byte[length];
        input.ReadExactly(bytes);
        return JsonDocument.Parse(bytes);
    }

    internal void Write(JsonNode value)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer)) value.WriteTo(writer);
        output.Write(Encoding.ASCII.GetBytes($"Content-Length: {buffer.WrittenCount}\r\n\r\n"));
        output.Write(buffer.WrittenSpan);
        output.Flush();
    }
}
