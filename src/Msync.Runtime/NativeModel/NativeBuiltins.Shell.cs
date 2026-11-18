using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static readonly NativeLayout ProcessRowLayout = NativeBuiltinLayouts.Process.Layout;

    private static NativeValue ShellOperation(string operation, IReadOnlyList<NativeValue> args,
        NativeRuntimeContext context, CancellationToken token) => operation switch
        {
            "tail_lines" => NativeValue.FromRange(TailRange(Range(args[0], operation), Ordinal(args[1], operation, false, "shell"), context, token)),
            "unique_lines" => NativeValue.FromRange(Adapt(Range(args[0], operation), TypeRef.String, UniqueLines, token)),
            "which" => Which(Text(args[0]), context),
            "process_table" => NativeValue.FromRange(ProcessTable(context, token)),
            _ => throw new MsyncException($"Unknown shell native '{operation}'")
        };

    private static NativeRange TailRange(NativeRange source, long count, NativeRuntimeContext context, CancellationToken token) => count == 0
        ? context.Snapshot([], TypeRef.String)
        : Adapt(source, TypeRef.String, (range, cancellation) => TailLines(range, count, cancellation), token);

    private static async IAsyncEnumerable<NativeValue> TailLines(NativeRange source, long count,
        [EnumeratorCancellation] CancellationToken token)
    {
        Queue<NativeValue> retained = [];
        await foreach (NativeValue value in source.Items.WithCancellation(token))
        {
            if (retained.Count == count) retained.Dequeue();
            retained.Enqueue(value);
        }
        foreach (NativeValue value in retained) { token.ThrowIfCancellationRequested(); yield return value; }
    }

    private static async IAsyncEnumerable<NativeValue> UniqueLines(NativeRange source,
        [EnumeratorCancellation] CancellationToken token)
    {
        string? previous = null;
        await foreach (NativeValue value in source.Items.WithCancellation(token))
        {
            string text = Text(value);
            if (previous is null || !string.Equals(previous, text, StringComparison.Ordinal)) yield return value;
            previous = text;
        }
    }

    private static NativeValue Which(string name, NativeRuntimeContext context)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            string path = Path.GetFullPath(name, context.WorkingDirectory);
            return Executable(path) ? NativeValue.FromPath(new(path, context.WorkingDirectory, HomeDirectory: context.HomeDirectory)) : NativeValue.Null;
        }
        string[] extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';') : [""];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string extension in extensions)
            {
                string candidate = Path.GetFullPath(Path.Combine(directory, name + extension), context.WorkingDirectory);
                if (Executable(candidate)) return NativeValue.FromPath(new(candidate, context.WorkingDirectory, HomeDirectory: context.HomeDirectory));
            }
        return NativeValue.Null;
    }

    private static bool Executable(string path) => File.Exists(path) && (OperatingSystem.IsWindows() || NativeAccess.Executable(path));

    private static NativeRange ProcessTable(NativeRuntimeContext context, CancellationToken token)
    {
        List<NativeValue> rows = [];
        foreach (Process process in Process.GetProcesses())
        {
            using Process owned = process;
            token.ThrowIfCancellationRequested();
            try { rows.Add(Record(context, ProcessRowLayout, NativeValue.FromNumber(owned.Id), NativeValue.FromString(owned.ProcessName))); }
            catch (InvalidOperationException) { }
            catch (Win32Exception error)
            { rows.Add(Record(context, ProcessRowLayout, NativeValue.FromNumber(owned.Id), NativeValue.FromString($"<unavailable: {error.Message}>"))); }
        }
        rows.Sort(static (left, right) => left.AsAddress().Field(0).Load().AsNumber().CompareTo(right.AsAddress().Field(0).Load().AsNumber()));
        return context.Snapshot(rows, TypeRef.Scope);
    }
}
