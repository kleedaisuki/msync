using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private ConditionalWeakTable<Module, Dictionary<int, NumericLoopSites>>? numericLoopImages;

    private NumericLoopSites GetNumericLoopSites(Module image, int functionIndex)
    {
        var sites = (numericLoopImages ??= new()).GetOrCreateValue(image);
        if (!sites.TryGetValue(functionIndex, out var function))
            sites.Add(functionIndex, function = new(image, functionIndex));
        return function;
    }

    private SessionImage? sessionImage;
    private int imageGeneration;
    private BindingContext? imageContext;
    private BindingContext ImageContext => imageContext ??= new(context.WorkingDirectory, context.HomeDirectory, context.SourceFile);

    internal void Attach(SessionImage image)
    {
        sessionImage = image;
        imageGeneration = image.Generation;
        image.Seed(ImageContext, module);
    }

    private void RefreshSessionImage()
    {
        if (sessionImage is not { } image || imageGeneration == image.Generation) return;
        module = image.Project(ImageContext);
        scopePlaces = null;
        executionTargets = ExecutionTargets.For(module);
        dispatchTables.Clear();
        imageGeneration = image.Generation;
    }

}
