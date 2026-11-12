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
        RuntimeContext origin = context.CaptureDirectory();
        var range = name == "io.read_lines"
            ? new RangeValue(new StringRangeItems.Async(ReadIOItems(name, arguments, origin,
                static values => ((StringRangeItems.Async)values.Items).Read, token)), TypeRef.String)
            : new RangeValue(ReadIOItems(name, arguments, origin, static values => values.Items, token), TypeRef.Number);
        range.RetainedValues = arguments;
        return range;
    }

    private async IAsyncEnumerable<T> ReadIOItems<T>(string name, IReadOnlyList<Value> arguments, RuntimeContext origin,
        Func<RangeValue, IAsyncEnumerable<T>> select,
        [EnumeratorCancellation] CancellationToken token)
    {
        var resolved = await ResolveArgumentsAsync(arguments);
        Value[] paths = [origin.AsPath(resolved[0])];
        await AwaitNativeDependenciesAsync(NativeSignatures.Get("io.read_text"), paths, token);
        using var selectedSession = EnterPathSession(paths, token);
        var values = (RangeValue)await NativeRegistry.InvokeAsync(name, paths, origin, NativeCallback, token);
        await foreach (T value in select(values).WithCancellation(token)) yield return value;
    }
    private async IAsyncEnumerable<Value> SelectAsync(PathValue directory, string pattern, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await AwaitNativeDependenciesAsync(NativeSignatures.Get("fs.directory_iterator"), [directory], cancellationToken);
        using var selectedSession = EnterPathSession([directory], cancellationToken);
        var values = await NativeRegistry.InvokeAsync("fs.directory_iterator", [directory], context, NativeCallback, cancellationToken) as RangeValue ?? throw new MsyncException("Directory iterator did not return a Range");
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        await foreach (var item in values.Items.WithCancellation(cancellationToken))
            if (item is PathValue itemPath ? regex.IsMatch(PathSemantics.FileNameSpan(itemPath))
                : regex.IsMatch(Path.GetFileName(Text(item).AsSpan()))) yield return item;
    }
    private async Task<bool> TransferAsync(Value source, Value destination, SourceSpan? span, bool local, RuntimeContext context, CancellationToken cancellationToken)
    {
        var started = context.Observe is null ? 0 : Stopwatch.GetTimestamp();
        using var activity = context.Timeline?.Begin("transfer.residual");
        if (activity is not null)
        {
            TransferTimeline.Source(activity, source);
            if (destination is PathValue path) TransferTimeline.Path(activity, "destination", path);
            else if (destination is StringValue text) activity.SetAttribute("destination.path", text.Data);
            activity.SetSource(span);
            activity.SetAttribute("transfer.dry_run", context.DryRun ? "true" : "false");
        }
        try
        {
            var result = local ? await TransferEngine.CopyLocalAsync(source, destination, context, cancellationToken) : await TransferEngine.CopyAsync(source, destination, context, cancellationToken);
            activity?.Complete(result);
            context.Observe?.Invoke(new("transfer", result ? "success" : "failure", SourceLabel(source), destination.ToString(), DurationNanoseconds: (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000_000)));
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            activity?.Fail(error);
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
        catch (Exception error) { activity?.Fail(error); throw; }
    }
    private static string SourceLabel(Value value) => value is StringValue text ? $"literal ({Encoding.UTF8.GetByteCount(text.Data)} UTF-8 bytes)" : value.ToString()!;
}
