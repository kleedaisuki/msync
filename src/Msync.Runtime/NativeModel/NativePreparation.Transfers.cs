using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

internal sealed partial class NativePreparationState
{
    private bool Transfer(NativeInstruction instruction)
    {
        if (!Known(instruction.A, out NativeValue source) || !Known(instruction.B, out NativeValue target)) return false;
        NativePathValue destination = context.AsPath(target);
        if (source.Kind == NativeKind.Range && source.AsRange().Selection?.Directory.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl) return false;
        if (source.Kind == NativeKind.Range) FreezeInputs(source.AsRange());
        if (Portable(source) is not { } portable) return false;
        if (source.Kind is not (NativeKind.String or NativeKind.Path or NativeKind.Range)) return false;
        // Remote finite work keeps one transport owner for metadata and payload.
        // Do not authenticate here merely to classify a transfer.
        if (destination.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
            || source.Kind == NativeKind.Path && source.AsPath().Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
            || source.Kind == NativeKind.Range && portable.Items!.Any(item => item.Path?.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl)) return false;
        if (NamespaceChanged(destination)) return false;
        bool observed = Demanded(instruction.Result);
        bool container = destination.Raw.EndsWith('/') || OperatingSystem.IsWindows() && destination.Raw.EndsWith('\\');
        bool replace = destination.Raw.TrimEnd('/', '\\').EndsWith("/.", StringComparison.Ordinal)
            || OperatingSystem.IsWindows() && destination.Raw.TrimEnd('/', '\\').EndsWith("\\.", StringComparison.Ordinal);
        if (replace) destination = destination.WithRaw(destination.Raw.TrimEnd('/', '\\')[..^1].TrimEnd('/', '\\'));
        TransferDestinationMode mode = replace ? TransferDestinationMode.Replace : TransferDestinationMode.Exact;
        List<PlanResource> reads = [], writes = [];
        PlannedTransferKind kind = PlannedTransferKind.GenericCopy;
        bool complete = false;
        if (source.Kind != NativeKind.Range)
        {
            if (container && !replace)
            {
                string name = source.Kind == NativeKind.Path ? Path.GetFileName(context.LocalPath(source.AsPath()).TrimEnd('/', '\\')) : "";
                if (name.Length == 0) return false;
                destination = NativeBuiltins.JoinPath(destination, NativeValue.FromString(name));
            }
            if (source.Kind == NativeKind.String) { kind = PlannedTransferKind.WriteText; complete = true; }
            else
            {
                NativePathValue path = source.AsPath();
                if (NamespaceChanged(path) || graph.HasWrites(Resource(path, true))) return false;
                string status = Status(path);
                kind = status switch
                {
                    "regular" => PlannedTransferKind.CopyFile,
                    "directory" => PlannedTransferKind.CopyDirectory,
                    "symlink" => PlannedTransferKind.CopyLink,
                    _ => PlannedTransferKind.GenericCopy
                };
                bool directory = kind == PlannedTransferKind.CopyDirectory;
                if (directory && HasNestedLinks(context.LocalPath(path))) return false;
                reads.Add(Resource(path, directory, followLeaf: false));
                reads.AddRange(NamespaceResources(path));
                complete = kind is PlannedTransferKind.CopyFile or PlannedTransferKind.CopyLink || replace;
                if (directory && !replace)
                {
                    mode = container ? TransferDestinationMode.Merge : TransferDestinationMode.Exact;
                    // Nested destination links can write outside the nominal root.
                    // Keep the original operation when a finite footprint cannot be proven.
                    if (HasNestedLinks(context.LocalPath(destination))) return false;
                }
            }
            reads.AddRange(NamespaceResources(destination, kind == PlannedTransferKind.CopyDirectory && !replace));
            writes.Add(Resource(destination, kind == PlannedTransferKind.CopyDirectory || replace, kind == PlannedTransferKind.CopyDirectory && !replace));
        }
        else
        {
            if (!container && !replace) return false;
            foreach (NativePlanValue item in portable.Items!)
            {
                if (item.Path is not { } path) return false;
                if (NamespaceChanged(path) || graph.HasWrites(Resource(path, true))) return false;
                bool directory = Status(path) == "directory";
                reads.Add(Resource(path, directory));
                reads.AddRange(NamespaceResources(path));
                string name = Path.GetFileName(context.LocalPath(path).TrimEnd('/', '\\'));
                NativePathValue child = NativeBuiltins.JoinPath(destination, NativeValue.FromString(name));
                if (directory && (HasNestedLinks(context.LocalPath(path)) || HasNestedLinks(context.LocalPath(child)))) return false;
                reads.AddRange(NamespaceResources(child, directory));
                writes.Add(Resource(child, directory, directory && !replace));
            }
            reads.AddRange(NamespaceResources(destination, !replace));
            if (replace) { writes.Clear(); writes.Add(Resource(destination, true)); complete = true; }
        }
        int node = graph.Add(new(portable, destination, kind, reads.Distinct(ResourceIdentity.Instance).ToArray(),
            writes.Distinct(ResourceIdentity.Instance).ToArray(), instruction.Source, complete, observed, pc, mode));
        transfers.Add((pc, node, instruction));
        frame.Drop(instruction.Result);
        return true;
    }

    private bool NamespaceChanged(NativePathValue path)
    {
        string? parent = Path.GetDirectoryName(context.LocalPath(path));
        while (!string.IsNullOrEmpty(parent))
        {
            if (graph.HasAncestorWrites(Resource(new(parent)))
                || graph.HasAncestorWrites(new("local", Path.GetFullPath(parent)))) return true;
            parent = Path.GetDirectoryName(parent);
        }
        return false;
    }

