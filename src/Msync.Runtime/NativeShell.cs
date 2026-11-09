using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static Value ShellOperation(string name, IReadOnlyList<Value> arguments, RuntimeContext context,
        CancellationToken cancellationToken) => name switch
    {
        "tail_lines" => TailRange(Range(arguments[0], name), Ordinal(arguments[1], name, false), cancellationToken),
        "unique_lines" => Adapt(Range(arguments[0], name), TypeRef.String, UniqueLines, cancellationToken),
        "which" => Which(Text(arguments[0]), context),
        "process_table" => ProcessTable(cancellationToken),
        _ => throw new MsyncException($"Unknown shell native '{name}'")
    };

    private static RangeValue TailRange(RangeValue source, long count, CancellationToken cancellationToken) =>
        count == 0 ? new RangeValue(Array.Empty<Value>(), TypeRef.String)
            : Adapt(source, TypeRef.String, (values, token) => TailLines(values, count, token), cancellationToken);

    private static async IAsyncEnumerable<Value> TailLines(RangeValue source, long count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (count == 0) yield break;
        Queue<Value> retained = [];
        await foreach (Value value in source.Items.WithCancellation(cancellationToken))
        {
            if (retained.Count == count) retained.Dequeue();
            retained.Enqueue(value);
        }
        foreach (Value value in retained)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    private static async IAsyncEnumerable<Value> UniqueLines(RangeValue source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? previous = null;
        await foreach (Value value in source.Items.WithCancellation(cancellationToken))
        {
            string text = Text(value);
            if (previous is null || !string.Equals(previous, text, StringComparison.Ordinal))
                yield return value;
            previous = text;
        }
    }

    private static Value Which(string name, RuntimeContext context)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            string path = Path.GetFullPath(name, context.WorkingDirectory);
            return Executable(path) ? new PathValue(path, context.WorkingDirectory, HomeDirectory: context.HomeDirectory) : Value.Null;
        }
        string[] extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';') : [""];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string extension in extensions)
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, name + extension), context.WorkingDirectory);
                if (Executable(candidate)) return new PathValue(candidate, context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
            }
        return Value.Null;
    }

    private static bool Executable(string path)
    {
        if (!File.Exists(path)) return false;
        return OperatingSystem.IsWindows() || CanExecute(path, 1) == 0;
    }

    private static RangeValue ProcessTable(CancellationToken cancellationToken)
    {
        List<Value> rows = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    rows.Add(new ScopeValue(ProcessRowLayout,
                        [new NumberValue(process.Id), new StringValue(process.ProcessName)]));
                }
                // A process can exit between enumeration and reading its name.
                catch (InvalidOperationException) { }
                catch (Win32Exception error)
                {
                    rows.Add(new ScopeValue(ProcessRowLayout,
                        [new NumberValue(process.Id), new StringValue($"<unavailable: {error.Message}>")]));
                }
            }
        }
        rows.Sort(static (left, right) => ((NumberValue)((ScopeValue)left).GetField(0)).Data
            .CompareTo(((NumberValue)((ScopeValue)right).GetField(0)).Data));
        return new(rows, TypeRef.Scope);
    }

    private static readonly ScopeLayout ProcessRowLayout = new(["pid", "name"]);

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int CanExecute(string path, int mode);
}
