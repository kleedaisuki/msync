"""Exercise filesystem value Futures, coherent native writes and FIFO co-start."""
from __future__ import annotations
import argparse
import json
import os
import subprocess
from pathlib import Path

ROOT: Path = Path(__file__).resolve().parents[2]

def invoke(binary: Path, work: Path, arguments: list[str]) -> subprocess.CompletedProcess[bytes]:
    result: subprocess.CompletedProcess[bytes] = subprocess.run(
        [str(binary), '--no-config', '--no-telemetry', *arguments], cwd=work,
        input=b'', capture_output=True, timeout=30, check=False)
    if result.returncode: raise RuntimeError(f'{arguments}: {result.stderr.decode(errors="replace")}')
    return result

def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    args: argparse.Namespace = parser.parse_args()
    binary: Path = args.binary.resolve()
    work: Path = ROOT/'.temp/file-future-journeys'
    work.mkdir(parents=True, exist_ok=True)
    cases: dict[str,tuple[str,bytes]] = {
        'version': ('let old=$std::io::read_text(./value); $std::io::write_text(./value,"new"); $std::io::println(old); $std::io::println($std::io::read_text(./value));', b'old\nnew\n'),
        'alias': ('let path=./value; let old=$std::io::read_text(path); $std::io::write_text(./sub/../value,"new"); $std::io::println(old);', b'old\n'),
        'discard-before-write': ('fn discard() { $std::io::read_text(./value); }; let n=0; loop { if n>=64 { break; }; discard(); n=n+1; }; $std::io::write_text(./value,"new"); $std::io::println($std::io::read_text(./value));', b'new\n'),
        'self-stream': ('$std::io::write_bytes(./value,$std::io::read_bytes(./value)); $std::io::println($std::io::read_text(./value));', b'old\n'),
    }
    if os.name == 'posix':
        pipe: Path = work/'pipe'
        if not pipe.exists(): os.mkfifo(pipe)
        cases['fifo'] = ('let text=$std::io::read_text(./pipe); $std::io::write_text(./pipe,"payload"); $std::io::println(text);', b'payload\n')
        cases['discard-fifo'] = ('$std::io::read_text(./pipe); $std::io::println("done");', b'done\n')
    records: list[dict[str,str|int]] = []
    for name,(body,expected) in cases.items():
        source: Path = work/f'{name}.msync'
        source.write_text(body+'\n')
        (work/'sub').mkdir(exist_ok=True)
        for level in (0,2):
            invoke(binary, work, ['compile', str(source), f'-O{level}', '-o', f'{name}.mso'])
            invoke(binary, work, ['bind-ir', f'{name}.mso', '-o', f'{name}.mexe'])
            for route,target in [('source',str(source)),('bound',f'{name}.mexe')]:
                (work/'value').write_bytes(b'old')
                result: subprocess.CompletedProcess[bytes] = invoke(binary, work, ['run',target,f'-O{level}'])
                assert result.stdout == expected,(name,level,route,result.stdout,result.stderr)
                if name in ('version','alias'): assert (work/'value').read_bytes()==b'new'
                records.append({'case':name,'level':level,'route':route})
    (work/'result.json').write_text(json.dumps(records,indent=2)+'\n')
    print(f'PASS {len(records)} filesystem Future journeys: versions, aliases, nested self-read and stream lifetimes')

if __name__=='__main__': main()
