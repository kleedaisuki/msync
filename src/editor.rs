use std::borrow::Cow;
use std::collections::VecDeque;
use std::io::{self, BufRead, IsTerminal, Write};
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};

use msync::highlight::{BUILTINS, COMMANDS, KEYWORDS, RANGE_ADAPTORS, highlight};
use rustyline::completion::{Completer, Pair};
use rustyline::config::{Behavior, ColorMode};
use rustyline::error::ReadlineError;
use rustyline::highlight::{CmdKind, Highlighter};
use rustyline::hint::{Hint, Hinter, HistoryHinter};
use rustyline::history::MemHistory;
use rustyline::validate::{ValidationContext, ValidationResult, Validator};
use rustyline::{Config, Context, Editor, GraphemeClusterMode, Helper};

pub(super) enum InputEvent {
    Line(String),
    Interrupted,
    Eof,
}

pub(super) struct Input {
    editor: Option<Editor<LineHelper, MemHistory>>,
    interactive: bool,
    notice: Arc<AtomicU8>,
    pending: VecDeque<String>,
}

pub(super) struct NoticePrinter {
    external: Option<Box<dyn rustyline::ExternalPrinter + Send>>,
}

impl NoticePrinter {
    pub(super) fn refresh(&mut self) -> Result<(), String> {
        if let Some(printer) = &mut self.external {
            return printer.print(String::new()).map_err(|error| {
                format!("cannot refresh prompt after background diagnostic: {error}")
            });
        }
        Ok(())
    }

    pub(super) fn print(&mut self, text: String) -> Result<(), String> {
        if let Some(printer) = &mut self.external {
            return printer
                .print(text)
                .map_err(|error| format!("cannot display background notice: {error}"));
        }
        let mut output = io::stdout().lock();
        output
            .write_all(text.as_bytes())
            .and_then(|()| output.flush())
            .map_err(|error| format!("cannot display background notice: {error}"))
    }
}

pub(super) fn basic_terminal() -> bool {
    std::env::var("TERM").is_ok_and(|term| {
        ["dumb", "cons25", "emacs"]
            .iter()
            .any(|name| term.eq_ignore_ascii_case(name))
    })
}

impl Input {
    pub(super) fn new(color: bool) -> Result<Self, String> {
        let interactive = io::stdin().is_terminal() && io::stdout().is_terminal();
        let enhanced = interactive && !basic_terminal();
        let notice = Arc::new(AtomicU8::new(0));
        let editor = if enhanced {
            // Keep history inside this editor. No home-directory discovery,
            // plaintext history file or filesystem/network probe occurs per key.
            let config = Config::builder()
                .max_history_size(1000)
                .map_err(|error| error.to_string())?
                .history_ignore_dups(true)
                .map_err(|error| error.to_string())?
                .history_ignore_space(true)
                .behavior(Behavior::Stdio)
                .color_mode(ColorMode::Forced)
                .build();
            let history = MemHistory::with_config(&config);
            let mode = config.grapheme_cluster_mode();
            let mut editor = Editor::with_history(config, history)
                .map_err(|error| format!("cannot initialize the terminal editor: {error}"))?;
            editor.set_helper(Some(LineHelper {
                color,
                history: HistoryHinter::new(),
                notice: notice.clone(),
                mode,
            }));
            editor.bind_sequence(
                rustyline::KeyEvent::ctrl('Q'),
                rustyline::EventHandler::Conditional(Box::new(QuotedControl {
                    notice: notice.clone(),
                })),
            );
            #[cfg(unix)]
            editor.bind_sequence(
                rustyline::KeyEvent::ctrl('V'),
                rustyline::EventHandler::Conditional(Box::new(QuotedControl {
                    notice: notice.clone(),
                })),
            );
            #[cfg(windows)]
            editor.bind_sequence(
                rustyline::KeyEvent::ctrl('V'),
                rustyline::EventHandler::Conditional(Box::new(Clipboard {
                    notice: notice.clone(),
                })),
            );
            Some(editor)
        } else {
            None
        };
        Ok(Self {
            editor,
            interactive,
            notice,
            pending: VecDeque::new(),
        })
    }

    pub(super) fn interactive(&self) -> bool {
        self.interactive
    }

    pub(super) fn editing(&self) -> bool {
        self.editor.is_some()
    }

