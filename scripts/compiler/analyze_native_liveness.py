"""Replay NativeRegisterPlan liveness work over rendered artifact JSON files."""

from __future__ import annotations
import argparse
import json
import hashlib
from collections import Counter, deque
from pathlib import Path
from typing import Any

READS: dict[str, str] = {
    'Nop':'', 'Unreachable':'', 'Constant':'', 'FrameAddress':'a',
    'Copy':'a','Move':'a','Drop':'a','Return':'a','Jump':'','CloseIterator':'','PublishStatic':'',
    'ExecutePlan':'','BindLocation':'b','OpenIterator':'a','Allocate':'c','ProjectAddress':'a',
    'EnvironmentAddress':'a','AdaptFunction':'a','AddressOffset':'a','IndexAddress':'ab',
    'CopyMemory':'abc','ClearMemory':'ac','Load':'a','Store':'ab','Branch':'a','Unary':'a',
    'Binary':'ab','MachineBinary':'ab','MachineCompare':'ab','FunctionComponent':'a',
    'ComposeFunction':'','MachineUnary':'a','MachineCast':'a','MachineIntrinsic':'',
    'Select':'abc','Convert':'a','Narrow':'a','MakeFunction':'','Call':'a','CallNative':'c',
    'MakeRange':'c','RangeFromMemory':'ab','NextIterator':'','Transfer':'ab','ProcessCall':'a',
    'SelectPath':'a','Barrier':'','Acquire':'a','PlanResult':'','TestStatic':'','StaticAddress':'',
    'MakeType':'a'
}

def reads(ins: dict[str, Any]) -> set[int]:
    return {ins[k] for k in READS[ins['op']] if ins[k] >= 0} | {s for s in ins['inputs'] if s >= 0}

def definitions(ins: dict[str, Any]) -> set[int]:
    return ({ins['result']} if ins['result'] >= 0 else set()) | ({ins['a']} if ins['op'] in {'Move','Drop','BindLocation'} and ins['a'] >= 0 else set())

def analyze(path: Path) -> dict[str, Any]:
    artifact: dict[str, Any] = json.loads(path.read_text())
    fn: dict[str, Any] = artifact['functions'][0]
    code: list[dict[str, Any]] = fn['instructions']
    n: int = len(code)
    nxt: list[list[int]] = []
    pred: list[list[int]] = [[] for _ in code]
    for pc, ins in enumerate(code):
        op: str = ins['op']
        candidates: list[int] = [] if op in {'Return','Unreachable'} else [ins['a']] if op == 'Jump' else [ins['b'],ins['c']] if op == 'Branch' else [pc+1,ins['b']] if op == 'NextIterator' else [pc+1]
        targets: list[int] = list(dict.fromkeys(t for t in candidates if t < n))
        nxt.append(targets)
        for t in targets: pred[t].append(pc)
    before: list[set[int]] = [set() for _ in code]
    after: list[set[int]] = [set() for _ in code]
    work: deque[int] = deque(reversed(range(n)))
    queued: set[int] = set(range(n))
    pops: int = 0
    union_items: int = 0
    clone_items: int = 0
    changed: int = 0
    while work:
        pc = work.popleft()
        queued.remove(pc)
        pops += 1
        live: set[int] = set()
        for t in nxt[pc]:
            union_items += len(before[t])
            live.update(before[t])
        after[pc] = live
        clone_items += len(live)
        inp: set[int] = (live - definitions(code[pc])) | reads(code[pc])
        if inp == before[pc]: continue
        changed += 1
        before[pc] = inp
        for t in pred[pc]:
            if t not in queued:
                queued.add(t)
                work.append(t)
    defining: dict[int, str] = {}
    for ins in code:
        if ins['result'] >= 0:
            defining[ins['result']] = ins['op'] + (':' + artifact['constants'][ins['a']]['kind'] if ins['op'] == 'Constant' else '')
    live_origins: Counter[str] = Counter()
    for live_set in after:
        for slot in live_set:
            live_origins[defining.get(slot, 'no-direct-result-definition')] += 1
    peak_pc: int = max(range(n), key=lambda pc:len(after[pc]))
    peak_origins: Counter[str] = Counter(defining.get(slot, 'no-direct-result-definition') for slot in after[peak_pc])
    defining_instructions: dict[int, dict[str, Any]] = {ins['result']:ins for ins in code if ins['result'] >= 0}
    def describe(slot: int) -> dict[str, Any]:
        ins: dict[str, Any] = defining_instructions.get(slot, {})
        return {'slot':slot, 'definition_pc':ins.get('pc'), 'origin':defining.get(slot, 'no-direct-result-definition'), 'last_read_pc':max((pc for pc, candidate in enumerate(code) if slot in reads(candidate)), default=None), 'constant':artifact['constants'][ins['a']] if ins.get('op') == 'Constant' else None}
    backedges: list[dict[str, Any]] = []
    for pc, targets in enumerate(nxt):
        for target in targets:
            if target > pc: continue
            members: set[int] = before[target]
            backedges.append({'from':pc, 'to':target, 'live_count':len(members), 'origins':dict(Counter(defining.get(slot,'no-direct-result-definition') for slot in members)), 'constant_members':[describe(slot) for slot in sorted(members) if defining.get(slot,'').startswith('Constant:')]})
    return {'backedges':backedges, 'peak_members':[describe(slot) for slot in sorted(after[peak_pc])], 'live_origins':dict(live_origins), 'peak_pc':peak_pc, 'peak_origins':dict(peak_origins), 'sha256':hashlib.sha256(path.read_bytes()).hexdigest(), 'path':str(path), 'instructions':n,'slots':len(fn['slots']), 'cfg_edges':sum(map(len,nxt)), 'worklist_pops':pops,'changes':changed,'successor_union_elements':union_items,'clone_elements':clone_items,'final_live_elements':sum(map(len,after)), 'max_live':max(map(len,after)), 'opcodes':dict(Counter(ins['op'] for ins in code))}

def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", type=Path, nargs="+")
    parser.add_argument("--output", type=Path)
    args: argparse.Namespace = parser.parse_args()
    results: list[dict[str, Any]] = [analyze(path) for path in args.artifacts]
    rendered: str = json.dumps(results, indent=2) + "\n"
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered)
    else:
        print(rendered, end="")


if __name__ == "__main__":
    main()