    private bool Demanded(int result)
    {
        if (result < 0) return false;
        if (function.Slots[result].Role != NativeSlotRole.Temporary || function.Slots[result].Escapes
            || program.SessionRoots?.Contains(result) == true) return true;
        if (function.Instructions.Take(pc).Any(instruction => instruction.A == result
            && instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation)) return true;
        foreach (NativeInstruction instruction in function.Instructions.Skip(pc + 1))
        {
            if (instruction.Op == NativeOp.Drop && instruction.A == result) continue;
            if (NativeInstructionFacts.ValueReads(instruction).Contains(result)) return true;
            if (instruction.Result == result) return false;
        }
        return false;
    }

    private string Status(NativePathValue path)
    {
        string name = context.LocalPath(path);
        try
        {
            FileAttributes attributes = File.GetAttributes(name);
            return NativeFileMetadata.IsLink(name, attributes) ? "symlink" : (attributes & FileAttributes.Directory) != 0 ? "directory" : "regular";
        }
        catch (FileNotFoundException) { return "not_found"; }
        catch (DirectoryNotFoundException) { return "not_found"; }
    }

    private PlanResource Resource(NativePathValue path, bool subtree = false, bool followLeaf = false)
    {
        string local = context.LocalPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (local.Length == 0) local = Path.GetPathRoot(context.WorkingDirectory)!;
        string? parent = Path.GetDirectoryName(local);
        string physical = followLeaf || string.IsNullOrEmpty(parent) ? Resolve(local)
            : Path.GetFullPath(Path.Combine(Resolve(parent), Path.GetFileName(local)));
        return new("local", physical, subtree);
    }

    private string Resolve(string path)
    {
        if (canonicalPaths.TryGetValue(path, out string? resolved)) return resolved;
        resolved = ResourcePath.Resolve(path);
        canonicalPaths.Add(path, resolved);
        return resolved;
    }

    private IEnumerable<PlanResource> NamespaceResources(NativePathValue path, bool followLeaf = false)
    {
        string? parent = followLeaf ? context.LocalPath(path) : Path.GetDirectoryName(context.LocalPath(path));
        while (!string.IsNullOrEmpty(parent))
        {
            if (Status(new(parent)) == "symlink") yield return Resource(new(parent));
            parent = Path.GetDirectoryName(parent);
        }
    }

    private static bool HasNestedLinks(string root)
    {
        if (!Directory.Exists(root)) return false;
        Stack<string> pending = new(); pending.Push(root);
        while (pending.TryPop(out string? directory))
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(child);
                if (NativeFileMetadata.IsLink(child, attributes)) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
            }
        return false;
    }

    private void Flush()
    {
        if (transfers.Count == 0) return;
        int index = plans.Count;
        NativeExecutionPlan plan = graph.Build(program.EntryPoint, transfers[0].Pc, transfers[^1].Pc + 1, out var identities);
        plans.Add(plan);
        foreach (var site in transfers)
        {
            List<NativeInstruction> output = [];
            if (site.Pc == transfers[0].Pc) output.Add(new(NativeOp.ExecutePlan, A: index, Source: site.Instruction.Source));
            if (identities.TryGetValue(site.Node, out int selected) && graph.Nodes[site.Node].Observed)
                output.Add(new(NativeOp.PlanResult, site.Instruction.Result, A: index, B: selected, Source: site.Instruction.Source));
            else if (site.Instruction.Result >= 0)
            {
                int constant = constants.Count;
                constants.Add(new(NativeKind.Bool, Boolean: true));
                output.Add(new(NativeOp.Constant, site.Instruction.Result, A: constant,
                    Type: function.Slots[site.Instruction.Result].Type, Source: site.Instruction.Source));
            }
            replacements[site.Pc] = output.ToArray();
        }
        graph = new(); transfers.Clear();
    }

    private NativeProgram Rewrite()
    {
        IReadOnlyList<NativeInstruction> code = function.Instructions;
        int[] positions = new int[code.Count + 1];
        List<NativeInstruction> output = [];
        for (int index = 0; index < code.Count; index++)
        {
            positions[index] = output.Count;
            if (replacements.TryGetValue(index, out NativeInstruction[]? rewritten)) output.AddRange(rewritten);
            else output.Add(code[index]);
        }
        positions[^1] = output.Count;
        for (int index = 0; index < output.Count; index++)
        {
            NativeInstruction instruction = output[index];
            output[index] = instruction.Op switch
            {
                NativeOp.Jump => instruction with { A = positions[instruction.A] },
                NativeOp.Branch => instruction with { B = positions[instruction.B], C = positions[instruction.C] },
                NativeOp.NextIterator => instruction with { B = positions[instruction.B] },
                _ => instruction
            };
        }
        NativeFunctionCode[] functions = program.Functions.ToArray();
        functions[program.EntryPoint] = function with { Instructions = output, Slots = slots };
        return program with
        {
            Constants = constants,
            Functions = functions,
            Context = new(context.WorkingDirectory, context.HomeDirectory, context.SourceFile),
            Stage = ArtifactStage.Executable,
            ExecutionPlans = plans.Select(plan => plan with { Start = positions[plan.Start], End = positions[plan.End] }).ToArray(),
            Allocations = program.Allocations.Select(plan => plan.Function == program.EntryPoint ? plan with { Instruction = positions[plan.Instruction] } : plan).ToArray()
        };
    }
}
