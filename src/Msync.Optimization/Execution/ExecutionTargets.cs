using System.Runtime.CompilerServices;

namespace Msync.Optimization.Execution;

// Process-local executable targets, derived solely from the final bytecode.
public sealed class ExecutionTargets
{
    private const int Limit = 48;
    private static readonly ConditionalWeakTable<Module, ExecutionTargets> images = new();
    private static readonly ConditionalWeakTable<Module, ExecutionTargets> nativeImages = new();
    private readonly Target?[]? predicates;
    private readonly NumericBackedges?[]? numeric;
    private readonly EffectTails?[]? tails;
    private readonly NumberTextSites?[]? numberText;
    private readonly FrameTransport[]? frameTransports;
    private readonly DispatchViews?[]? dispatch;
    private readonly bool[]? directSourceCandidates;
    private readonly GuardedScopeRegion?[]?[]? scopeRegions;
    private readonly ReadonlyScopeFold?[]?[]? scopeFolds;
    private readonly ReadonlyNumberFold?[]?[]? numberFolds;
    private ScopeStorageUses?[]? scopeStorage;

    public const int NumericLoopLimit = 256;
    // These tags belong to the dispatch view, not the bytecode format.
    public const OpCode LoadLocalField = (OpCode)((byte)OpCode.StoreStaticField + 1);
    public const OpCode DirectSourceCall = (OpCode)((byte)LoadLocalField + 1);
    public const OpCode ScopeComparison = (OpCode)((byte)DirectSourceCall + 1);
    public const OpCode ReadonlyScopeFoldEntry = (OpCode)((byte)ScopeComparison + 1);
    public const OpCode ReadonlyNumberFoldEntry = (OpCode)((byte)ReadonlyScopeFoldEntry + 1);
    public const OpCode NativeWorkerCall = (OpCode)((byte)ReadonlyNumberFoldEntry + 1);
    public const OpCode NumericPrimitiveCall = (OpCode)((byte)NativeWorkerCall + 1);
    public const OpCode ArenaPrimitiveCall = (OpCode)((byte)NumericPrimitiveCall + 1);
    public const OpCode LoadWordLocal = (OpCode)((byte)ArenaPrimitiveCall + 1);
    public const OpCode StoreWordLocal = (OpCode)((byte)LoadWordLocal + 1);

    private sealed record DispatchViews(Instruction[]? Bound, Instruction[]? Counted, Instruction[]? Fused,
        DispatchViews? WithWords = null);
    public readonly record struct FrameTransport(int FirstRawPlusOne, bool Words)
    {
        public int FirstNumericLocal => FirstRawPlusOne - 1;
    }

