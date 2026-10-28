//! Standalone, real-process v0.4 latency probe. Build with rustc -O.

use std::fs;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{Shutdown, TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
const DEADLINE: Duration = Duration::from_secs(15);
const SOURCE: &str = "input.bin -> target.bin\n";

struct Executable {
    label: &'static str,
    path: PathBuf,
    work: PathBuf,
}

struct Fixture(PathBuf);

impl Drop for Fixture {
    fn drop(&mut self) {
        if let Err(error) = fs::remove_dir_all(&self.0) {
            eprintln!("retained fixture {}: {error}", self.0.display());
        }
    }
}

fn command(executable: &Executable) -> Command {
    let mut command = Command::new(&executable.path);
    command
        .current_dir(&executable.work)
        .env("NO_COLOR", "1")
        .env_remove("MSYNC_TRACE")
        .stdout(Stdio::null())
        .stderr(Stdio::piped());
    command
}

fn process(executable: &Executable, args: &[&str], input: &str) -> Result<Duration> {
    let start = Instant::now();
    let mut child = command(executable)
        .args(args)
        .stdin(Stdio::piped())
        .spawn()?;
    child.stdin.take().unwrap().write_all(input.as_bytes())?;
    let output = child.wait_with_output()?;
    let elapsed = start.elapsed();
    if !output.status.success() {
        return Err(format!(
            "{} {:?}: {}",
            executable.label,
            args,
            String::from_utf8_lossy(&output.stderr)
        )
        .into());
    }
    Ok(elapsed)
}

fn record(case: &str, label: &str, sample: usize, duration: Duration) {
    println!(
        "sample,{case},{label},{sample},{:.6}",
        duration.as_secs_f64() * 1000.0
    );
}

fn summary(case: &str, label: &str, samples: &mut [Duration]) {
    samples.sort_unstable();
    let percentile =
        |p: f64| samples[((samples.len() - 1) as f64 * p).floor() as usize].as_secs_f64() * 1000.0;
    println!(
        "summary,{case},{label},{},{:.6},{:.6}",
        samples.len(),
        percentile(0.5),
        percentile(0.95)
    );
}

fn prepare(executable: &Executable, missing: bool) -> Result<()> {
    let target = executable.work.join("target.bin");
    if missing {
        match fs::remove_file(target) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(error.into()),
        }
    } else {
        fs::write(target, b"old target")?;
    }
    Ok(())
}

fn unrelated_files(executable: &Executable) -> Result<()> {
    let unrelated = executable.work.join("unrelated");
    fs::create_dir(&unrelated)?;
    for index in 0..1000 {
        fs::write(unrelated.join(format!("file-{index:04}")), [b'X'; 128])?;
    }
    fs::write(unrelated.join("ballast.bin"), vec![b'B'; 8 * 1024 * 1024])?;
    Ok(())
}

fn fresh_processes(executables: &[Executable], count: usize, payload: &[u8]) -> Result<()> {
    for (name, args, input, copied, missing) in [
        ("version", vec!["--version"], "", false, false),
        ("check-1", vec!["check", "-"], SOURCE, false, false),
        (
            "plan-1-offline",
            vec!["plan", "-", "--dry-run"],
            SOURCE,
            false,
            false,
        ),
        ("run-missing-64k", vec!["run", "-"], SOURCE, true, true),
        ("run-existing-64k", vec!["run", "-"], SOURCE, true, false),
    ] {
        let mut results = vec![Vec::new(); executables.len()];
        for round in 0..count + 2 {
            for offset in 0..executables.len() {
                let index = if round % 2 == 0 {
                    offset
                } else {
                    executables.len() - offset - 1
                };
                let executable = &executables[index];
                prepare(executable, missing)?;
                let elapsed = process(executable, &args, input)?;
                if copied && fs::read(executable.work.join("target.bin"))? != payload {
                    return Err(format!("incorrect {name} output from {}", executable.label).into());
                }
                if round >= 2 {
                    results[index].push(elapsed);
                    record(name, executable.label, round - 2, elapsed);
                }
            }
        }
        for (executable, samples) in executables.iter().zip(&mut results) {
            summary(name, executable.label, samples);
        }
    }
    for executable in executables {
        unrelated_files(executable)?;
    }
    for missing in [false, true] {
        let name = if missing {
            "run-missing-unrelated-1000-8m"
        } else {
            "run-existing-unrelated-1000-8m"
        };
        let mut results = vec![Vec::new(); executables.len()];
        for round in 0..count + 2 {
            for offset in 0..executables.len() {
                let index = if round % 2 == 0 {
                    offset
                } else {
                    executables.len() - offset - 1
                };
                prepare(&executables[index], missing)?;
                let elapsed = process(&executables[index], &["run", "-"], SOURCE)?;
                if fs::read(executables[index].work.join("target.bin"))? != payload {
                    return Err("unrelated-cwd run changed output".into());
                }
                if round >= 2 {
                    results[index].push(elapsed);
                    record(name, executables[index].label, round - 2, elapsed);
                }
            }
        }
        for (executable, samples) in executables.iter().zip(&mut results) {
            summary(name, executable.label, samples);
        }
    }
    for executable in executables {
        fs::remove_dir_all(executable.work.join("unrelated"))?;
    }
    Ok(())
}