    pub(super) fn external_printer(&mut self) -> Result<NoticePrinter, String> {
        let external = self
            .editor
            .as_mut()
            .map(|editor| {
                editor
                    .create_external_printer()
                    .map(|printer| Box::new(printer) as Box<dyn rustyline::ExternalPrinter + Send>)
            })
            .transpose()
            .map_err(|error| {
                format!("cannot initialize prompt-safe background notices: {error}")
            })?;
        Ok(NoticePrinter { external })
    }

    pub(super) fn read(&mut self, prompt: &str) -> Result<InputEvent, String> {
        if let Some(line) = self.pending.pop_front() {
            return Ok(InputEvent::Line(line));
        }
        if let Some(editor) = &mut self.editor {
            self.notice.store(0, Ordering::Relaxed);
            return match editor.readline(prompt) {
                Ok(line) => {
                    editor
                        .add_history_entry(line.as_str())
                        .map_err(|error| error.to_string())?;
                    self.pending
                        .extend(line.split_inclusive('\n').map(str::to_owned));
                    Ok(InputEvent::Line(
                        self.pending.pop_front().unwrap_or_default(),
                    ))
                }
                Err(ReadlineError::Interrupted) => Ok(InputEvent::Interrupted),
                Err(ReadlineError::Eof) => Ok(InputEvent::Eof),
                Err(error) => Err(format!("cannot read terminal input: {error}")),
            };
        }
        if self.interactive {
            super::print_text(prompt).map_err(|error| error.to_string())?;
            io::stdout().flush().map_err(|error| error.to_string())?;
        }
        let mut line = String::new();
        match io::stdin().lock().read_line(&mut line) {
            Ok(0) => Ok(InputEvent::Eof),
            Ok(_) => Ok(InputEvent::Line(line)),
            Err(error) if error.kind() == io::ErrorKind::Interrupted => Ok(InputEvent::Interrupted),
            Err(error) => Err(format!("cannot read REPL input: {error}")),
        }
    }

    pub(super) fn clear(&mut self) -> Result<bool, String> {
        match &mut self.editor {
            Some(editor) => clear_viewport(editor)
                .map(|()| true)
                .map_err(|error| error.to_string()),
            None => Ok(false),
        }
    }
}

#[cfg(not(windows))]
fn clear_viewport(editor: &mut Editor<LineHelper, MemHistory>) -> rustyline::Result<()> {
    editor.clear_screen()
}

#[cfg(windows)]
fn clear_viewport(_: &mut Editor<LineHelper, MemHistory>) -> rustyline::Result<()> {
    use windows_sys::Win32::System::Console::*;
    // Erase only the visible window. Upstream clear_screen clears the entire
    // Windows screen buffer, which can also discard the user's scrollback.
    let handle = unsafe { GetStdHandle(STD_OUTPUT_HANDLE) };
    let mut info = std::mem::MaybeUninit::<CONSOLE_SCREEN_BUFFER_INFO>::uninit();
    if unsafe { GetConsoleScreenBufferInfo(handle, info.as_mut_ptr()) } == 0 {
        return Err(io::Error::last_os_error().into());
    }
    let info = unsafe { info.assume_init() };
    let width = (i32::from(info.srWindow.Right) - i32::from(info.srWindow.Left) + 1) as u32;
    for row in info.srWindow.Top..=info.srWindow.Bottom {
        let position = COORD {
            X: info.srWindow.Left,
            Y: row,
        };
        let mut written = 0;
        if unsafe {
            FillConsoleOutputCharacterW(handle, b' ' as u16, width, position, &mut written)
        } == 0
            || unsafe {
                FillConsoleOutputAttribute(handle, info.wAttributes, width, position, &mut written)
            } == 0
        {
            return Err(io::Error::last_os_error().into());
        }
    }
    if unsafe {
        SetConsoleCursorPosition(
            handle,
            COORD {
                X: info.srWindow.Left,
                Y: info.srWindow.Top,
            },
        )
    } == 0
    {
        return Err(io::Error::last_os_error().into());
    }
    Ok(())
}

struct LineHelper {
    color: bool,
    history: HistoryHinter,
    notice: Arc<AtomicU8>,
    mode: GraphemeClusterMode,
}

impl Helper for LineHelper {}