    private ExecutionTargets(Module image, bool directSourceCalls)
    {
        var candidates = directSourceCalls ? NumericSourceAdmission.Find(image) : null;
        bool[]? workers = directSourceCalls ? FindNativeWorkers(image) : null;
        for (int i = 0; i < image.Functions.Count; i++)
        {
            var function = image.Functions[i];
            Instruction[]? fused = null, counted = null;
            ExecutionRegion? graph = null;
            if (NumericLocalAdmission.Find(function))
                (frameTransports ??= new FrameTransport[image.Functions.Count])[i] = new(NumericLocalAdmission.FirstWrite(function) + 1, false);
            if (function.Instructions is Instruction[] code)
            {
                fused = PrepareLocalDispatch(image, i, code, out var regions, out graph, out var numberEntries);
                if (candidates is not null && NumericSourceAdmission.Lower(code, candidates, FirstNumericLocal(i)) is { } direct)
                {
                    directSourceCandidates = candidates;
                    counted = direct;
                    fused = fused is null ? direct : MergeDirectCalls(fused, direct);
                }
                if (regions is not null)
                {
                    (scopeRegions ??= new GuardedScopeRegion?[]?[image.Functions.Count])[i] = regions;
                    counted ??= (Instruction[])code.Clone();
                    for (int pc = 0; pc < regions.Length; pc++)
                        if (regions[pc] is not null) counted[pc] = fused![pc];
                    if (graph is not null && ReadonlyScopeFold.Find(function, code, graph, regions) is { } folds)
                    {
                        (scopeFolds ??= new ReadonlyScopeFold?[]?[image.Functions.Count])[i] = folds;
                        for (int pc = 0; pc < folds.Length; pc++)
                            if (folds[pc] is not null) fused![pc] = new(ReadonlyScopeFoldEntry);
                    }
                }
                if (numberEntries is not null && FindNumberFolds(image, i, code, ref graph, numberEntries) is { } recurrences)
                {
                    (numberFolds ??= new ReadonlyNumberFold?[]?[image.Functions.Count])[i] = recurrences;
                    fused ??= (Instruction[])code.Clone();
                    counted ??= (Instruction[])code.Clone();
                    for (int pc = 0; pc < recurrences.Length; pc++)
                    {
                        if (recurrences[pc] is null) continue;
                        fused[pc] = counted[pc] = new(ReadonlyNumberFoldEntry);
                    }
                }
                if (workers is not null) MarkNativeWorkers(code, workers, ref fused, ref counted);
            }
            var bound = BindScalars(function.Instructions, fused, counted);
            var words = !image.PreserveLocals && bound is not null
                ? WordTransportPlan.Find(image, i, bound, graph) : null;
            DispatchViews? specialized = null;
            if (words is not null)
            {
                specialized = BindWords(bound!, counted, fused, words);
                if (specialized is null) words = null;
                else
                {
                    var frames = frameTransports ??= new FrameTransport[image.Functions.Count];
                    frames[i] = new(words.HasLocals ? NumericLocalAdmission.FirstWrite(function) + 1
                        : frames[i].FirstRawPlusOne, true);
                }
            }
            if (bound is not null || counted is not null || fused is not null)
                (dispatch ??= new DispatchViews?[image.Functions.Count])[i] = new(bound, counted, fused, specialized);
            if (BuildPredicate(function) is { } target)
                (predicates ??= new Target?[image.Functions.Count])[i] = target;
            if (FindNumericBackedges(function.Instructions, image.Constants, words) is { } backedges)
                (numeric ??= new NumericBackedges?[image.Functions.Count])[i] = backedges;
            if (EffectTails.Find(function, image.Constants, words) is { } effectCandidates)
                (tails ??= new EffectTails?[image.Functions.Count])[i] = effectCandidates;
            if (NumberTextSites.Find(function.Instructions, image.Constants) is { } casts)
                (numberText ??= new NumberTextSites?[image.Functions.Count])[i] = casts;
        }
    }

    public static ExecutionTargets For(Module image) => For(image, directSourceCalls: false);
    public static ExecutionTargets For(Module image, bool directSourceCalls) => directSourceCalls
        ? nativeImages.GetValue(image, static image => new(image, directSourceCalls: true))
        : images.GetValue(image, static image => new(image, directSourceCalls: false));
    public FrameTransport Frame(int function) => frameTransports?[function] ?? default;
    public int FirstNumericLocal(int function) => Frame(function).FirstNumericLocal;
    public bool KeepsNumericLocals(int function) => FirstNumericLocal(function) >= 0;
    public Target? Predicate(int function) => predicates?[function];
    public NumericBackedges? Numeric(int function) => numeric?[function];
    public EffectTails? Tails(int function) => tails?[function];
    public NumberTextSites? NumberText(int function) => numberText?[function];
    private DispatchViews? Views(int function, bool words)
    {
        var views = dispatch?[function];
        return words ? views?.WithWords ?? views : views;
    }
    public IReadOnlyList<Instruction>? Dispatch(int function, bool words = true) => Views(function, words) is { } views ? views.Fused ?? views.Bound : null;
    public IReadOnlyList<Instruction>? CountedDispatch(int function, bool words = true) => Views(function, words) is { } views ? views.Counted ?? views.Bound : null;
    public IReadOnlyList<Instruction>? BoundDispatch(int function, bool words = true) => Views(function, words)?.Bound;
    public bool IsDirectSourceCandidate(int function) => directSourceCandidates?[function] == true;
    public GuardedScopeRegion ScopeRegion(int function, int pc) => scopeRegions![function]![pc]!;
    public ReadonlyScopeFold ScopeFold(int function, int pc) => scopeFolds![function]![pc]!;
    public ReadonlyNumberFold NumberFold(int function, int pc) => numberFolds![function]![pc]!;

    public bool TryExecutionWorker(Module image, int function, out IndexedMethodPlan? plan)
    {
        var body = image.Functions[function];
        if (ExecutionScalarWorker.MayPrepare(body)
            && ExecutionScalarWorker.TryPrepare(image, function, out plan, out _)) return true;
        return IndexedMethodPreparation.TryPrepare(image, function, Predicate, out plan, out _, allowPrefix: true);
    }


