using System.Globalization;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

/// <summary>Anchors typed compiler inputs without selecting or executing guest code.</summary>
public static partial class HpirBinding
{
    public static HpirProgram Prepare(HpirProgram program, BindingContext binding, ExecutionOptions? options = null,
        CancellationToken token = default, CompilationTrace? trace = null)
    {
        if (program.Stage == HpirStage.Executable) return program;
        token.ThrowIfCancellationRequested();
        var state = (program, binding, options, token, trace: trace?.Detail);
        return trace?.Detail is not { } detail ? PrepareCore(state)
            : detail.MeasureDetailed("backend.hpir.bind", state, PrepareCore,
                metrics: static result => new Dictionary<string, ulong>
                {
                    ["transfer.inputs"] = (ulong)result.PreparedTransfers.Count,
                    ["selection.frozen"] = (ulong)result.PreparedTransfers.Count(transfer =>
                        transfer.Kind == HpirTransferSource.Selection && transfer.Pattern is null),
                    ["selection.residual"] = (ulong)result.PreparedTransfers.Count(transfer => transfer.Pattern is not null),
                    ["selection.members"] = (ulong)result.PreparedTransfers.Where(transfer =>
                        transfer.Kind == HpirTransferSource.Selection && transfer.Pattern is null).Sum(transfer => (long)transfer.Paths.Count),
                    ["dependencies.edges"] = (ulong)result.PreparedTransfers.Sum(transfer => (long)transfer.Predecessors.Count),
                    ["context_checks.frozen"] = (ulong)result.ContextChecks.Count(check => check.Value.HasValue),
                    ["context_checks.residual"] = (ulong)result.ContextChecks.Count(check => !check.Value.HasValue),
                    ["iterator_selection.frozen"] = (ulong)result.IteratorSelections.Count(selection => selection.Members is not null),
                    ["iterator_selection.residual"] = (ulong)result.IteratorSelections.Count(selection => selection.Members is null)
                });
    }

    private static HpirContextCheck[] PrepareContextChecks(IReadOnlyList<HpirContextCheck> inputs,
        NativeRuntimeContext context, CancellationToken token, CompilationTrace? trace)
    {
        if (inputs.Count == 0) return [];
        HpirContextCheck[] checks = new HpirContextCheck[inputs.Count];
        for (int index = 0; index < checks.Length; ++index)
        {
            token.ThrowIfCancellationRequested();
            var check = inputs[index];
            checks[index] = trace is null ? ObserveCheck(check, context, token, null)
                : trace.MeasureDetailed("backend.hpir.bind.context_check", (check, context, token, trace),
                    static site => ObserveCheck(site.check, site.context, site.token, site.trace),
                    attributes: ContextCheckAttributes(check),
                    metrics: static result => new Dictionary<string, ulong> { ["frozen"] = result.Value.HasValue ? 1UL : 0UL });
        }
        return checks;
    }

