using Msync.Core;
using System.Runtime.CompilerServices;

namespace Msync.Runtime;

internal sealed class SessionImage(Module module)
{
    public Module Module { get; private set; } = module;
    public int Generation { get; private set; }
    public Dictionary<int, ScopeValue> Singletons { get; } = [];
    private readonly Dictionary<BindingContext, (Module Module, BasicBlock[][] Blocks)> projections = [];

    public (Module Module, BasicBlock[][] Blocks) Project(BindingContext context)
    {
        if (!projections.TryGetValue(context, out var projected))
            projections.Add(context, projected = Interpreter.ProjectImage(Module, context));
        return projected;
    }

    public void Seed(BindingContext context, Module module, BasicBlock[][] blocks) => projections[context] = (module, blocks);

    public void Publish(Module module)
    {
        Module = module;
        projections.Clear();
        Generation++;
    }
}

public sealed partial class Interpreter
{
    private sealed record PreparedImage(Module Module, BasicBlock[][] Blocks);
    private static readonly ConditionalWeakTable<Module, PreparedImage> preparedImages = new();
    private SessionImage? sessionImage;
    private int imageGeneration;
    private BindingContext? imageContext;
    private BindingContext ImageContext => imageContext ??= new(context.WorkingDirectory, context.HomeDirectory, context.SourceFile);

    internal void Attach(SessionImage image)
    {
        sessionImage = image;
        singletons = image.Singletons;
        imageGeneration = image.Generation;
        image.Seed(ImageContext, module, blocks);
    }

    private void RefreshSessionImage()
    {
        if (sessionImage is not { } image || imageGeneration == image.Generation) return;
        (module, blocks) = image.Project(ImageContext);
        dispatchTables.Clear();
        imageGeneration = image.Generation;
    }

    private (Module Module, BasicBlock[][] Blocks) ProjectImage(Module image) => ProjectImage(image, ImageContext);

    internal static (Module Module, BasicBlock[][] Blocks) ProjectImage(Module image, BindingContext origin)
    {
        // Code follows mutable session identities; physical anchors belong to this invocation's origin.
        var projected = image.Stage == ArtifactStage.Relocatable ? Binding.Bind(image, origin) : image;
        var prepared = preparedImages.GetValue(projected, static module =>
        {
            if (module.Blocks is null) module = Optimizer.FormBlocks(module);
            var indexed = module.Functions.Select(function => new BasicBlock[function.Instructions.Count]).ToArray();
            foreach (var block in module.Blocks!)
                for (var pc = block.Start; pc < block.End; pc++) indexed[block.Function][pc] = block;
            return new(module, indexed);
        });
        return (prepared.Module, prepared.Blocks);
    }
}
