using Msync.Core;

namespace Msync.Runtime;

internal sealed class SessionImage(Module module)
{
    public Module Module { get; private set; } = module;
    public int Generation { get; private set; }
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
    private SessionImage? sessionImage;
    private int imageGeneration;
    private BindingContext? imageContext;
    private BindingContext ImageContext => imageContext ??= new(context.WorkingDirectory, context.HomeDirectory, context.SourceFile);

    internal void Attach(SessionImage image)
    {
        sessionImage = image;
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
        if (projected.Blocks is null) projected = Optimizer.FormBlocks(projected);
        var indexed = projected.Functions.Select(function => new BasicBlock[function.Instructions.Count]).ToArray();
        foreach (var block in projected.Blocks!)
            for (var pc = block.Start; pc < block.End; pc++) indexed[block.Function][pc] = block;
        return (projected, indexed);
    }
}