struct Repl {
    child: Child,
    input: Option<ChildStdin>,
    lines: Receiver<String>,
    output: Option<JoinHandle<()>>,
    errors: Option<JoinHandle<Vec<u8>>>,
    marker: String,
}

impl Repl {
    fn start(executable: &Executable) -> Result<Self> {
        let mut child = command(executable)
            .args(["repl", "--color", "never"])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .spawn()?;
        let input = child.stdin.take();
        let stdout = child.stdout.take().unwrap();
        let stderr = child.stderr.take().unwrap();
        let (sender, lines) = mpsc::channel();
        let output = thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                let Ok(line) = line else { break };
                if sender.send(line).is_err() {
                    break;
                }
            }
        });
        let errors = thread::spawn(move || {
            let mut bytes = Vec::new();
            let _ = BufReader::new(stderr).read_to_end(&mut bytes);
            bytes
        });
        let mut repl = Self {
            child,
            input,
            lines,
            output: Some(output),
            errors: Some(errors),
            marker: String::new(),
        };
        repl.input.as_mut().unwrap().write_all(b"/pwd\n")?;
        repl.marker = repl.lines.recv_timeout(DEADLINE)?;
        if repl.marker.trim().is_empty() {
            return Err("empty REPL cwd barrier".into());
        }
        println!("environment,repl_cwd,{}", repl.marker);
        Ok(repl)
    }

    fn submit(&mut self, input: &str) -> Result<Duration> {
        let start = Instant::now();
        let stream = self.input.as_mut().unwrap();
        stream.write_all(input.as_bytes())?;
        stream.write_all(b"/pwd\n")?;
        stream.flush()?;
        loop {
            if self
                .lines
                .recv_timeout(DEADLINE.saturating_sub(start.elapsed()))?
                == self.marker
            {
                return Ok(start.elapsed());
            }
        }
    }

    fn finish(mut self) -> Result<()> {
        self.input.as_mut().unwrap().write_all(b"/quit\n")?;
        self.input.take();
        let deadline = Instant::now() + DEADLINE;
        let status = loop {
            if let Some(status) = self.child.try_wait()? {
                break status;
            }
            if Instant::now() >= deadline {
                return Err("REPL shutdown timed out".into());
            }
            thread::sleep(Duration::from_millis(1));
        };
        self.output.take().unwrap().join().unwrap();
        let errors = self.errors.take().unwrap().join().unwrap();
        if !status.success() || !errors.is_empty() {
            return Err(format!(
                "REPL failed or emitted diagnostics: {}",
                String::from_utf8_lossy(&errors)
            )
            .into());
        }
        Ok(())
    }
}

impl Drop for Repl {
    fn drop(&mut self) {
        self.input.take();
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(output) = self.output.take() {
            let _ = output.join();
        }
        if let Some(errors) = self.errors.take()
            && let Ok(errors) = errors.join()
            && !errors.is_empty()
        {
            eprintln!("REPL diagnostics: {}", String::from_utf8_lossy(&errors));
        }
    }
}

