using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Native;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

public static partial class HpirBinding
{
    private static HpirIteratorSelection[] PrepareIteratorSelections(IReadOnlyList<HpirIteratorSelection> recipes,
        NativeRuntimeContext context, CancellationToken token, CompilationTrace? trace)
    {
        if (recipes.Count == 0) return [];
        var result = new HpirIteratorSelection[recipes.Count];
        for (int index = 0; index < recipes.Count; ++index)
        {
            token.ThrowIfCancellationRequested();
            var recipe = recipes[index];
            result[index] = trace is null ? ObserveIteratorSelection(recipe, context, token, null)
                : trace.MeasureDetailed("backend.hpir.bind.iterator_selection", (recipe, context, token, trace),
                    static item => ObserveIteratorSelection(item.recipe, item.context, item.token, item.trace),
                    attributes: new Dictionary<string, string>
                    {
                        ["selection.directory"] = recipe.Directory.Raw,
                        ["selection.pattern"] = recipe.Pattern,
                        ["source.file"] = recipe.Source?.Source ?? ""
                    });
        }
        return result;
    }

    private static HpirIteratorSelection ObserveIteratorSelection(HpirIteratorSelection recipe, NativeRuntimeContext context,
        CancellationToken token, CompilationTrace? trace)
    {
        var directory = ToNativePath(recipe.Directory) with
        {
            WorkingDirectory = context.WorkingDirectory,
            HomeDirectory = context.HomeDirectory,
            Connection = recipe.ExplicitLocal ? new NativeConnection(ConnectionKind.Local) : null
        };
        try
        {
            HpirLocalPath[] members = ObserveMembers(directory, recipe.Pattern, context, token).GetAwaiter().GetResult();
            trace?.Metric("selection.members", members.Length);
            trace?.Metric("selection.frozen", 1);
            return recipe with { Members = members };
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            trace?.Metric("selection.residual", 1);
            trace?.Report(new("backend.hpir.bind.iterator_selection.unavailable", CompilationTrace.UnixNow(), 0, 0,
                CompilationOutcome.Ok, Aggregated: true)
            {
                Attributes = new Dictionary<string, string> { ["reason"] = failure.Message }
            });
            return recipe;
        }
    }

    private static async Task<HpirLocalPath[]> ObserveMembers(NativePathValue directory, string pattern,
        NativeRuntimeContext context, CancellationToken token)
    {
        List<HpirLocalPath> members = [];
        await foreach (var path in NativeHostIO.OpenSelectedPaths(directory, pattern, context, token).ConfigureAwait(false))
            members.Add(new(path.Raw, path.LocalSpelling));
        return members.ToArray();
    }
}
