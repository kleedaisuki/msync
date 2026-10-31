use std::collections::BTreeMap;
use std::sync::{Arc, Condvar, Mutex, mpsc};

use lsp_types::{Diagnostic, DiagnosticRelatedInformation, DiagnosticSeverity, Location, Uri};

use crate::compiler::{CompileContext, structured::Session};
use crate::language::Document;
use crate::source::{LocatedReport, Source, SourceSpan};
use crate::syntax::Report;

use super::documents::Text;

pub(super) struct Job {
    pub uri: Uri,
    pub version: i32,
    pub generation: u64,
    pub source: String,
    pub document: Document,
    pub context: CompileContext,
}

pub(super) struct Completed {
    pub uri: Uri,
    pub version: i32,
    pub generation: u64,
    pub diagnostics: Vec<Diagnostic>,
}

#[derive(Default)]
struct Pending {
    jobs: BTreeMap<Uri, Job>,
    stopping: bool,
}

pub(super) struct Analyzer {
    pending: Arc<(Mutex<Pending>, Condvar)>,
    pub completed: mpsc::Receiver<Completed>,
}

impl Analyzer {
    pub fn new() -> Result<Self, String> {
        let pending = Arc::new((Mutex::new(Pending::default()), Condvar::new()));
        let (sender, completed) = mpsc::channel();
        let shared = pending.clone();
        std::thread::Builder::new()
            .name("msync-lsp-analysis".into())
            .spawn(move || worker(shared, sender))
            .map_err(|error| format!("cannot start language-server analysis: {error}"))?;
        Ok(Self { pending, completed })
    }

    pub fn submit(&self, job: Job) -> Result<(), String> {
        let (lock, available) = &*self.pending;
        lock.lock()
            .map_err(|_| "language-server analysis queue is poisoned")?
            .jobs
            .insert(job.uri.clone(), job);
        available.notify_one();
        Ok(())
    }

    pub fn forget(&self, uri: &Uri) {
        if let Ok(mut pending) = self.pending.0.lock() {
            pending.jobs.remove(uri);
        }
    }
}

impl Drop for Analyzer {
    fn drop(&mut self) {
        let (lock, available) = &*self.pending;
        if let Ok(mut pending) = lock.lock() {
            pending.stopping = true;
            pending.jobs.clear();
        }
        available.notify_one();
        // A current pure compiler job need not delay protocol shutdown. The
        // dedicated server process owns its thread and exits without runtime IO.
    }
}

fn worker(shared: Arc<(Mutex<Pending>, Condvar)>, sender: mpsc::Sender<Completed>) {
    loop {
        let Some(job) = next_job(&shared) else {
            return;
        };
        let source = Source::new(job.uri.as_str(), job.source.as_str());
        let errors = Session::new()
            .compile_named(&job.document, &job.context, source)
            .err()
            .unwrap_or_default();
        let text = Text::new(job.source);
        let diagnostics = errors
            .into_iter()
            .map(|report| located_diagnostic(&text, &job.uri, report))
            .collect();
        if sender
            .send(Completed {
                uri: job.uri,
                version: job.version,
                generation: job.generation,
                diagnostics,
            })
            .is_err()
        {
            return;
        }
    }
}

fn next_job(shared: &(Mutex<Pending>, Condvar)) -> Option<Job> {
    let (lock, available) = shared;
    let mut pending = lock.lock().ok()?;
    loop {
        if pending.stopping {
            return None;
        }
        if let Some((_, job)) = pending.jobs.pop_first() {
            return Some(job);
        }
        pending = available.wait(pending).ok()?;
    }
}

fn related(
    site: &SourceSpan,
    message: String,
    current: &Uri,
) -> Option<DiagnosticRelatedInformation> {
    let uri = url::Url::from_file_path(site.source.name.as_ref())
        .ok()
        .and_then(|url| url.as_str().parse().ok())
        .or_else(|| {
            url::Url::parse(&site.source.name)
                .ok()
                .and_then(|url| url.as_str().parse::<Uri>().ok())
        })
        .or_else(|| (site.source.name.as_ref() == current.as_str()).then(|| current.clone()))?;
    let text = Text::new(site.source.text.to_string());
    Some(DiagnosticRelatedInformation {
        location: Location::new(uri, text.range(site.span)),
        message,
    })
}

fn located_diagnostic(text: &Text, uri: &Uri, report: LocatedReport) -> Diagnostic {
    let mut related_information = Vec::new();
    if report.source.name.as_ref() != uri.as_str() {
        let site = report.source.at(report.report.diagnostic.span);
        related_information.extend(related(
            &site,
            report.report.diagnostic.message.clone(),
            uri,
        ));
        for label in &report.report.labels {
            related_information.extend(related(
                &report.source.at(label.span),
                label.message.clone(),
                uri,
            ));
        }
    }
    for label in &report.related {
        related_information.extend(related(&label.site, label.message.clone(), uri));
    }
    let mut diagnostic = diagnostic(text, uri, report.into_report());
    related_information.extend(diagnostic.related_information.take().unwrap_or_default());
    diagnostic.related_information =
        (!related_information.is_empty()).then_some(related_information);
    diagnostic
}

pub(super) fn diagnostic(text: &Text, uri: &Uri, report: Report) -> Diagnostic {
    let related_information = report
        .labels
        .iter()
        .map(|label| DiagnosticRelatedInformation {
            location: Location::new(uri.clone(), text.range(label.span)),
            message: label.message.clone(),
        })
        .collect::<Vec<_>>();
    let mut message = report.diagnostic.message;
    for help in report.help {
        message.push_str("\nHelp: ");
        message.push_str(&help);
    }
    Diagnostic {
        range: text.range(report.diagnostic.span),
        severity: Some(DiagnosticSeverity::ERROR),
        source: Some("msync".into()),
        message,
        related_information: (!related_information.is_empty()).then_some(related_information),
        ..Diagnostic::default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::syntax::Span;
    use lsp_types::{Position, Range};

    #[test]
    fn include_diagnostics_preserve_child_uri_and_child_utf16_coordinates() {
        let uri: Uri = "file:///project/main.msync".parse().unwrap();
        let parent = Source::new(uri.as_str(), "include \"child.msync\"\n");
        let child = Source::new(
            "file:///project/child.msync",
            "# 🎇\nflag = \"🎇\" == true\n",
        );
        let start = child.text.find("==").unwrap();
        let report = LocatedReport::new(
            child.clone(),
            Report::new(
                "incompatible comparison",
                Span {
                    start,
                    end: start + 2,
                },
            ),
        )
        .in_context(parent.at(Span { start: 8, end: 21 }));
        let text = Text::new(parent.text.to_string());
        let diagnostic = located_diagnostic(&text, &uri, report);
        assert_eq!(diagnostic.range.start, Position::new(0, 8));
        let related = diagnostic.related_information.unwrap();
        assert_eq!(
            related[0].location.uri.as_str(),
            "file:///project/child.msync"
        );
        assert_eq!(
            related[0].location.range,
            Range::new(Position::new(1, 12), Position::new(1, 14))
        );
    }
}
