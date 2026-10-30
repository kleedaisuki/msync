//! Physical input accumulation, independent of terminal editing and execution.

use crate::syntax::{Report, Span};

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Submission {
    Empty,
    Pending,
    Ready(String),
}

#[derive(Clone, Debug, Default)]
enum State {
    #[default]
    Idle,
    Block(String),
}

/// A compound REPL submission is released by a blank physical line or EOF.
/// No indented prefix is returned to the compiler before that boundary.
#[derive(Clone, Debug, Default)]
pub struct SubmissionBuffer {
    state: State,
}

impl SubmissionBuffer {
    pub fn is_pending(&self) -> bool {
        matches!(self.state, State::Block(_))
    }

    pub fn push_line(&mut self, line: &str) -> Result<Submission, Report> {
        let line = line.strip_suffix('\n').unwrap_or(line);
        let line = line.strip_suffix('\r').unwrap_or(line);
        if let Some(index) = line.find('\n') {
            return Err(Report::new(
                "the input editor must submit one physical line at a time",
                Span {
                    start: index,
                    end: index + 1,
                },
            )
            .help("Queue pasted lines in order; a blank line finishes the compound submission."));
        }
        if let State::Block(source) = &mut self.state {
            if line.trim().is_empty() {
                return Ok(Submission::Ready(self.finish().expect("pending block")));
            }
            source.push_str(line);
            source.push('\n');
            return Ok(Submission::Pending);
        }
        if line.trim().is_empty() {
            return Ok(Submission::Empty);
        }
        let source = format!("{line}\n");
        if super::parser::opens_block(line) {
            self.state = State::Block(source);
            Ok(Submission::Pending)
        } else {
            Ok(Submission::Ready(source))
        }
    }

    /// EOF submits the complete buffered text; the parser still checks missing
    /// bodies or malformed indentation. Cancellation instead discards it.
    pub fn finish(&mut self) -> Option<String> {
        match std::mem::take(&mut self.state) {
            State::Idle => None,
            State::Block(source) => Some(source),
        }
    }

    pub fn cancel(&mut self) -> bool {
        self.finish().is_some()
    }
}