impl Highlighter for LineHelper {
    fn highlight<'l>(&self, line: &'l str, _: usize) -> Cow<'l, str> {
        // Match the editor's selected width model: WcWidth measures C0 as zero,
        // Unicode mode as one. Diagnostic escape expansion would move the cursor.
        // ESC cannot enter the buffer: upstream filters Unix paste, and the
        // Windows clipboard binding rejects it before insertion.
        if !line
            .chars()
            .any(|ch| ch.is_control() && !matches!(ch, '\n' | '\t'))
        {
            return if self.color {
                Cow::Owned(highlight(line, true))
            } else {
                Cow::Borrowed(line)
            };
        }
        let display = editor_display(line, self.mode);
        Cow::Owned(highlight(&display, self.color))
    }

    fn highlight_prompt<'b, 's: 'b, 'p: 'b>(&'s self, prompt: &'p str, _: bool) -> Cow<'b, str> {
        if self.color {
            Cow::Owned(format!("\x1b[1;36m{prompt}\x1b[0m"))
        } else {
            Cow::Borrowed(prompt)
        }
    }

    fn highlight_hint<'h>(&self, hint: &'h str) -> Cow<'h, str> {
        if self.color {
            Cow::Owned(format!("\x1b[2m{hint}\x1b[0m"))
        } else {
            Cow::Borrowed(hint)
        }
    }

    fn highlight_char(&self, _: &str, _: usize, _: CmdKind) -> bool {
        true
    }
}

impl Completer for LineHelper {
    type Candidate = Pair;

    fn complete(
        &self,
        line: &str,
        pos: usize,
        _: &Context<'_>,
    ) -> rustyline::Result<(usize, Vec<Pair>)> {
        let Some((start, prefix, candidates)) = completion_prefix(line, pos) else {
            return Ok((pos, Vec::new()));
        };
        Ok((
            start,
            candidates
                .iter()
                .map(|candidate| completion_spelling(candidate, prefix))
                .filter(|candidate| candidate.starts_with(prefix))
                .map(|candidate| Pair {
                    display: candidate.to_string(),
                    replacement: candidate.into_owned(),
                })
                .collect(),
        ))
    }
}

fn completion_prefix(line: &str, pos: usize) -> Option<(usize, &str, &'static [&'static str])> {
    if pos > line.len() || !line.is_char_boundary(pos) {
        return None;
    }
    let before = &line[..pos];
    if let Some((command, _)) = before.trim_start().split_once(char::is_whitespace)
        && matches!(command, "/bash" | "/pwsh" | ":bash" | ":pwsh")
    {
        return None;
    }
    if msync::highlight::token_spans(before)
        .last()
        .is_some_and(|token| {
            matches!(
                token.kind,
                msync::highlight::TokenKind::Comment
                    | msync::highlight::TokenKind::Selector
                    | msync::highlight::TokenKind::String
            )
        })
    {
        return None;
    }
    let start = before
        .char_indices()
        .rev()
        .find(|(_, ch)| ch.is_whitespace() || matches!(ch, '(' | ')' | ','))
        .map_or(0, |(index, ch)| index + ch.len_utf8());
    let prefix = &before[start..];
    if prefix.starts_with(['/', ':']) && before[..start].trim().is_empty() {
        Some((start, prefix, COMMANDS))
    } else if prefix.starts_with('$') {
        Some((start, prefix, BUILTINS))
    } else if prefix.starts_with("views::") || prefix.starts_with("ranges::") {
        Some((start, prefix, RANGE_ADAPTORS))
    } else if !prefix.is_empty() && prefix.chars().all(|ch| ch.is_alphanumeric() || ch == '_') {
        Some((start, prefix, KEYWORDS))
    } else {
        None
    }
}

fn completion_spelling<'a>(candidate: &'a str, prefix: &str) -> Cow<'a, str> {
    if prefix.starts_with(':') && candidate.starts_with('/') {
        Cow::Owned(format!(":{}", &candidate[1..]))
    } else {
        Cow::Borrowed(candidate)
    }
}

impl Hinter for LineHelper {
    type Hint = EditorHint;

