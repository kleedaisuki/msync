using Msync.Core;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private readonly Dictionary<int, ScopeLayout> allocationLayouts = [];
    private readonly Dictionary<ScopeInfo, ScopeLayout> joinedLayouts = new(ReferenceEqualityComparer.Instance);

    private int FieldLayoutHint(ScopeInfo scope, string member)
    {
        ScopeLayout? layout;
        if (scope.AllocationSite != 0)
        {
            if (!allocationLayouts.TryGetValue(scope.AllocationSite, out layout))
                allocationLayouts.Add(scope.AllocationSite, layout = new(scope.Members.Keys));
        }
        else if (!joinedLayouts.TryGetValue(scope, out layout))
            joinedLayouts.Add(scope, layout = new(scope.Members.Keys));
        return layout.GetOffset(member) + 1;
    }
}
