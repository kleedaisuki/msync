using System.Diagnostics;
using System.Globalization;
using System.Text;
using Msync.Core;

namespace Msync.Runtime;

public static partial class TransferEngine
{
    private static readonly AsyncLocal<SelectedQuery?> ActiveQuery = new();

    internal static bool HasSelectedQuery(ConnectionValue connection) => ActiveQuery.Value?.Connection == connection;

    private sealed class SelectedQuery(ConnectionValue connection, Process process) : IDisposable
    {
        private readonly SemaphoreSlim serial = new(1);
        internal ConnectionValue Connection { get; } = connection;

        internal async Task<string> InvokeAsync(string script, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            await serial.WaitAsync(cancellationToken);
            try
            {
                Stream input = process.StandardInput.BaseStream;
                await input.WriteAsync("MSYNC-QUERY\n"u8.ToArray(), cancellationToken);
                await WriteEncodedLineAsync(input, script, cancellationToken);
                await input.WriteAsync(Encoding.ASCII.GetBytes(arguments.Count.ToString(CultureInfo.InvariantCulture) + "\n"), cancellationToken);
                foreach (string argument in arguments) await WriteEncodedLineAsync(input, argument, cancellationToken);
                await input.FlushAsync(cancellationToken);
                string header = await ReadHeaderAsync(process.StandardOutput.BaseStream, cancellationToken);
                string[] fields = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3 || fields[0] != "MSYNC-QUERY")
                    throw new IOException($"Invalid selected metadata response from {Connection}: {header}");
                int status = int.Parse(fields[1], CultureInfo.InvariantCulture);
                int bytes = int.Parse(fields[2], CultureInfo.InvariantCulture);
                byte[] output = new byte[bytes];
                await process.StandardOutput.BaseStream.ReadExactlyAsync(output, cancellationToken);
                if (status != 0) throw new IOException($"Selected native operation on {Connection} failed with exit status {status}; see its diagnostic output.");
                return Encoding.UTF8.GetString(output);
            }
            finally { serial.Release(); }
        }

        public void Dispose() => serial.Dispose();
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        StringBuilder header = new();
        byte[] current = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(current, cancellationToken);
            if (current[0] == (byte)'\n') return header.ToString();
            header.Append((char)current[0]);
        }
    }

    private static async Task WriteEncodedLineAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        StringBuilder encoded = new();
        foreach (byte value in Encoding.UTF8.GetBytes(text))
            encoded.Append("\\0").Append(Convert.ToString(value, 8).PadLeft(3, '0'));
        encoded.Append('\n');
        await stream.WriteAsync(Encoding.ASCII.GetBytes(encoded.ToString()), cancellationToken);
    }

    private static async Task<IReadOnlyList<Value>> ApplySelectionAsync(PathSelection selection, IReadOnlyList<string> names,
        Process process, CancellationToken cancellationToken)
    {
        IEnumerable<Value> values = names.Select(name => selection.Directory.WithRaw(selection.Directory.Raw.TrimEnd('/') + "/" + name));
        RangeValue discovered = new(values, TypeRef.Path);
        List<Value> results = [];
        SelectedQuery? previous = ActiveQuery.Value;
        using SelectedQuery query = new(Remote(selection.Directory)!, process);
        ActiveQuery.Value = query;
        try
        {
            IAsyncEnumerable<Value> items = selection.Apply?.Invoke(discovered.Items, cancellationToken) ?? discovered.Items;
            await foreach (Value value in items.WithCancellation(cancellationToken)) results.Add(value);
        }
        finally { ActiveQuery.Value = previous; }
        return results;
    }

    private static string SelectionQueryScript => field ??= ShellProgram.Source("\n" + """
        query() (
            IFS= read -r encoded_script
            query_script=$(printf '%b.' "$encoded_script"); query_script=${query_script%.}
            IFS= read -r count
            set --
            while [ "$count" -gt 0 ]; do
                IFS= read -r encoded_argument
                argument=$(printf '%b.' "$encoded_argument"); argument=${argument%.}
                set -- "$@" "$argument"
                count=$((count - 1))
            done
            output=$(mktemp "${TMPDIR:-/tmp}/.msync-query-XXXXXXXXXX")
            trap 'rm -f -- "$output"' EXIT HUP INT TERM
            if sh -c "$query_script" sh "$@" < /dev/null > "$output"; then status=0; else status=$?; fi
            bytes=$(wc -c < "$output")
            printf 'MSYNC-QUERY %s %s\n' "$status" "$bytes"
            cat -- "$output"
        )
        """);
}