    private static Instruction[]? BindScalars(IReadOnlyList<Instruction> code,
        Instruction[]? fused, Instruction[]? counted)
    {
        Instruction[]? result = null;
        for (int pc = 0; pc < code.Count; pc++)
        {
            var instruction = code[pc];
            Instruction bound;
            if (instruction.Op == OpCode.NativeCall && instruction.Text is { } name
                && NativeSignatures.TryGet(name, out var signature) && signature.Parameters.Count == instruction.A)
            {
                if (signature.Numeric != NumericIntrinsic.None)
                    bound = new(NumericPrimitiveCall, instruction.A, (int)signature.Numeric);
                else if (signature.Arena is not (ArenaOperation.None or ArenaOperation.New))
                    bound = new(ArenaPrimitiveCall, instruction.A, (int)signature.Arena);
                else continue;
            }
            else continue;
            (result ??= code.ToArray())[pc] = bound;
            if (fused?[pc].Op == instruction.Op) fused[pc] = bound;
            if (counted?[pc].Op == instruction.Op) counted[pc] = bound;
        }
        return result;
    }

    private static DispatchViews? BindWords(Instruction[] bound, Instruction[]? counted, Instruction[]? fused,
        WordTransportPlan words)
    {
        // These whole-span tiers read their own frame places. A scalar
        // variant is not composable if another tier owns that frame span.
        if (words.HasLocals && fused is not null && fused.Any(instruction => instruction.Op is ScopeComparison
            or ReadonlyScopeFoldEntry or ReadonlyNumberFoldEntry)) return null;
        Instruction[]? wordBound = null, wordCounted = null, wordFused = null;
        for (int pc = 0; pc < bound.Length; pc++)
        {
            int local = words.LocalAt(pc);
            int lanes = (int)words.At(pc);
            if (local < 0 && lanes == 0) continue;
            var original = bound[pc];
            // Do not compose an opaque fused entry with a changed stack/local
            // representation. Both overlays must preserve the proved use.
            if (counted is not null && counted[pc].Op != original.Op
                || fused is not null && fused[pc].Op != original.Op) return null;
            var lowered = local < 0 ? original with { B = original.B | (lanes << 8) }
                : new Instruction(original.Op == OpCode.LoadLocal ? LoadWordLocal : StoreWordLocal, local, lanes);
            (wordBound ??= (Instruction[])bound.Clone())[pc] = lowered;
            if (counted is not null) (wordCounted ??= (Instruction[])counted.Clone())[pc] = lowered;
            if (fused is not null) (wordFused ??= (Instruction[])fused.Clone())[pc] = lowered;
        }
        return wordBound is null ? null : new(wordBound, wordCounted, wordFused);
    }

    private static bool[]? FindNativeWorkers(Module image)
    {
        bool[]? candidates = null;
        for (int i = 0; i < image.Functions.Count; i++)
            if (IndexedMethodPreparation.MayPrepare(image.Functions[i], includeEffectFrontier: true)
                || ExecutionScalarWorker.MayPrepare(image.Functions[i]))
                (candidates ??= new bool[image.Functions.Count])[i] = true;
        return candidates;
    }

    private static void MarkNativeWorkers(Instruction[] code, bool[] workers, ref Instruction[]? fused, ref Instruction[]? counted)
    {
        for (int pc = 0; pc < code.Length; pc++)
        {
            if (code[pc] is not { Op: OpCode.Call, B: > 0, Text: null } call
                || call.B > workers.Length || !workers[call.B - 1]) continue;
            fused ??= (Instruction[])code.Clone();
            counted ??= (Instruction[])code.Clone();
            fused[pc] = counted[pc] = new(NativeWorkerCall);
        }
    }

    private static ReadonlyNumberFold?[]? FindNumberFolds(Module image, int function, Instruction[] code,
        ref ExecutionRegion? graph, List<int> entries)
    {
        ReadonlyNumberFold?[]? plans = null;
        foreach (int pc in entries)
        {
            if (graph is null && !ExecutionRegionImporter.TryImport(image, function, out graph, out _)) return null;
            if (ReadonlyNumberFold.Find(image, image.Functions[function], code, graph, pc) is { } plan)
                (plans ??= new ReadonlyNumberFold?[code.Length])[pc] = plan;
        }
        return plans;
    }