fn retained_bytes(path: &Path) -> Result<(usize, u64)> {
    if !path.exists() {
        return Ok((0, 0));
    }
    let mut files = 0;
    let mut bytes = 0;
    for entry in fs::read_dir(path)? {
        let entry = entry?;
        let metadata = entry.metadata()?;
        if metadata.is_dir() {
            let (child_files, child_bytes) = retained_bytes(&entry.path())?;
            files += child_files;
            bytes += child_bytes;
        } else {
            files += 1;
            bytes += metadata.len();
        }
    }
    Ok((files, bytes))
}

fn first_file(path: &Path) -> Result<Option<PathBuf>> {
    for entry in fs::read_dir(path)? {
        let entry = entry?;
        if entry.file_type()?.is_dir() {
            if let Some(file) = first_file(&entry.path())? {
                return Ok(Some(file));
            }
        } else if entry.file_type()?.is_file() {
            return Ok(Some(entry.path()));
        }
    }
    Ok(None)
}

fn descriptor_filesystem(role: &str, label: &str, path: &Path) -> Result<()> {
    #[cfg(target_os = "linux")]
    {
        use std::os::{fd::AsRawFd, unix::fs::MetadataExt};
        let file = fs::File::open(path)?;
        let descriptor = format!("/proc/{}/fd/{}", std::process::id(), file.as_raw_fd());
        let output = Command::new("stat")
            .args(["-f", "-L", "-c", "type=%T magic=%t"])
            .arg(descriptor)
            .output()?;
        if !output.status.success() {
            return Err(String::from_utf8_lossy(&output.stderr).into_owned().into());
        }
        println!(
            "filesystem,{role},{label},dev={},{}",
            file.metadata()?.dev(),
            String::from_utf8_lossy(&output.stdout).trim()
        );
    }
    #[cfg(not(target_os = "linux"))]
    println!("filesystem,{role},{label},path={}", path.display());
    Ok(())
}

fn repl_overwrites(executables: &[Executable], count: usize, payload: &[u8]) -> Result<()> {
    let mut repls = executables
        .iter()
        .map(Repl::start)
        .collect::<Result<Vec<_>>>()?;
    let mut results = vec![Vec::new(); executables.len()];
    for round in 0..count + 2 {
        for offset in 0..executables.len() {
            let index = if round % 2 == 0 {
                offset
            } else {
                executables.len() - offset - 1
            };
            let elapsed = repls[index].submit(SOURCE)?;
            if fs::read(executables[index].work.join("target.bin"))? != payload {
                return Err("REPL output differs at its barrier".into());
            }
            if round >= 2 {
                results[index].push(elapsed);
                record(
                    "repl-overwrite-64k-with-pwd-barrier",
                    executables[index].label,
                    round - 2,
                    elapsed,
                );
            }
        }
    }
    for (executable, samples) in executables.iter().zip(&mut results) {
        summary(
            "repl-overwrite-64k-with-pwd-barrier",
            executable.label,
            samples,
        );
        let (files, bytes) = retained_bytes(&executable.work.join(".temp"))?;
        println!(
            "retention,{},repl-overwrite-64k,{files},{bytes}",
            executable.label
        );
        descriptor_filesystem(
            "source",
            executable.label,
            &executable.work.join("input.bin"),
        )?;
        descriptor_filesystem(
            "destination",
            executable.label,
            &executable.work.join("target.bin"),
        )?;
        if let Some(artifact) = first_file(&executable.work.join(".temp"))? {
            descriptor_filesystem("artifact", executable.label, &artifact)?;
        }
    }
    for repl in repls {
        repl.finish()?;
    }
    Ok(())
}

fn connected(listener: &TcpListener) -> Result<TcpStream> {
    listener.set_nonblocking(true)?;
    let deadline = Instant::now() + DEADLINE;
    loop {
        match listener.accept() {
            Ok((stream, _)) => return Ok(stream),
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {}
            Err(error) => return Err(error.into()),
        }
        if Instant::now() >= deadline {
            return Err("physical source did not connect".into());
        }
        thread::sleep(Duration::from_millis(1));
    }
}

