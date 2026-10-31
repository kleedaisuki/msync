//! A real stdio LSP client used only by independent acceptance tests.

use std::io::{BufRead, BufReader, Read, Write};
use std::path::Path;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use serde_json::{Value, json};

pub struct Client {
    child: Child,
    input: Option<ChildStdin>,
    messages: Receiver<Result<Value, String>>,
    pending: Vec<Value>,
    reader: Option<JoinHandle<()>>,
    errors: Option<JoinHandle<String>>,
}

impl Client {
    pub fn start(root: &Path) -> Self {
        let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
            .args(["--language-server"])
            .current_dir(root)
            .env("HOME", root)
            .env("USERPROFILE", root)
            .env("XDG_CONFIG_HOME", root.join("config"))
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let input = child.stdin.take();
        let stdout = child.stdout.take().unwrap();
        let mut stderr = child.stderr.take().unwrap();
        let (sender, messages) = mpsc::channel();
        let reader = thread::spawn(move || {
            let mut stdout = BufReader::new(stdout);
            loop {
                let message = read_message(&mut stdout);
                let terminal = message.as_ref().is_err();
                if sender.send(message).is_err() || terminal {
                    return;
                }
            }
        });
        let errors = thread::spawn(move || {
            let mut output = String::new();
            stderr.read_to_string(&mut output).unwrap();
            output
        });
        Self {
            child,
            input,
            messages,
            pending: Vec::new(),
            reader: Some(reader),
            errors: Some(errors),
        }
    }

    pub fn send(&mut self, message: Value) {
        let bytes = serde_json::to_vec(&message).unwrap();
        let input = self.input.as_mut().unwrap();
        write!(input, "Content-Length: {}\r\n\r\n", bytes.len()).unwrap();
        input.write_all(&bytes).unwrap();
        input.flush().unwrap();
    }

    pub fn notify(&mut self, method: &str, params: Value) {
        self.send(json!({"jsonrpc": "2.0", "method": method, "params": params}));
    }

    pub fn request(&mut self, id: u64, method: &str, params: Value) -> Value {
        self.send(json!({"jsonrpc": "2.0", "id": id, "method": method, "params": params}));
        let response = self.receive(|message| message["id"] == id);
        assert!(response.get("error").is_none(), "{response}");
        response["result"].clone()
    }

    pub fn receive(&mut self, predicate: impl Fn(&Value) -> bool) -> Value {
        if let Some(index) = self.pending.iter().position(&predicate) {
            return self.pending.remove(index);
        }
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            let message = self
                .messages
                .recv_timeout(deadline.saturating_duration_since(Instant::now()))
                .unwrap_or_else(|error| panic!("LSP deadline: {error}; pending={:?}", self.pending))
                .unwrap_or_else(|error| panic!("LSP protocol failure: {error}"));
            if predicate(&message) {
                return message;
            }
            self.pending.push(message);
        }
    }

    pub fn finish(&mut self) {
        assert!(self.request(99, "shutdown", Value::Null).is_null());
        self.notify("exit", Value::Null);
        self.input.take();
        let deadline = Instant::now() + Duration::from_secs(10);
        let status = loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                break status;
            }
            assert!(Instant::now() < deadline, "LSP did not exit after shutdown");
            thread::sleep(Duration::from_millis(10));
        };
        assert!(status.success(), "LSP exit status={status}");
        self.reader.take().unwrap().join().unwrap();
        let errors = self.errors.take().unwrap().join().unwrap();
        assert!(errors.is_empty(), "unexpected LSP stderr: {errors}");
    }
}

impl Drop for Client {
    fn drop(&mut self) {
        self.input.take();
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(reader) = self.reader.take() {
            let _ = reader.join();
        }
        if let Some(errors) = self.errors.take() {
            let _ = errors.join();
        }
    }
}

fn read_message(reader: &mut impl BufRead) -> Result<Value, String> {
    let mut length = None;
    loop {
        let mut line = String::new();
        reader
            .read_line(&mut line)
            .map_err(|error| error.to_string())?;
        if line.is_empty() {
            return Err("server stdout ended".into());
        }
        if line == "\r\n" || line == "\n" {
            break;
        }
        let (name, value) = line
            .split_once(':')
            .ok_or_else(|| format!("nonprotocol server stdout: {line:?}"))?;
        if name.eq_ignore_ascii_case("Content-Length") {
            length = Some(
                value
                    .trim()
                    .parse::<usize>()
                    .map_err(|error| error.to_string())?,
            );
        }
    }
    let length = length.ok_or("missing Content-Length")?;
    if length > 8 * 1024 * 1024 {
        return Err("unbounded server response".into());
    }
    let mut bytes = vec![0; length];
    reader
        .read_exact(&mut bytes)
        .map_err(|error| error.to_string())?;
    serde_json::from_slice(&bytes).map_err(|error| error.to_string())
}