    private static Instruction[] MergeDirectCalls(Instruction[] fused, Instruction[] direct)
    {
        for (int pc = 0; pc < direct.Length; pc++)
            if (direct[pc].Op == DirectSourceCall) fused[pc] = direct[pc];
        return fused;
    }

    public bool UsesScopeStorage(Module image, int function, int pc)
    {
        var functions = Volatile.Read(ref scopeStorage) ?? InitializeScopeStorage(image.Functions.Count);
        var sites = Volatile.Read(ref functions[function]);
        if (sites is null)
        {
            sites = new(image, image.Functions[function].Instructions);
            Volatile.Write(ref functions[function], sites);
        }
        return sites.Contains(image, function, pc);
    }

    private ScopeStorageUses?[] InitializeScopeStorage(int count)
    {
        var functions = new ScopeStorageUses?[count];
        return Interlocked.CompareExchange(ref scopeStorage, functions, null) ?? functions;
    }

    private static Instruction[]? PrepareLocalDispatch(Module image, int function, Instruction[] code,
        out GuardedScopeRegion?[]? regions, out ExecutionRegion? graph, out List<int>? numberEntries)
    {
        regions = null;
        graph = null;
        numberEntries = null;
        Instruction[]? fused = null;
        bool attemptedGraph = false;
        for (int pc = 0; pc + 1 < code.Length; pc++)
        {
            var load = code[pc];
            if (load.Op != OpCode.LoadLocal)
            {
                if (load.Op == OpCode.GetIterator && !image.PreserveLocals && ReadonlyNumberFold.MayStart(code, pc))
                    (numberEntries ??= []).Add(pc);
                continue;
            }
            var member = code[pc + 1];
            if (member.Op != OpCode.LoadField) continue;
            // Preserve LoadField so incoming edges to its PC still work.
            if (!image.PreserveLocals && GuardedScopeRegion.Match(code.AsSpan(pc)) is { } region)
            {
                int branch = pc + GuardedScopeRegion.Width;
                region = region.FollowBranch(code, branch);
                if (region.Exit is { } exit)
                {
                    int crossing = exit.OnTrue ? branch + 1 : exit.Target;
                    byte writes = exit.OnTrue ? exit.FallthroughWrites : exit.TakenWrites;
                    if (writes == 3 && ScopeCrossingArm.MayStart(code, crossing, region.Receiver))
                    {
                        if (!attemptedGraph)
                        {
                            attemptedGraph = true;
                            ExecutionRegionImporter.TryImport(image, function, out graph, out _);
                        }
                        if (graph is not null && ScopeCrossingArm.Build(image, graph, crossing, region, code) is { } arm)
                            region = region with { Crossing = arm };
                    }
                }
                (regions ??= new GuardedScopeRegion?[code.Length])[pc] = region;
                (fused ??= (Instruction[])code.Clone())[pc] = new(ScopeComparison);
            }
            else if (image.FieldProjections[member.A].Cases.Count != 0)
                (fused ??= (Instruction[])code.Clone())[pc] = new(LoadLocalField, load.A, member.A);
        }
        return fused;
    }

    public sealed class NumericBackedges
    {
        private readonly bool[] sites;
        internal NumericBackedges(bool[] sites) => this.sites = sites;
        public bool Contains(int pc) => sites[pc];
    }

    public sealed class NumberTextSites
    {
        private readonly bool[] sites;
        private NumberTextSites(bool[] sites) => this.sites = sites;
        public bool Contains(int pc) => sites[pc];

        internal static NumberTextSites? Find(IReadOnlyList<Instruction> code, IReadOnlyList<Value> constants)
        {
            bool[]? sites = null;
            int count = 0;
            for (int pc = 1; pc < code.Count; pc++)
            {
                if (code[pc] is not { Op: OpCode.NativeCall, A: 2, Text: "cast" }
                    || code[pc - 1] is not { Op: OpCode.Constant, A: var target }
                    || (uint)target >= (uint)constants.Count
                    || constants[target] is not TypeValue type || type.Data != TypeRef.Number) continue;
                (sites ??= new bool[code.Count])[pc] = true;
                count++;
            }
            if (sites is null) return null;

            // The immediately preceding Constant proves the target only on
            // fallthrough. Any incoming explicit edge invalidates the site.
            foreach (Instruction instruction in code)
            {
                int target = instruction.Op switch
                {
                    OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.JumpIfStaticReady => instruction.A,
                    OpCode.IteratorNext => instruction.B,
                    _ => -1
                };
                if ((uint)target >= (uint)sites.Length || !sites[target]) continue;
                sites[target] = false;
                if (--count == 0) return null;
            }
            return new(sites);
        }
    }

