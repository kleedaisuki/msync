//! Delayed terminal-only observation of genuine execution phases.

use std::io::{self, IsTerminal, Write};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use msync::execution::progress::{Progress, Snapshot};
use unicode_width::UnicodeWidthChar;

use super::{Color, editor, presentation};

const DELAY: Duration = Duration::from_millis(200);
const INTERVAL: Duration = Duration::from_millis(250);

pub(super) struct Reporter {
    progress: Progress,
    worker: Option<JoinHandle<io::Result<()>>>,
}

impl Reporter {
    pub(super) fn start(color: Color) -> io::Result<Option<Self>> {
        if !io::stderr().is_terminal() {
            return Ok(None);
        }
        let inline = color.stderr() && !editor::basic_terminal();
        let progress = Progress::new();
        let observer = progress.clone();
        let worker = thread::Builder::new()
            .name("msync-progress".into())
            .spawn(move || report(observer, &mut io::stderr(), inline))?;
        Ok(Some(Self {
            progress,
            worker: Some(worker),
        }))
    }

    pub(super) fn handle(&self) -> Progress {
        self.progress.clone()
    }

    pub(super) fn finish(mut self) -> io::Result<()> {
        self.stop()
    }

    fn stop(&mut self) -> io::Result<()> {
        self.progress.finish();
        match self.worker.take() {
            Some(worker) => worker
                .join()
                .map_err(|_| io::Error::other("execution progress observer failed"))?,
            None => Ok(()),
        }
    }
}

impl Drop for Reporter {
    fn drop(&mut self) {
        let _ = self.stop();
    }
}

fn report(progress: Progress, output: &mut impl Write, inline: bool) -> io::Result<()> {
    let mut next = Instant::now() + DELAY;
    let mut visible = false;
    let mut revision = None;
    let mut second = 0;
    loop {
        let snapshot = progress.wait(next.saturating_duration_since(Instant::now()));
        if snapshot.finished {
            return clear(output, inline && visible);
        }
        if Instant::now() < next {
            continue;
        }
        next = Instant::now() + INTERVAL;
        if inline || revision != Some(snapshot.revision) || second != snapshot.elapsed.as_secs() {
            render(output, &snapshot, inline)?;
            visible = true;
            revision = Some(snapshot.revision);
            second = snapshot.elapsed.as_secs();
        }
    }
}

fn render(output: &mut impl Write, snapshot: &Snapshot, inline: bool) -> io::Result<()> {
    let text = describe(snapshot);
    if inline {
        write!(
            output,
            "\r\x1b[2K{}",
            presentation::paint(&text, "36", true)
        )?;
    } else {
        writeln!(output, "{text}")?;
    }
    output.flush()
}

fn clear(output: &mut impl Write, visible: bool) -> io::Result<()> {
    if visible {
        write!(output, "\r\x1b[2K")?;
        output.flush()?;
    }
    Ok(())
}

fn describe(snapshot: &Snapshot) -> String {
    let event = &snapshot.event;
    let instruction = event
        .instruction
        .map(|id| format!(" #{}", id.0))
        .unwrap_or_default();
    let mut text = format!(
        "working: {}{instruction} [{:.1}s]",
        event.phase.label(),
        snapshot.elapsed.as_secs_f64()
    );
    if let Some(source) = &event.source {
        text.push(' ');
        text.push_str(&presentation::escape(source));
    }
    if let Some(destination) = &event.destination {
        text.push_str(" -> ");
        text.push_str(&presentation::escape(destination));
    }
    if event.files != 0 || event.bytes != 0 {
        text.push_str(&format!(
            " (input: {} files, {} bytes)",
            event.files, event.bytes
        ));
    }
    // Keep a progress redraw on one ordinary terminal line, including CJK paths.
    truncate(&text, 76)
}

fn truncate(text: &str, limit: usize) -> String {
    let mut width = 0;
    for (index, character) in text.char_indices() {
        width += character.width().unwrap_or(0);
        if width > limit.saturating_sub(1) {
            return format!("{}…", &text[..index]);
        }
    }
    text.to_owned()
}

#[cfg(test)]
mod tests {
    use msync::execution::progress::{Event, Phase};

    use super::*;

    #[test]
    fn a_finished_fast_operation_emits_nothing() {
        let progress = Progress::new();
        progress.finish();
        let mut output = Vec::new();
        report(progress, &mut output, true).unwrap();
        assert!(output.is_empty());
    }

    #[test]
    fn monochrome_progress_contains_no_terminal_controls_or_payload_guess() {
        let progress = Progress::new();
        progress.update(Event {
            phase: Phase::Acquire,
            source: Some("source\x1b[31m\n可莉".into()),
            destination: Some("target".into()),
            ..Event::default()
        });
        let mut output = Vec::new();
        render(&mut output, &progress.snapshot(), false).unwrap();
        let text = String::from_utf8(output).unwrap();
        assert!(text.contains("acquire source"));
        assert!(text.contains("target"));
        assert!(!text.contains('\x1b'));
        assert_eq!(text.lines().count(), 1);
        assert!(!text.contains('%'));
    }

    #[test]
    fn inline_progress_clears_its_line_before_completion_output() {
        let progress = Progress::new();
        let mut output = Vec::new();
        render(&mut output, &progress.snapshot(), true).unwrap();
        clear(&mut output, true).unwrap();
        assert!(output.ends_with(b"\r\x1b[2K"));
    }

    #[test]
    fn a_long_wide_path_cannot_wrap_the_progress_line() {
        let progress = Progress::new();
        progress.update(Event {
            source: Some("可莉".repeat(100)),
            ..Event::default()
        });
        let text = describe(&progress.snapshot());
        assert!(text.ends_with('…'));
        assert!(text.chars().map(|c| c.width().unwrap_or(0)).sum::<usize>() <= 76);
    }
}