    fn hint(&self, line: &str, pos: usize, context: &Context<'_>) -> Option<EditorHint> {
        let notice = match self.notice.load(Ordering::Relaxed) {
            1 => Some(" [paste rejected: use spaces or quoted escapes instead of raw controls]"),
            2 => Some(" [clipboard unavailable: copy text and retry]"),
            3 => Some(" [literal control insertion is disabled; use quoted DSL escapes]"),
            _ => None,
        };
        if let Some(notice) = notice {
            return Some(EditorHint {
                text: notice.into(),
                insertable: false,
            });
        }
        if invalid_controls(line, false) {
            return Some(EditorHint {
                text: " [control characters: use quoted escapes or Ctrl-C to discard]".into(),
                insertable: false,
            });
        }
        if !self.color {
            return None;
        }
        if pos != line.len() {
            return None;
        }
        if let Some((_, prefix, candidates)) = completion_prefix(line, pos) {
            let mut matches = candidates
                .iter()
                .map(|candidate| completion_spelling(candidate, prefix))
                .filter(|candidate| candidate.starts_with(prefix));
            if let Some(candidate) = matches.next()
                && matches.next().is_none()
                && candidate != prefix
            {
                return Some(EditorHint {
                    text: candidate[prefix.len()..].to_owned(),
                    insertable: true,
                });
            }
        }
        self.history
            .hint(line, pos, context)
            .map(|text| EditorHint {
                text,
                insertable: true,
            })
    }
}

struct EditorHint {
    text: String,
    insertable: bool,
}

struct QuotedControl {
    notice: Arc<AtomicU8>,
}

impl rustyline::ConditionalEventHandler for QuotedControl {
    fn handle(
        &self,
        _: &rustyline::Event,
        _: rustyline::RepeatCount,
        _: bool,
        _: &rustyline::EventContext<'_>,
    ) -> Option<rustyline::Cmd> {
        // QuotedInsert would bypass the key decoder and put a raw ESC into the
        // buffer. The language already provides explicit quoted escapes.
        self.notice.store(3, Ordering::Relaxed);
        Some(rustyline::Cmd::Repaint)
    }
}

impl Hint for EditorHint {
    fn display(&self) -> &str {
        &self.text
    }
    fn completion(&self) -> Option<&str> {
        self.insertable.then_some(self.text.as_str())
    }
}

impl Validator for LineHelper {
    fn validate(&self, context: &mut ValidationContext<'_>) -> rustyline::Result<ValidationResult> {
        if invalid_controls(context.input(), false) {
            return Ok(ValidationResult::Invalid(Some(
                "Control characters are not source text; use quoted escapes or Ctrl-C to discard this input.".into(),
            )));
        }
        Ok(ValidationResult::Valid(None))
    }
}

fn invalid_controls(text: &str, clipboard: bool) -> bool {
    text.char_indices().any(|(index, ch)| {
        ch.is_control()
            && ch != '\n'
            && !(ch == '\t' && !clipboard)
            && !(ch == '\r' && text[index + 1..].starts_with('\n'))
    })
}

fn editor_display(line: &str, mode: GraphemeClusterMode) -> String {
    let mut output = String::with_capacity(line.len());
    for ch in line.chars() {
        if ch.is_control() && !matches!(ch, '\n' | '\t') {
            let mut encoded = [0_u8; 4];
            let width = mode.width(ch.encode_utf8(&mut encoded));
            output.extend(std::iter::repeat_n(' ', usize::from(width)));
        } else {
            output.push(ch);
        }
    }
    output
}

#[cfg(windows)]
struct Clipboard {
    notice: Arc<AtomicU8>,
}

