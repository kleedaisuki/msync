using Msync.Core;

namespace Msync.Runtime;

internal sealed class SessionImage(Module module)
{
    public Module Module { get; private set; } = module;
    public int Generation { get; private set; }
    public Dictionary<int, ScopeValue> Singletons { get; } = [];
    public Task Ordering { get; set; } = Task.CompletedTask;
    public Interpreter? ActiveInterpreter { get; set; }
    private readonly Dictionary<BindingContext, (Module Module, BasicBlock[][] Blocks)> projections = [];

    public (Module Module, BasicBlock[][] Blocks) Project(BindingContext context, Interpreter owner)
    {
        if (!projections.TryGetValue(context, out var projected))
            projections.Add(context, projected = owner.ProjectImage(Module));
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
