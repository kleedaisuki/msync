namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticContext
{
    internal void DetachFacts()
    {
        SingletonWriteFacts.Detach();
        SingletonSchemas.Detach();
        Locations.Detach();
        CallableContracts.Detach();
        ClosedCallRequirements.Detach();
        Definitions.Detach();
        Values.Detach();
        Storage.Detach();
        Objects.Detach();
        Indexed.Detach();
        AliasViews.Detach();
        MemberContracts.Detach();
        Substitutions.Detach();
    }
}
