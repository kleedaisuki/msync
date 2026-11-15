from __future__ import annotations
import argparse
import hashlib
import json
import os
import platform
import resource
import statistics
import subprocess
import time
from pathlib import Path
from typing import Any
ROOT: Path = Path.cwd()
OUT: Path = ROOT/'artifacts/benchmarks/synchronous-evaluator'
IMAGES: dict[str,Path] = {name:ROOT/f'artifacts/native/{directory}/msync' for name,directory in [('before','evaluator-before'),('sync','evaluator-sync')]}

def invoke(binary: Path,args: list[str], expected: bytes | None = None) -> tuple[float, float]:
    usage: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    start: int=time.perf_counter_ns()
    result: subprocess.CompletedProcess[bytes]=subprocess.run([str(binary),'--no-config','--no-telemetry',*args],cwd=ROOT,env=dict(os.environ,MSYNC_JIT='auto'),input=b'',capture_output=True,timeout=120,check=True)
    if expected is not None and result.stdout!=expected: raise RuntimeError(f'{args}: {result.stdout!r}, {result.stderr!r}')
    after: resource.struct_rusage = resource.getrusage(resource.RUSAGE_CHILDREN)
    return (time.perf_counter_ns()-start)/1e6, 1000 * (after.ru_utime + after.ru_stime - usage.ru_utime - usage.ru_stime)

def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate', type=Path, default=IMAGES['sync'])
    parser.add_argument('--baseline', type=Path, default=IMAGES['before'])
    parser.add_argument('--reference', type=Path, help='Optional third frozen image for the same paired run')
    parser.add_argument('--call-count', type=int, default=100000)
    parser.add_argument('--repetitions', type=int, default=3)
    args: argparse.Namespace = parser.parse_args()
    IMAGES['sync'], IMAGES['before'] = args.candidate.resolve(), args.baseline.resolve()
    if args.reference is not None: IMAGES['reference'] = args.reference.resolve()
    count: int = args.call_count
    OUT.mkdir(parents=True,exist_ok=True)
    source: Path=OUT/f'calls-{count}.msync'
    source.write_text('fn leaf(x: Number) { x+1 }; fn middle(x: Number) { leaf(x)+leaf(x+1) }; let i=0; let sum=0; loop { if i>=' + str(count) + ' {break;}; sum=sum+middle(i); i=i+1; }; $std::io::println(sum as String);\n')
    cases: dict[str,tuple[Path,int,bytes]]={f'calls-{count}':(source,0,f'{count * (count + 2)}\n'.encode()),'bplus':(ROOT/'artifacts/benchmarks/storage-contract/workload.msync',2,(ROOT/'artifacts/benchmarks/storage-contract/expected.txt').read_bytes())}
    affinity: list[int] | None = sorted(os.sched_getaffinity(0)) if hasattr(os, 'sched_getaffinity') else None
    report: dict[str,Any]={'cpu_affinity':affinity,'images':{name:{'path':str(binary),'sha256':hashlib.sha256(binary.read_bytes()).hexdigest()} for name,binary in IMAGES.items()},'platform':platform.platform(),'warmups':1,'repetitions':args.repetitions,'samples':[],'medians_ms':{},'medians_cpu_ms':{},'paired_change_pct':{},'workloads':{name:{'source':str(path),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'optimization':level,'expected':expected.decode()} for name,(path,level,expected) in cases.items()}}
    for case,(source,level,expected) in cases.items():
        for name,binary in IMAGES.items():
            invoke(binary,['compile',str(source),f'-O{level}','-o',str(OUT/f'{case}-{name}.mso')])
            invoke(binary,['bind-ir',str(OUT/f'{case}-{name}.mso'),'-o',str(OUT/f'{case}-{name}.mexe')])
        for pair in range(args.repetitions + 1):
            order: list[str] = list(IMAGES) if pair%2==0 else list(reversed(IMAGES))
            if len(order)>2:
                offset: int = pair%len(order)
                order = order[offset:]+order[:offset]
            for name in order:
                ms,cpu=invoke(IMAGES[name],['run-ir',str(OUT/f'{case}-{name}.mexe')],expected)
                print(f'{case} {pair} {name}: {ms:.1f} ms wall / {cpu:.1f} ms CPU',flush=True)
                if pair: report['samples'].append({'case':case,'pair':pair,'image':name,'ms':ms,'cpu_ms':cpu})
        report['medians_ms'][case]={name:statistics.median(row['ms'] for row in report['samples'] if row['case']==case and row['image']==name) for name in IMAGES}
        report['medians_cpu_ms'][case]={name:statistics.median(row['cpu_ms'] for row in report['samples'] if row['case']==case and row['image']==name) for name in IMAGES}
        report['paired_change_pct'][case] = {}
        for name in IMAGES:
            if name == 'before': continue
            changes: list[float] = []
            for pair in range(1, args.repetitions + 1):
                values: dict[str, float] = {row['image']: row['ms'] for row in report['samples'] if row['case'] == case and row['pair'] == pair}
                changes.append(100 * (values[name] / values['before'] - 1))
            report['paired_change_pct'][case][name] = {'samples': changes, 'median': statistics.median(changes)}
        (OUT/'measurements.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps(report['medians_ms'],indent=2))
if __name__=='__main__': main()