    public sealed class EffectTails
    {
        private const int MaxLength = 64;
        private const int MinArithmetic = 3;
        public readonly record struct Site(int End, int CaptureProbe, bool WritesCapture);
        private readonly Site[] sites;
        private EffectTails(Site[] sites) => this.sites = sites;
        public Site After(int pc) => sites[pc];

        internal static EffectTails? Find(FunctionCode function, IReadOnlyList<Value> constants, WordTransportPlan? words)
        {
            var code = function.Instructions;
            Site[]? sites = null;
            for (int effect = 0; effect + 1 < code.Count; effect++)
            {
                if (code[effect].Op is not (OpCode.Call or OpCode.LoadField or OpCode.StoreField or OpCode.LoadStaticField or OpCode.StoreStaticField or OpCode.NativeCall))
                    continue;
                if ((words?.At(effect) & WordTransport.Result) == WordTransport.Result) continue;
                var site = FindEnd(code, constants, function.CaptureCount, effect + 1, words);
                if (site.End == 0) continue;
                (sites ??= new Site[code.Count])[effect] = site;
            }
            return sites is null ? null : new(sites);
        }

        private static Site FindEnd(IReadOnlyList<Instruction> code, IReadOnlyList<Value> constants, int captures, int first,
            WordTransportPlan? words)
        {
            int work = 0, probe = -1;
            bool writesProbe = false;
            Site site = default;
            for (int pc = first; pc < code.Count && pc - first < MaxLength; pc++)
            {
                if (words?.LocalAt(pc) >= 0) break;
                var instruction = code[pc];
                switch (instruction.Op)
                {
                    case OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Negate:
                        work++;
                        site = new(pc, probe, writesProbe);
                        break;
                    case OpCode.StoreLocal or OpCode.InitializeLocal:
                        if (probe < 0 && (uint)instruction.A < (uint)captures) probe = instruction.A;
                        if (probe == instruction.A) writesProbe = true;
                        if (work != 0) site = new(pc, probe, writesProbe);
                        break;
                    case OpCode.LoadLocal:
                        if (probe < 0 && (uint)instruction.A < (uint)captures) probe = instruction.A;
                        break;
                    case OpCode.Pop or OpCode.Dup or OpCode.Nop:
                    case OpCode.Constant when constants[instruction.A] is NumberValue:
                        break;
                    default:
                        return work >= MinArithmetic ? site : default;
                }
            }
            return work >= MinArithmetic ? site : default;
        }
    }

    private static NumericBackedges? FindNumericBackedges(IReadOnlyList<Instruction> code, IReadOnlyList<Value> constants,
        WordTransportPlan? words)
    {
        int incompatible = -1;
        int store = -1;
        bool[]? sites = null;
        for (int pc = 0; pc < code.Count; pc++)
        {
            var i = code[pc];
            if (i.Op is OpCode.StoreLocal or OpCode.InitializeLocal) store = pc;
            if (words?.LocalAt(pc) >= 0 || !NumericInstruction(i, constants)) incompatible = pc;
            // An inexpensive necessary condition, not the importer's CFG/stack proof.
            if (i.Op == OpCode.Jump && i.A < pc && pc - i.A < NumericLoopLimit
                && i.A > incompatible && i.A <= store)
                (sites ??= new bool[code.Count])[pc] = true;
        }
        return sites is null ? null : new(sites);
    }

    private static bool NumericInstruction(Instruction i, IReadOnlyList<Value> constants) => i.Op switch
    {
        OpCode.Constant => constants[i.A] is NumberValue,
        OpCode.Nop or OpCode.Dup or OpCode.Pop or OpCode.LoadLocal or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.Negate or OpCode.Add or OpCode.Subtract or OpCode.Multiply
            or OpCode.Divide or OpCode.Remainder or OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual
            or OpCode.Greater or OpCode.GreaterEqual or OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse => true,
        _ => false
    };

    public enum Relation : byte { Less, Equal, Greater, LeftNaN, RightNaN, BothNaN }
    private enum Term : byte { Left, Right, False, True, Null }

    public sealed class Target
    {
        private readonly byte[][] paths;

        internal Target(bool numeric, int mask, byte[][] paths)
        {
            Numeric = numeric;
            Mask = mask;
            this.paths = paths;
        }

