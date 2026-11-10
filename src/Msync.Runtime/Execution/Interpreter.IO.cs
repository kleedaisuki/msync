using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private RangeValue ReadIORange(string name, IReadOnlyList<Value> arguments, CancellationToken token)
    {
        var type = name == "io.read_lines" ? TypeRef.String : TypeRef.Number;
        return new(ReadIOItems(name, arguments, context.CaptureDirectory(), token), type)
        { RetainedValues = arguments };
    }

    private async IAsyncEnumerable<Value> ReadIOItems(string name, IReadOnlyList<Value> arguments, RuntimeContext origin,
        [EnumeratorCancellation] CancellationToken token)
    {
        var resolved = await ResolveArgumentsAsync(arguments);
        Value[] paths = [origin.AsPath(resolved[0])];
        await AwaitNativeDependenciesAsync("io.read_text", paths, token);
        using var selectedSession = EnterPathSession(paths, token);
        var values = (RangeValue)await NativeRegistry.InvokeAsync(name, paths, origin, NativeCallback, token);
        await foreach (var value in values.Items.WithCancellation(token)) yield return value;
    }
    private async IAsyncEnumerable<Value> SelectAsync(PathValue directory, string pattern, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await AwaitNativeDependenciesAsync("fs.directory_iterator", [directory], cancellationToken);
        using var selectedSession = EnterPathSession([directory], cancellationToken);
        var values = await NativeRegistry.InvokeAsync("fs.directory_iterator", [directory], context, NativeCallback, cancellationToken) as RangeValue ?? throw new MsyncException("Directory iterator did not return a Range");
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        await foreach (var item in values.Items.WithCancellation(cancellationToken))
            if (regex.IsMatch(item is PathValue itemPath ? PathSemantics.FileName(itemPath) : Path.GetFileName(Text(item)))) yield return item;
    }
    private async Task<bool> TransferAsync(Value source, Value destination, SourceSpan? span, bool local, RuntimeContext context, CancellationToken cancellationToken)
    {
        var started = context.Observe is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            var result = local ? await TransferEngine.CopyLocalAsync(source, destination, context, cancellationToken) : await TransferEngine.CopyAsync(source, destination, context, cancellationToken);
            context.Observe?.Invoke(new("transfer", result ? "success" : "failure", SourceLabel(source), destination.ToString(), DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000_000)));
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Interlocked.Increment(ref failedTransfers);
            context.Observe?.Invoke(new("transfer", "failure", SourceLabel(source), destination.ToString(), error.ToString(), (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000_000)));
            if (context.ReportError is { } report)
                report(new MsyncException($"Cannot transfer {SourceLabel(source)} -> {destination}.", span, error));
            else
            {
                var location = span is { } origin ? $"{origin.Source}:{origin.Start}: " : "";
                await context.Error.WriteLineAsync($"{location}transfer {SourceLabel(source)} -> {destination}: {error.Message}");
                for (var cause = error.InnerException; cause is not null; cause = cause.InnerException)
                    await context.Error.WriteLineAsync($"  caused by {cause.Message}");
            }
            return false;
        }
    }
    private static string SourceLabel(Value value) => value is StringValue text ? $"literal ({Encoding.UTF8.GetByteCount(text.Data)} UTF-8 bytes)" : value.ToString()!;
}
