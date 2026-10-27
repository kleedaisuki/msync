//! A real native pseudoterminal, not a piped-stdin approximation of an editor.

use std::io::{Read, Write};
use std::path::Path;
use std::sync::mpsc::{self, Receiver};
use std::thread;
use std::time::{Duration, Instant};

#[cfg(unix)]
#[path = "pty_unix.rs"]
mod platform;
#[cfg(windows)]
#[path = "pty_windows.rs"]
mod platform;

pub struct Terminal {
    child: platform::Process,
    output: Receiver<Vec<u8>>,
    bytes: Vec<u8>,
    checkpoint: usize,
}

impl Terminal {
    pub fn spawn(executable: &Path, directory: &Path, color: &str) -> Self {
        let (child, mut reader) = platform::Process::spawn(executable, directory, color).unwrap();
        let (sender, output) = mpsc::channel();
        thread::spawn(move || {
            let mut buffer = [0_u8; 8192];
            loop {
                match reader.read(&mut buffer) {
                    Ok(0) | Err(_) => break,
                    Ok(count) if sender.send(buffer[..count].to_vec()).is_err() => break,
                    Ok(_) => {}
                }
            }
        });
        Self {
            child,
            output,
            bytes: Vec::new(),
            checkpoint: 0,
        }
    }

    pub fn send(&mut self, text: &str) {
        self.child.input().write_all(text.as_bytes()).unwrap();
        self.child.input().flush().unwrap();
    }

    pub fn mark(&mut self) {
        while let Ok(bytes) = self.output.try_recv() {
            self.bytes.extend_from_slice(&bytes);
        }
        self.checkpoint = self.bytes.len();
    }

    pub fn since_mark(&self) -> String {
        String::from_utf8_lossy(&self.bytes[self.checkpoint..]).into_owned()
    }

    pub fn until(&mut self, expected: &str) -> String {
        self.until_matching(|raw| visible(raw).contains(expected))
    }

    pub fn until_followed_by(&mut self, first: &str, second: &str) -> String {
        self.until_matching(|raw| {
            let text = visible(raw);
            text.find(first)
                .is_some_and(|index| text[index + first.len()..].contains(second))
        })
    }

    pub fn prompt(&mut self) -> String {
        self.until_matching(prompt_ready)
    }

    pub fn accept(&mut self) -> String {
        self.mark();
        self.send("\r");
        self.prompt()
    }

    pub fn submit(&mut self, line: &str) -> String {
        self.mark();
        self.send(line);
        self.until(line);
        self.accept()
    }

    pub fn until_matching(&mut self, predicate: impl Fn(&str) -> bool) -> String {
        self.until_matching_within(Duration::from_secs(30), predicate)
    }

    pub fn until_matching_within(
        &mut self,
        timeout: Duration,
        predicate: impl Fn(&str) -> bool,
    ) -> String {
        let deadline = Instant::now() + timeout;
        loop {
            let recent = self.since_mark();
            if predicate(&recent) {
                return recent;
            }
            let next = self
                .output
                .recv_timeout(deadline.saturating_duration_since(Instant::now()));
            match next {
                Ok(bytes) => self.bytes.extend_from_slice(&bytes),
                Err(error) => panic!(
                    "terminal condition not reached: {error}; recent={recent:?}; child={:?}",
                    self.child.status()
                ),
            }
        }
    }

    pub fn exit(mut self) {
        self.prompt();
        self.send("/quit\r");
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            if let Some(code) = self.child.status().unwrap() {
                assert_eq!(code, 0, "terminal child failed: {:?}", self.since_mark());
                return;
            }
            assert!(Instant::now() < deadline, "terminal child did not exit");
            thread::sleep(Duration::from_millis(10));
        }
    }
}

fn prompt_ready(raw: &str) -> bool {
    #[cfg(unix)]
    let raw = {
        // Rustyline's synchronized redraw boundary is the readiness signal;
        // observing an intermediate prompt prefix is not enough to send the
        // next statement without racing terminal input-reader setup.
        let Some((_, frame)) = raw.rsplit_once("\u{1b}[?2026h") else {
            return false;
        };
        let Some((frame, _)) = frame.split_once("\u{1b}[?2026l") else {
            return false;
        };
        frame
    };
    visible(raw)
        .rsplit_once("msync>")
        .is_some_and(|(_, input)| input.trim().is_empty())
}

pub fn visible(raw: &str) -> String {
    styled_characters(raw)
        .into_iter()
        .map(|(character, _)| character)
        .collect()
}

pub fn color_of(raw: &str, lexeme: &str) -> Option<String> {
    let characters = styled_characters(raw);
    let text: String = characters.iter().map(|(character, _)| character).collect();
    let start = text.rfind(lexeme)?;
    let ordinal = text[..start].chars().count();
    characters.get(ordinal).and_then(|(_, style)| style.clone())
}

fn styled_characters(raw: &str) -> Vec<(char, Option<String>)> {
    let mut characters = raw.chars().peekable();
    let mut style = None;
    let mut output = Vec::new();
    while let Some(character) = characters.next() {
        if character != '\u{1b}' {
            if !character.is_control() {
                output.push((character, style.clone()));
            }
            continue;
        }
        let next = characters.next();
        if next == Some(']') {
            while let Some(next) = characters.next() {
                if next == '\u{7}' || (next == '\u{1b}' && characters.peek() == Some(&'\\')) {
                    if next == '\u{1b}' {
                        characters.next();
                    }
                    break;
                }
            }
            continue;
        }
        if next != Some('[') {
            continue;
        }
        let mut parameters = String::new();
        for next in characters.by_ref() {
            if ('@'..='~').contains(&next) {
                if next == 'm' {
                    let values: Vec<_> = parameters
                        .split(';')
                        .filter_map(|part| part.parse::<u16>().ok())
                        .collect();
                    if values.is_empty() || values.contains(&0) || values.contains(&39) {
                        style = None;
                    }
                    if values
                        .iter()
                        .any(|value| (30..=38).contains(value) || (90..=97).contains(value))
                    {
                        style = Some(parameters.clone());
                    }
                }
                break;
            }
            parameters.push(next);
        }
    }
    output
}