        public bool Numeric { get; }
        public int Mask { get; }
        public ReadOnlySpan<byte> Path(Relation relation) => paths[(int)relation];
        public bool Test(Relation relation) => (Mask & (1 << (int)relation)) != 0;
    }

    internal static Target? BuildPredicate(FunctionCode function)
    {
        if (function is not { ParameterCount: 2, CaptureCount: 0, ParameterTypes.Count: 2 }
            || function.ReturnType != TypeRef.Bool || function.Instructions.Count is 0 or > Limit)
            return null;
        var types = function.ParameterTypes;
        bool numeric = types[0].WithoutNull == TypeRef.Number && types[1].WithoutNull == TypeRef.Number;
        if (!numeric && (!Text(types[0].WithoutNull) || !Text(types[1].WithoutNull))) return null;

        var paths = new byte[numeric ? 6 : 3][];
        Span<Term> stack = stackalloc Term[Limit];
        Span<byte> path = stackalloc byte[Limit];
        int mask = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            if (!Evaluate(function.Instructions, (Relation)i, stack, path, out int count, out bool value)) return null;
            paths[i] = path[..count].ToArray();
            if (value) mask |= 1 << i;
        }
        return new(numeric, mask, paths);
    }

    private static bool Text(TypeRef type) => type == TypeRef.String || type == TypeRef.Path;
    private static Term Bool(bool result) => result ? Term.True : Term.False;
    private static bool IsBool(Term term) => term is Term.True or Term.False;

    private static bool Evaluate(IReadOnlyList<Instruction> code, Relation relation, Span<Term> stack,
        Span<byte> path, out int count, out bool result)
    {
        count = 0;
        result = false;
        int height = 0;
        for (int pc = 0; pc < code.Count; pc++)
        {
            var i = code[pc];
            path[count++] = (byte)pc;
            switch (i.Op)
            {
                case OpCode.Nop: break;
                case OpCode.LoadLocal when i.A is 0 or 1: stack[height++] = (Term)i.A; break;
                case OpCode.True: stack[height++] = Term.True; break;
                case OpCode.False: stack[height++] = Term.False; break;
                case OpCode.Null: stack[height++] = Term.Null; break;
                case OpCode.Dup when height != 0: stack[height] = stack[height - 1]; height++; break;
                case OpCode.Pop when height != 0: height--; break;
                case OpCode.IsNull when height != 0: stack[height - 1] = Bool(stack[height - 1] == Term.Null); break;
                case OpCode.Not when height != 0 && IsBool(stack[height - 1]):
                    stack[height - 1] = Bool(stack[height - 1] == Term.False); break;
                case OpCode.Jump when i.A > pc && i.A < code.Count: pc = i.A - 1; break;
                case OpCode.JumpIfTrue or OpCode.JumpIfFalse when i.A > pc && i.A < code.Count
                    && height != 0 && IsBool(stack[height - 1]):
                    if ((stack[--height] == Term.True) == (i.Op == OpCode.JumpIfTrue)) pc = i.A - 1;
                    break;
                case OpCode.Return when height != 0 && IsBool(stack[height - 1]):
                    result = stack[height - 1] == Term.True;
                    return true;
                case OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual or OpCode.Equal or OpCode.NotEqual
                    when height >= 2 && stack[height - 2] <= Term.Right && stack[height - 1] <= Term.Right:
                    var right = stack[--height];
                    stack[height - 1] = Bool(Compare(i.Op, relation, stack[height - 1], right));
                    break;
                default: return false;
            }
        }
        return false;
    }

    private static bool Compare(OpCode op, Relation relation, Term left, Term right)
    {
        if (IsNaN(relation, left) || IsNaN(relation, right)) return op == OpCode.NotEqual;
        int order = left == right ? 0 : relation switch { Relation.Less => -1, Relation.Greater => 1, _ => 0 };
        if (left == Term.Right) order = -order;
        return op switch
        {
            OpCode.Less => order < 0, OpCode.LessEqual => order <= 0,
            OpCode.Greater => order > 0, OpCode.GreaterEqual => order >= 0,
            OpCode.Equal => order == 0, _ => order != 0
        };
    }

    private static bool IsNaN(Relation relation, Term term) =>
        relation == Relation.BothNaN || relation == (term == Term.Left ? Relation.LeftNaN : Relation.RightNaN);
}