    private static HpirContextCheck ObserveCheck(HpirContextCheck check, NativeRuntimeContext context,
        CancellationToken token, CompilationTrace? trace)
    {
        try
        {
            var path = ToNativePath(check.Path);
            string location = context.LocalPath(path);
            token.ThrowIfCancellationRequested();
            return check with { Path = new(location), Value = NativeBuiltins.LocalFilePredicate(check.Operation, location) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            trace?.Report(new("backend.hpir.bind.context_check.unavailable", CompilationTrace.UnixNow(), 0, 0,
                CompilationOutcome.Ok, Aggregated: true)
            {
                Attributes = new Dictionary<string, string> { ["input.path"] = check.Path.Raw, ["reason"] = error.Message }
            });
            return check;
        }
    }

    private static IReadOnlyDictionary<string, string> ContextCheckAttributes(HpirContextCheck check)
    {
        var attributes = new Dictionary<string, string>
        {
            ["operation"] = HpirFileMetadataImports.NativeName(check.Operation), ["input.path"] = check.Path.Raw
        };
        if (check.Source is { } source)
        {
            attributes["source.file"] = source.Source;
            attributes["source.offset"] = source.Start.ToString(CultureInfo.InvariantCulture);
        }
        return attributes;
    }

    private static HpirProgram PrepareCore((HpirProgram program, BindingContext binding, ExecutionOptions? options,
        CancellationToken token, CompilationTrace? trace) state)
    {
        var (program, binding, options, token, trace) = state;
        if (program.TransferInputs.Count == 0 && program.ContextChecks.Count == 0 && program.IteratorSelections.Count == 0)
            return program with { Stage = HpirStage.Executable, Context = binding };
        var context = new NativeRuntimeContext(options, binding);
        HpirContextCheck[] checks = PrepareContextChecks(program.ContextChecks, context, token, trace);
        HpirIteratorSelection[] selections = PrepareIteratorSelections(program.IteratorSelections, context, token, trace);
        HpirPreparedTransfer[] prepared = new HpirPreparedTransfer[program.TransferInputs.Count];
        HpirTransferFrontier? frontier = prepared.Length > 1 ? new() : null;
        for (int site = 0; site < prepared.Length; site++)
        {
            token.ThrowIfCancellationRequested();
            HpirTransferInput input = program.TransferInputs[site];
            prepared[site] = trace is null ? BindTransfer(site, input, context, frontier, token, null)
                : trace.MeasureDetailed("backend.hpir.bind.transfer", (site, input, context, frontier, token, trace),
                    static item => BindTransfer(item.site, item.input, item.context, item.frontier, item.token, item.trace),
                    attributes: TransferAttributes(site, input));
        }
        return program with { Stage = HpirStage.Executable, Context = binding, TransferInputs = [],
            PreparedTransfers = prepared, ContextChecks = checks, IteratorSelections = selections };
    }

    private static HpirPreparedTransfer BindTransfer(int site, HpirTransferInput input, NativeRuntimeContext context,
        HpirTransferFrontier? frontier, CancellationToken token, CompilationTrace? trace)
    {
        HpirPreparedTransfer transfer = PrepareTransfer(input, context, frontier, token, trace);
        return frontier?.Add(site, transfer, trace) ?? transfer;
    }

    private static HpirPreparedTransfer PrepareTransfer(HpirTransferInput input, NativeRuntimeContext context,
        HpirTransferFrontier? frontier, CancellationToken token, CompilationTrace? trace)
    {
        string destination = context.LocalPath(ToNativePath(input.Destination));
        if (input.Kind == HpirTransferSource.Text) return new(input.Kind, [], input.Text, destination, null, input.Source);
        string path = context.LocalPath(ToNativePath(input.Path!.Value));
        if (input.Kind == HpirTransferSource.Path) return new(input.Kind, [path], null, destination, null, input.Source);
        if (frontier?.MayChangeSelection(path) == true)
        {
            trace?.Metric("selection.residual.preceding_write", 1);
            return new(input.Kind, [path], null, destination, input.Pattern, input.Source);
        }

        try
        {
            string[] selected = FreezeMembers(path, input.Pattern!, context, token).GetAwaiter().GetResult();
            trace?.Metric("selection.members", selected.Length);
            trace?.Metric("selection.frozen", 1);
            return new(input.Kind, selected, null, destination, null, input.Source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An unavailable observation isn't an earlier execution failure.
            // Retain the already-anchored directory and a live selection call.
            trace?.Metric("selection.residual", 1);
            trace?.Report(new("backend.hpir.bind.selection.unavailable", CompilationTrace.UnixNow(), 0, 0,
                CompilationOutcome.Ok, Aggregated: true)
            {
                Attributes = new Dictionary<string, string> { ["selection.directory"] = path, ["reason"] = error.Message }
            });
            return new(input.Kind, [path], null, destination, input.Pattern, input.Source);
        }
    }

    private static NativePathValue ToNativePath(HpirLocalPath path) =>
        new(path.Raw, LocalSpelling: OperatingSystem.IsWindows() ? path.WindowsSpelling : null);

    private static async ValueTask<string[]> FreezeMembers(string path, string pattern, NativeRuntimeContext context,
        CancellationToken token)
    {
        List<string> paths = [];
        await foreach (var selected in NativeHostIO.OpenSelectedPaths(new(path), pattern, context, token).ConfigureAwait(false))
            paths.Add(context.LocalPath(selected));
        return paths.ToArray();
    }

    private static IReadOnlyDictionary<string, string> TransferAttributes(int site, HpirTransferInput input)
    {
        var attributes = new Dictionary<string, string>
        {
            ["transfer.id"] = site.ToString(CultureInfo.InvariantCulture),
            ["transfer.kind"] = input.Kind.ToString(),
            ["transfer.destination"] = input.Destination.Raw
        };
        if (input.Source is { } source)
        {
            attributes["source.file"] = source.Source;
            attributes["source.offset"] = source.Start.ToString(CultureInfo.InvariantCulture);
            attributes["source.length"] = source.Length.ToString(CultureInfo.InvariantCulture);
        }
        return attributes;
    }
}
