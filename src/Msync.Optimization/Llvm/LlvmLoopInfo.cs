// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/GenericLoopInfo.h,
// Support/GenericLoopInfoImpl.h static analyzeImpl and preorder queries.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmLoop
{
    private readonly LlvmLoopInfo owner;
    internal readonly List<LlvmLoop> Children = [];
    internal int BlockOffset, BlockLength, Cursor;
    private LlvmBasicBlock? pendingHeader;
    private LlvmBasicBlock[] blockData = [];
    private IReadOnlyList<LlvmBasicBlock> blocks = Array.Empty<LlvmBasicBlock>();
    private uint blockCapacity;
    private const uint BorrowedCapacity = uint.MaxValue;
    public bool IsInvalid { get; private set; }
    internal LlvmLoop(LlvmLoopInfo owner, LlvmBasicBlock? header = null, LlvmLoop? parent = null)
    {
        this.owner = owner;
        pendingHeader = header;
        ParentLoop = parent;
        SubLoops = Children.AsReadOnly();
    }
    public LlvmBasicBlock Header
    {
        get
        {
            Debug.Assert(!IsInvalid && BlockLength != 0);
            return blockData[BlockOffset];
        }
    }
    internal LlvmBasicBlock PendingHeader => pendingHeader!;
    public LlvmLoop? ParentLoop { get; private set; }
    public IReadOnlyList<LlvmLoop> SubLoops { get; }
    public IReadOnlyList<LlvmBasicBlock> Blocks => blocks;
    public int BlockCount => BlockLength;
    public bool IsInnermost => Children.Count == 0;
    public bool IsOutermost => ParentLoop is null;
    public int LoopDepth
    {
        get
        {
            int depth = 1;
            for (LlvmLoop? loop = ParentLoop; loop is not null; loop = loop.ParentLoop) ++depth;
            return depth;
        }
    }
    public LlvmLoop OutermostLoop
    {
        get { LlvmLoop loop = this; while (loop.ParentLoop is { } parent) loop = parent; return loop; }
    }
    public bool Contains(LlvmLoop? loop)
    {
        Debug.Assert(!IsInvalid);
        for (; loop is not null; loop = loop.ParentLoop)
            if (ReferenceEquals(loop, this)) return true;
        return false;
    }
    public bool Contains(LlvmBasicBlock block) => ReferenceEquals(block.Parent, owner.Function) && Contains(owner.GetLoopFor(block));
}

public sealed partial class LlvmLoopInfo
{
    private readonly List<LlvmLoop> roots = [];
    private LlvmLoop?[] blockMap = [];
    private readonly CompilationTrace? telemetry;
    private readonly Dictionary<string, ulong>? mutationMetrics;
    public LlvmFunction Function { get; }
    public IReadOnlyList<LlvmLoop> TopLevelLoops { get; }

    public LlvmLoopInfo(LlvmFunction function, LlvmDominatorTree dominators, CompilationTrace? trace = null)
    {
        Debug.Assert(ReferenceEquals(function, dominators.Function) && !dominators.IsPostDominator);
        Function = function;
        TopLevelLoops = roots.AsReadOnly();
        trace = trace?.Detail;
        telemetry = trace;
        mutationMetrics = trace is null ? null : [];
        var work = new Discovery(this, dominators, trace);
        if (trace is null) work.Run();
        else trace.Run("optimizer.analysis.llvm-loop-info", work, static state => state.Run(),
            new Dictionary<string, string> { ["function.name"] = function.Name });
    }

    public LlvmLoop? GetLoopFor(LlvmBasicBlock block)
    {
        Debug.Assert(ReferenceEquals(block.Parent, Function));
        return block.Number < blockMap.Length ? blockMap[(int)block.Number] : null;
    }
    public int GetLoopDepth(LlvmBasicBlock block) => GetLoopFor(block)?.LoopDepth ?? 0;
    public bool IsLoopHeader(LlvmBasicBlock block) => ReferenceEquals(GetLoopFor(block)?.Header, block);
    public IReadOnlyList<LlvmLoop> GetLoopsInPreorder()
    {
        var result = new List<LlvmLoop>();
        var pending = new Stack<LlvmLoop>(roots);
        while (pending.TryPop(out LlvmLoop? loop))
        {
            result.Add(loop);
            for (int index = loop.Children.Count - 1; index >= 0; --index) pending.Push(loop.Children[index]);
        }
        return result;
    }
}
