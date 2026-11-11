using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private SessionImage? sessionImage;
    private int imageGeneration;
    private BindingContext? imageContext;
    private BindingContext ImageContext => imageContext ??= new(context.WorkingDirectory, context.HomeDirectory, context.SourceFile);

    internal void Attach(SessionImage image)
    {
        sessionImage = image;
        singletons = image.Singletons;
        imageGeneration = image.Generation;
        image.Seed(ImageContext, module);
    }

    private void RefreshSessionImage()
    {
        if (sessionImage is not { } image || imageGeneration == image.Generation) return;
        module = image.Project(ImageContext);
        dispatchTables.Clear();
        imageGeneration = image.Generation;
    }

}
