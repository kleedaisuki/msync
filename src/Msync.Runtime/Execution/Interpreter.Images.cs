using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

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
        (module, blocks) = image.Project(ImageContext, this);
        dispatchTables.Clear();
        imageGeneration = image.Generation;
    }

    internal (Module Module, BasicBlock[][] Blocks) ProjectImage(Module image)
    {
        var projected = image.Stage == ArtifactStage.Relocatable ? Binding.Bind(image, ImageContext) : image;
        var prepared = preparedImages.GetValue(projected, static module =>
        {
            if (module.Blocks is null) module = BlockBuilder.Form(module);
            var indexed = module.Functions.Select(function => new BasicBlock[function.Instructions.Count]).ToArray();
            foreach (var block in module.Blocks!)
                for (var pc = block.Start; pc < block.End; pc++) indexed[block.Function][pc] = block;
            return new(module, indexed);
        });
        return (prepared.Module, prepared.Blocks);
    }
}