fn observe(path: &Path, expected: &[u8], start: Instant) -> Result<Duration> {
    loop {
        if fs::read(path).is_ok_and(|bytes| bytes == expected) {
            return Ok(start.elapsed());
        }
        if start.elapsed() >= DEADLINE {
            return Err(format!("publication timed out: {}", path.display()).into());
        }
        thread::sleep(Duration::from_millis(1));
    }
}

fn flow_completion(executables: &[Executable], count: usize) -> Result<()> {
    let mut results = vec![Vec::new(); executables.len()];
    let mut acknowledgements = vec![Vec::new(); executables.len()];
    for round in 0..count + 1 {
        for offset in 0..executables.len() {
            let index = if round % 2 == 0 {
                offset
            } else {
                executables.len() - offset - 1
            };
            let executable = &executables[index];
            for name in ["slow-flow", "fast-flow"] {
                match fs::remove_file(executable.work.join(name)) {
                    Ok(()) => {}
                    Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
                    Err(error) => return Err(error.into()),
                }
            }
            let slow_listener = TcpListener::bind("127.0.0.1:0")?;
            let fast_listener = TcpListener::bind("127.0.0.1:0")?;
            let mut repl = Repl::start(executable)?;
            repl.submit(&format!(
                "slow => tcp://{}\nslow::/ -> slow-flow\nfast => tcp://{}\nfast::/ -> fast-flow\n",
                slow_listener.local_addr()?,
                fast_listener.local_addr()?
            ))?;
            let mut slow = connected(&slow_listener)?;
            let mut fast = connected(&fast_listener)?;
            slow.write_all(b"slow payload")?;
            fast.write_all(b"fast payload")?;
            let start = Instant::now();
            fast.shutdown(Shutdown::Write)?;
            let elapsed = observe(&executable.work.join("fast-flow"), b"fast payload", start)?;
            repl.submit("")?;
            let acknowledged = start.elapsed();
            if executable.work.join("slow-flow").exists() {
                return Err("an idle first flow published before its EOF".into());
            }
            let parent_modified = fs::metadata(&executable.work)?.modified()?;
            slow.shutdown(Shutdown::Write)?;
            observe(
                &executable.work.join("slow-flow"),
                b"slow payload",
                Instant::now(),
            )?;
            repl.submit("")?;
            if fs::metadata(&executable.work)?.modified()? != parent_modified {
                return Err("earlier logical namespace receipt overrode the later receipt".into());
            }
            repl.submit("/wait\n")?;
            repl.finish()?;
            if round >= 1 {
                results[index].push(elapsed);
                acknowledgements[index].push(acknowledged);
                record(
                    "second-flow-eof-to-visible-file",
                    executable.label,
                    round - 1,
                    elapsed,
                );
                record(
                    "second-flow-eof-to-repl-ack",
                    executable.label,
                    round - 1,
                    acknowledged,
                );
            }
        }
    }
    for (executable, samples) in executables.iter().zip(&mut results) {
        summary("second-flow-eof-to-visible-file", executable.label, samples);
    }
    for (executable, samples) in executables.iter().zip(&mut acknowledgements) {
        summary("second-flow-eof-to-repl-ack", executable.label, samples);
    }
    Ok(())
}

fn capture_scope(executables: &[Executable], payload: &[u8]) -> Result<()> {
    for executable in executables {
        for name in ["slow-flow", "fast-flow"] {
            let path = executable.work.join(name);
            if path.exists() {
                fs::remove_file(path)?;
            }
        }
        prepare(executable, true)?;
        unrelated_files(executable)?;
        let mut repl = Repl::start(executable)?;
        repl.submit(SOURCE)?;
        if fs::read(executable.work.join("target.bin"))? != payload {
            return Err("capture-scope copy changed output".into());
        }
        let (files, bytes) = retained_bytes(&executable.work.join(".temp"))?;
        println!(
            "capture-scope,{},missing-target-unrelated-1000-8m,{files},{bytes}",
            executable.label
        );
        repl.finish()?;
        fs::remove_dir_all(executable.work.join("unrelated"))?;
    }
    Ok(())
}

