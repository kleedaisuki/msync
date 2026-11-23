namespace Msync.Core.Hpir;

public static partial class HpirArtifact
{
    private static void ValidateStage(HpirProgram program)
    {
        if (program.OptimizationLevel is < -1 or > 3) throw new MsyncException("Invalid HPIR optimization level");
        foreach (var check in program.ContextChecks)
            if (check.Operation is < HpirFileMetadata.Exists or > HpirFileMetadata.IsReadonly
                || program.Stage == HpirStage.Relocatable && check.Value is not null)
                throw new MsyncException("Invalid HPIR contextual check", check.Source);
        if (program.Stage == HpirStage.Relocatable && program.IteratorSelections.Any(selection => selection.Members is not null))
            throw new MsyncException("Unbound HPIR iterator selection contains frozen members");
        switch (program.Stage)
        {
            case HpirStage.Relocatable when program.PreparedTransfers.Count == 0 && program.Context is null:
                foreach (var input in program.TransferInputs)
                {
                    bool valid = input.Kind switch
                    {
                        HpirTransferSource.Path => input.Path.HasValue && input.Text is null && input.Pattern is null,
                        HpirTransferSource.Text => !input.Path.HasValue && input.Text is not null && input.Pattern is null,
                        HpirTransferSource.Selection => input.Path.HasValue && input.Text is null && input.Pattern is not null,
                        _ => false
                    };
                    if (!valid) throw new MsyncException("Invalid HPIR transfer input", input.Source);
                }
                return;
            case HpirStage.Executable when program.TransferInputs.Count == 0:
                for (int id = 0; id < program.PreparedTransfers.Count; ++id)
                {
                    var plan = program.PreparedTransfers[id];
                    bool valid = plan.Kind switch
                    {
                        HpirTransferSource.Path => plan.Paths.Count == 1 && plan.Text is null && plan.Pattern is null,
                        HpirTransferSource.Text => plan.Paths.Count == 0 && plan.Text is not null && plan.Pattern is null,
                        HpirTransferSource.Selection => plan.Text is null && (plan.Pattern is null || plan.Paths.Count == 1),
                        _ => false
                    };
                    if (!valid) throw new MsyncException("Invalid HPIR prepared transfer", plan.Source);
                    int previous = -1;
                    foreach (int predecessor in plan.Predecessors)
                    {
                        if (predecessor <= previous || predecessor >= id)
                            throw new MsyncException("Invalid HPIR transfer dependency order", plan.Source);
                        previous = predecessor;
                    }
                }
                return;
            default:
                throw new MsyncException("HPIR artifact stage and effect tables disagree");
        }
    }
}
