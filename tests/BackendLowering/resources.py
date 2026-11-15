"""Exercise bound native resources against a real owned OpenSSH server."""
from __future__ import annotations
import argparse
import json
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

ROOT: Path = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tests/support'))
from ssh_fixture import Fixture, lines

def invoke(binary: Path, arguments: list[str], work: Path, environment: dict[str,str]) -> tuple[bytes,float]:
    start: int = time.perf_counter_ns()
    result: subprocess.CompletedProcess[bytes] = subprocess.run([str(binary),'--no-config','--no-telemetry',*arguments],cwd=work,env=environment,input=b'',capture_output=True,timeout=60,check=False)
    if result.returncode: raise RuntimeError(f"{arguments!r}: {result.stderr.decode()}")
    return result.stdout,(time.perf_counter_ns()-start)/1e6

def main() -> None:
    parser: argparse.ArgumentParser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, required=True)
    parser.add_argument('--baseline', type=Path)
    options: argparse.Namespace = parser.parse_args()
    images: dict[str,Path] = {'candidate': options.binary.resolve()}
    if options.baseline: images['baseline'] = options.baseline.resolve()
    reports: list[dict[str,Any]] = []
    with Fixture(ROOT/'.temp/backend-ssh') as fixture:
        env: dict[str,str] = fixture.environment()
        source: Path = fixture.root/'resources.msync'
        source.write_text('let remote=ssh://Perf_Fixture;\n' + '$std::process::wait((remote @ "/bin/true")());\n'*4 + '$std::io::println("complete");\n')
        for name,binary in images.items():
            before: int = len(lines(fixture.calls))
            invoke(binary,['compile',str(source),'-O2','-o',f'{name}.mso'],fixture.root,env)
            invoke(binary,['bind-ir',f'{name}.mso','-o',f'{name}.mexe'],fixture.root,env)
            assert len(lines(fixture.calls))==before, 'Binding authenticated an endpoint'
        for sample in range(4):
            for name in (list(images) if sample%2==0 else list(reversed(images))):
                before_log: int = len(lines(fixture.log))
                output,elapsed = invoke(images[name],['run-ir',f'{name}.mexe'],fixture.root,env)
                log: list[str] = lines(fixture.log)[before_log:]
                auth: int = sum('Accepted publickey for ' in line for line in log)
                sessions: int = sum('Starting session:' in line for line in log)
                assert output==b'complete\n', output
                assert sessions==4, log
                assert auth==(1 if name=='candidate' else 4), log
                reports.append({'image':name,'sample':sample,'ms':elapsed,'authentications':auth,'sessions':sessions})
        local: Path = fixture.root/'synchronous.msync'
        local.write_text('/bin/sh("-c", "sleep 0.02; printf ready > marker");\n$std::io::println($std::fs::exists(./marker) as String);\n')
        output,_ = invoke(images['candidate'],['run',str(local),'-O2'],fixture.root,env)
        assert output==b'true\n',output
        result: dict[str,Any] = {'fixture':str(fixture.root),'journeys':reports,'synchronous_process':'pass'}
        destination: Path = ROOT/'artifacts/benchmarks/backend-native/resources.json'
        destination.parent.mkdir(parents=True,exist_ok=True)
        destination.write_text(json.dumps(result,indent=2)+'\n')
        print(json.dumps(result,indent=2))
if __name__=='__main__':
    main()