fn literal_processes(executables: &[Executable], count: usize) -> Result<()> {
    let old_time = UNIX_EPOCH + Duration::from_secs(978307200);
    let selected = std::env::var("MSYNC_PERF_LITERAL_CASE").ok();
    let mut matched = false;
    for (case, size, named) in [
        ("literal-4b", 4, false),
        ("literal-64k", 64 * 1024, false),
        ("literal-1m", 1024 * 1024, false),
        ("literal-named-two-4_5m", 9 * 1024 * 512, true),
    ] {
        let left = "A".repeat(size);
        let right = "B".repeat(size);
        let source = if named {
            format!(
                "transaction A:\n    \"{left}\" -> a/target.bin\ntransaction B:\n    \"{right}\" -> b/target.bin\n"
            )
        } else {
            format!("\"{left}\" -> target.bin\n")
        };
        let outputs = if named {
            vec![
                ("a/target.bin", left.as_bytes()),
                ("b/target.bin", right.as_bytes()),
            ]
        } else {
            vec![("target.bin", left.as_bytes())]
        };
        for jobs in [1, 2] {
            let name = format!("{case}-jobs{jobs}");
            if selected.as_ref().is_some_and(|selected| selected != &name) {
                continue;
            }
            matched = true;
            let mut results = vec![Vec::new(); executables.len()];
            let mut parent_changes = None;
            for round in 0..count + 2 {
                for offset in 0..executables.len() {
                    let index = if round % 2 == 0 {
                        offset
                    } else {
                        executables.len() - offset - 1
                    };
                    let executable = &executables[index];
                    fs::create_dir_all(executable.work.join(".temp"))?;
                    for (relative, _) in &outputs {
                        let target = executable.work.join(relative);
                        fs::create_dir_all(target.parent().unwrap())?;
                        fs::write(&target, b"old")?;
                        #[cfg(unix)]
                        {
                            use std::os::unix::fs::PermissionsExt;
                            fs::set_permissions(&target, fs::Permissions::from_mode(0o640))?;
                        }
                        fs::File::options()
                            .write(true)
                            .open(&target)?
                            .set_times(fs::FileTimes::new().set_modified(old_time))?;
                        fs::File::open(target.parent().unwrap())?
                            .set_times(fs::FileTimes::new().set_modified(old_time))?;
                    }
                    let started = SystemTime::now();
                    let elapsed = process(
                        executable,
                        &["run", "-", "--jobs", &jobs.to_string()],
                        &source,
                    )?;
                    let mut changed = Vec::new();
                    for (relative, expected) in &outputs {
                        let target = executable.work.join(relative);
                        let metadata = fs::metadata(&target)?;
                        if !metadata.is_file() || fs::read(&target)? != *expected {
                            return Err(
                                format!("{name}: incorrect {} bytes", executable.label).into()
                            );
                        }
                        #[cfg(unix)]
                        {
                            use std::os::unix::fs::PermissionsExt;
                            if metadata.permissions().mode() & 0o777 != 0o640 {
                                return Err(format!("{name}: changed target permissions").into());
                            }
                        }
                        if metadata.modified()? < started
                            || metadata.modified()? > SystemTime::now()
                        {
                            return Err(format!("{name}: invalid literal timestamp").into());
                        }
                        let parent_time = fs::metadata(target.parent().unwrap())?.modified()?;
                        if parent_time != old_time
                            && (parent_time < started || parent_time > SystemTime::now())
                        {
                            return Err(format!("{name}: invalid parent timestamp").into());
                        }
                        changed.push(parent_time != old_time);
                    }
                    if parent_changes
                        .as_ref()
                        .is_some_and(|expected| expected != &changed)
                    {
                        return Err(format!("{name}: parent timestamp behavior differs").into());
                    }
                    parent_changes = Some(changed);
                    if round == 0 {
                        descriptor_filesystem(
                            "literal-destination",
                            executable.label,
                            &executable.work.join(outputs[0].0),
                        )?;
                    }
                    if round >= 2 {
                        results[index].push(elapsed);
                        record(&name, executable.label, round - 2, elapsed);
                    }
                }
            }
            for (executable, samples) in executables.iter().zip(&mut results) {
                summary(&name, executable.label, samples);
            }
            println!(
                "validation,{name},bytes-per-output={size},outputs={},permissions=0640,literal-mtime=fresh,parent-mtime-changed={:?},buffer=default-65536",
                outputs.len(),
                parent_changes.unwrap()
            );
        }
    }
    if !matched {
        return Err(format!("unknown literal case: {}", selected.unwrap_or_default()).into());
    }
    Ok(())
}