#[cfg(windows)]
impl rustyline::ConditionalEventHandler for Clipboard {
    fn handle(
        &self,
        _: &rustyline::Event,
        count: rustyline::RepeatCount,
        _: bool,
        _: &rustyline::EventContext<'_>,
    ) -> Option<rustyline::Cmd> {
        // The upstream Windows renderer assumes a buffer without embedded ANSI
        // escapes. Reject unsupported clipboard controls before LineBuffer sees
        // them, preserving normal Unicode/CRLF paste without rewriting source.
        match clipboard_win::get_clipboard_string() {
            Ok(text) if !invalid_controls(&text, true) => {
                self.notice.store(0, Ordering::Relaxed);
                // Match the upstream Unix paste path's newline convention.
                Some(rustyline::Cmd::Insert(count, text.replace("\r\n", "\n")))
            }
            Ok(_) => {
                self.notice.store(1, Ordering::Relaxed);
                Some(rustyline::Cmd::Repaint)
            }
            Err(_) => {
                self.notice.store(2, Ordering::Relaxed);
                Some(rustyline::Cmd::Repaint)
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn completion_is_static_utf8_safe_and_never_replaces_prior_text() {
        let (start, prefix, candidates) =
            completion_prefix("目标 -> $fi", "目标 -> $fi".len()).unwrap();
        assert_eq!(&"目标 -> $fi"[..start], "目标 -> ");
        assert_eq!(prefix, "$fi");
        assert!(candidates.contains(&"$file::this"));
        assert!(completion_prefix("文件", 1).is_none());
        assert!(completion_prefix("x", 10).is_none());
        assert!(completion_prefix("path/to/file", 12).is_none());
        for line in [
            "/bash echo $pw",
            "/pwsh Write-Output $pw",
            "# $pw",
            "dir/<$pw",
            "root := \"word $pw",
        ] {
            assert!(completion_prefix(line, line.len()).is_none(), "{line}");
        }
        assert_eq!(
            completion_prefix("x\u{2003}$fi", "x\u{2003}$fi".len())
                .unwrap()
                .1,
            "$fi"
        );
    }

    #[test]
    fn v6_keyword_and_range_completion_preserves_the_typed_boundary() {
        for (line, expected) in [
            ("boole", "boolean"),
            ("files | views::fi", "views::filter"),
            ("files | ranges::any", "ranges::any_of"),
        ] {
            let (start, prefix, candidates) = completion_prefix(line, line.len()).unwrap();
            assert_eq!(&line[start..], prefix);
            assert!(candidates.contains(&expected));
        }
        for line in ["delete \"boole", "files/<boole", "# boole"] {
            assert!(completion_prefix(line, line.len()).is_none());
        }
    }

    #[test]
    fn namespace_completion_inside_calls_preserves_the_callee_and_prior_arguments() {
        for (prior, expected) in [
            ("copy(", "$fi"),
            ("copy(source,", "$pw"),
            ("copy(\"字节\", ", "$lo"),
        ] {
            let line = format!("{prior}{expected}");
            let (start, prefix, candidates) = completion_prefix(&line, line.len()).unwrap();
            assert_eq!(prefix, expected);
            assert_eq!(&line[..start], prior);
            assert!(
                candidates
                    .iter()
                    .any(|candidate| candidate.starts_with(prefix))
            );
        }
        for line in ["copy(\"$pw", "copy(dir/<$pw", "/bash echo copy($pw"] {
            assert!(completion_prefix(line, line.len()).is_none(), "{line}");
        }
    }

    #[test]
    fn colon_command_completion_retains_the_typed_prefix() {
        let (start, prefix, candidates) = completion_prefix(":hel", 4).unwrap();
        assert_eq!(start, 0);
        let spellings: Vec<_> = candidates
            .iter()
            .map(|candidate| completion_spelling(candidate, prefix))
            .filter(|candidate| candidate.starts_with(prefix))
            .collect();
        assert_eq!(spellings, vec![":help"]);
        assert!(completion_prefix(":bash echo $pw", 14).is_none());
    }

    #[test]
    fn color_never_helper_does_not_emit_sgr_but_still_escapes_raw_controls() {
        let helper = LineHelper {
            color: false,
            history: HistoryHinter::new(),
            notice: Arc::new(AtomicU8::new(0)),
            mode: GraphemeClusterMode::WcWidth,
        };
        assert_eq!(helper.highlight("$pwd::pwd -> x", 0), "$pwd::pwd -> x");
        assert!(matches!(
            helper.highlight("$pwd::pwd -> x", 0),
            Cow::Borrowed(_)
        ));
        assert_eq!(helper.highlight("x\0\x07y", 0), "xy");
        for mode in [
            GraphemeClusterMode::Unicode,
            GraphemeClusterMode::WcWidth,
            GraphemeClusterMode::NoZwj,
        ] {
            let source = "x\0\x07y";
            assert_eq!(
                mode.width(source),
                mode.width(&editor_display(source, mode))
            );
        }
        assert!(invalid_controls("x\0y", false));
        assert!(invalid_controls("x\x1b[2J", true));
        assert!(!invalid_controls("first\r\nsecond\n中文", true));
        assert!(invalid_controls("x\ty", true));
    }
}
