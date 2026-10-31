//! Deadline-bounded subprocesses with concurrently drained diagnostic streams.

use std::io::{Read, Write};
use std::path::Path;
use std::process::{Child, ChildStdin, Command, Output, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

const DEADLINE: Duration = Duration::from_secs(15);

#[derive(Clone, Copy)]
enum Stream {
    Stdout,
    Stderr,
}

pub struct Process {
    child: Child,
    input: Option<ChildStdin>,
    chunks: Receiver<(Stream, Vec<u8>)>,
    readers: Vec<JoinHandle<()>>,
    stdout: Vec<u8>,
    stderr: Vec<u8>,
}

impl Process {
    pub fn command(root: &Path, args: &[&str]) -> Command {
        let mut command = Command::new(env!("CARGO_BIN_EXE_msync"));
        command
            .current_dir(root)
            .args(args)
            .env("HOME", root.join("home"))
            .env("USERPROFILE", root.join("home"))
            .env("XDG_CONFIG_HOME", root.join("config"))
            .env("LOCALAPPDATA", root.join("local"))
            .env("APPDATA", root.join("config"))
            .env("NO_COLOR", "1");
        for (name, _) in std::env::vars_os() {
            if name.to_string_lossy().starts_with("MSYNC_") {
                command.env_remove(name);
            }
        }
        command
    }

    pub fn start(mut command: Command) -> Self {
        let mut child = command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let input = child.stdin.take();
        let stdout = child.stdout.take().unwrap();
        let stderr = child.stderr.take().unwrap();
        let (sender, chunks) = mpsc::channel();
        let mut readers = Vec::new();
        for (stream, mut reader) in [
            (Stream::Stdout, Box::new(stdout) as Box<dyn Read + Send>),
            (Stream::Stderr, Box::new(stderr) as Box<dyn Read + Send>),
        ] {
            let sender = sender.clone();
            readers.push(thread::spawn(move || {
                let mut bytes = [0; 4096];
                loop {
                    let count = match reader.read(&mut bytes) {
                        Ok(0) => return,
                        Ok(count) => count,
                        Err(error) if error.kind() == std::io::ErrorKind::Interrupted => continue,
                        Err(error) => panic!("subprocess stream read failed: {error}"),
                    };
                    if sender.send((stream, bytes[..count].to_vec())).is_err() {
                        return;
                    }
                }
            }));
        }
        Self {
            child,
            input,
            chunks,
            readers,
            stdout: Vec::new(),
            stderr: Vec::new(),
        }
    }

    pub fn send(&mut self, bytes: &[u8]) {
        let input = self.input.as_mut().unwrap();
        input.write_all(bytes).unwrap();
        input.flush().unwrap();
    }

    pub fn until(&mut self, predicate: impl Fn(&str, &str) -> bool) {
        let deadline = Instant::now() + DEADLINE;
        loop {
            if predicate(
                &String::from_utf8_lossy(&self.stdout),
                &String::from_utf8_lossy(&self.stderr),
            ) {
                return;
            }
            let chunk = self
                .chunks
                .recv_timeout(deadline.saturating_duration_since(Instant::now()))
                .unwrap_or_else(|error| {
                    panic!(
                        "subprocess deadline: {error}\nstdout={}\nstderr={}",
                        String::from_utf8_lossy(&self.stdout),
                        String::from_utf8_lossy(&self.stderr)
                    )
                });
            self.record(chunk);
        }
    }

    pub fn finish(&mut self) -> Output {
        self.input.take();
        let deadline = Instant::now() + DEADLINE;
        let status = loop {
            self.drain();
            if let Some(status) = self.child.try_wait().unwrap() {
                break status;
            }
            assert!(
                Instant::now() < deadline,
                "subprocess exit deadline\nstdout={}\nstderr={}",
                String::from_utf8_lossy(&self.stdout),
                String::from_utf8_lossy(&self.stderr)
            );
            thread::sleep(Duration::from_millis(5));
        };
        for reader in self.readers.drain(..) {
            reader.join().unwrap();
        }
        self.drain();
        Output {
            status,
            stdout: std::mem::take(&mut self.stdout),
            stderr: std::mem::take(&mut self.stderr),
        }
    }

    fn record(&mut self, (stream, bytes): (Stream, Vec<u8>)) {
        match stream {
            Stream::Stdout => self.stdout.extend_from_slice(&bytes),
            Stream::Stderr => self.stderr.extend_from_slice(&bytes),
        }
    }

    fn drain(&mut self) {
        while let Ok(chunk) = self.chunks.try_recv() {
            self.record(chunk);
        }
    }
}

impl Drop for Process {
    fn drop(&mut self) {
        self.input.take();
        let _ = self.child.kill();
        let _ = self.child.wait();
        for reader in self.readers.drain(..) {
            let _ = reader.join();
        }
    }
}