fn main() -> Result<()> {
    let args = std::env::args().collect::<Vec<_>>();
    if cfg!(debug_assertions) {
        eprintln!("latency_v4: release-only probe; skipping debug execution");
        return Ok(());
    }
    if args.len() < 4 {
        eprintln!(
            "usage: latency_v4 BASELINE CANDIDATE|- FIXTURE_ROOT [SAMPLES] [NATIVE_ROOT_ALIAS]"
        );
        return Ok(());
    }
    let count = args
        .get(4)
        .map(|value| value.parse())
        .transpose()?
        .unwrap_or(15usize);
    if count == 0 {
        return Err("samples must be positive".into());
    }
    let root = fs::canonicalize(&args[3])?;
    let name = format!(
        "latency-v4-{}-{}",
        std::process::id(),
        SystemTime::now().duration_since(UNIX_EPOCH)?.as_nanos()
    );
    let owned = root.join(&name);
    fs::create_dir(&owned)?;
    let fixture = Fixture(owned.clone());
    let visible = args
        .get(5)
        .map_or_else(|| owned.clone(), |alias| Path::new(alias).join(&name));
    let payload = (0..65536)
        .map(|index| ((index * 31 + index / 251) % 251) as u8)
        .collect::<Vec<_>>();
    let mut executables = Vec::new();
    fs::create_dir(owned.join("bin"))?;
    for (label, path) in [
        ("baseline", args[1].as_str()),
        ("candidate", args[2].as_str()),
    ] {
        if path == "-" {
            continue;
        }
        let short = if label == "baseline" { "A" } else { "B" };
        fs::create_dir(owned.join(short))?;
        fs::write(owned.join(short).join("input.bin"), &payload)?;
        let binary_name = if cfg!(windows) {
            format!("msync-{short}.exe")
        } else {
            format!("msync-{short}")
        };
        let binary = owned.join("bin").join(&binary_name);
        fs::copy(fs::canonicalize(path)?, binary)?;
        executables.push(Executable {
            label,
            path: visible.join("bin").join(&binary_name),
            work: visible.join(short),
        });
    }
    println!("environment,fixture_creation,{}", fixture.0.display());
    println!("environment,fixture_address,{}", visible.display());
    println!("environment,payload_bytes,{}", payload.len());
    println!("environment,cache,warm-source-and-loader-after-two-warmups");
    println!("environment,repl_ack,copy-submission-plus-pwd-roundtrip");
    for executable in &executables {
        descriptor_filesystem("binary", executable.label, &executable.path)?;
    }
    if std::env::var_os("MSYNC_PERF_ONLY_LITERAL").is_some() {
        return literal_processes(&executables, count);
    }
    if let Some(profile) = std::env::var_os("MSYNC_PERF_PROFILE_DIR") {
        let profile = fs::canonicalize(profile)?;
        for executable in &executables {
            for missing in [true, false] {
                prepare(executable, missing)?;
                let name = if missing { "missing" } else { "existing" };
                let trace = format!("profile-{name}.jsonl");
                process(executable, &["run", "-", "--trace", &trace], SOURCE)?;
                fs::copy(
                    executable.work.join(trace),
                    profile.join(format!("{}-{name}.jsonl", executable.label)),
                )?;
            }
        }
        return Ok(());
    }
    let repl_rounds = std::env::var("MSYNC_PERF_REPL_ROUNDS")
        .ok()
        .map(|value| value.parse())
        .transpose()?
        .unwrap_or(count.max(32));
    if std::env::var_os("MSYNC_PERF_ONLY_REPL").is_some() {
        for executable in &executables {
            fs::write(executable.work.join("target.bin"), &payload)?;
        }
        repl_overwrites(&executables, repl_rounds, &payload)?;
        return Ok(());
    }
    fresh_processes(&executables, count, &payload)?;
    repl_overwrites(&executables, repl_rounds, &payload)?;
    flow_completion(&executables, count.min(9))?;
    capture_scope(&executables, &payload)?;
    drop(fixture);
    Ok(())
}
