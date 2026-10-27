//! Owned source identities and cross-source diagnostic presentation.

use std::collections::BTreeMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use crate::hir::InstructionId;
use crate::syntax::{self, Label, Report, Span};

static NEXT_SOURCE: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct SourceId(u64);

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Source {
    pub id: SourceId,
    pub name: Arc<str>,
    pub text: Arc<str>,
}

impl Source {
    pub fn new(name: impl Into<Arc<str>>, text: impl Into<Arc<str>>) -> Arc<Self> {
        Arc::new(Self {
            id: SourceId(NEXT_SOURCE.fetch_add(1, Ordering::Relaxed)),
            name: name.into(),
            text: text.into(),
        })
    }

    pub fn at(self: &Arc<Self>, span: Span) -> SourceSpan {
        SourceSpan {
            source: self.clone(),
            span,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SourceSpan {
    pub source: Arc<Source>,
    pub span: Span,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SourceLabel {
    pub site: SourceSpan,
    pub message: String,
}

/// The stable `Report` remains a single-source value. Related locations carry
/// their own source ownership instead of reinterpreting foreign byte offsets.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LocatedReport {
    pub source: Arc<Source>,
    pub report: Box<Report>,
    pub related: Vec<SourceLabel>,
    context: Option<Box<SourceSpan>>,
}

impl LocatedReport {
    pub fn new(source: Arc<Source>, report: Report) -> Self {
        Self {
            source,
            report: Box::new(report),
            related: Vec::new(),
            context: None,
        }
    }

    pub fn label(mut self, site: SourceSpan, message: impl Into<String>) -> Self {
        let message = message.into();
        if site.source.id == self.source.id {
            self.report.labels.push(Label {
                span: site.span,
                message,
            });
        } else {
            self.related.push(SourceLabel { site, message });
        }
        self
    }

    pub fn in_context(mut self, site: SourceSpan) -> Self {
        self.context = Some(Box::new(site));
        self
    }

    pub fn render(&self, color: bool) -> String {
        let mut output =
            syntax::render_report_named(&self.source.text, &self.report, &self.source.name, color);
        let mut grouped = BTreeMap::<SourceId, (Arc<Source>, Vec<Label>)>::new();
        for label in &self.related {
            grouped
                .entry(label.site.source.id)
                .or_insert_with(|| (label.site.source.clone(), Vec::new()))
                .1
                .push(Label {
                    span: label.site.span,
                    message: label.message.clone(),
                });
        }
        for (_, (source, labels)) in grouped {
            output.push('\n');
            output.push_str(&syntax::render_related_named(
                &source.text,
                &labels,
                &source.name,
                color,
            ));
        }
        output
    }

    /// Compatibility callers still receive a `Report`; foreign locations are
    /// named in help rather than placed inside the wrong source's label list.
    pub fn into_report(mut self) -> Report {
        if let Some(context) = &self.context
            && context.source.id != self.source.id
        {
            self.report.help.insert(
                0,
                format!(
                    "The error originates in {} at byte {}.",
                    self.source.name, self.report.diagnostic.span.start
                ),
            );
            for label in self.report.labels.drain(..) {
                self.report.help.push(format!(
                    "{} ({} at byte {})",
                    label.message, self.source.name, label.span.start
                ));
            }
            self.report.diagnostic.span = context.span;
            self.source = context.source.clone();
        }
        for label in self.related {
            if label.site.source.id == self.source.id
                || (self.source.text.is_empty() && label.site.source.text.is_empty())
            {
                self.report.labels.push(Label {
                    span: label.site.span,
                    message: label.message,
                });
            } else {
                self.report.help.push(format!(
                    "{} ({} at byte {})",
                    label.message, label.site.source.name, label.site.span.start
                ));
            }
        }
        *self.report
    }
}

#[derive(Clone, Debug, Default)]
pub struct SourceMap {
    sources: BTreeMap<SourceId, Arc<Source>>,
}

impl SourceMap {
    pub fn insert(&mut self, source: Arc<Source>) {
        self.sources.insert(source.id, source);
    }

    pub fn get(&self, id: SourceId) -> Option<&Arc<Source>> {
        self.sources.get(&id)
    }

    pub fn iter(&self) -> impl Iterator<Item = &Arc<Source>> {
        self.sources.values()
    }
}

#[derive(Clone, Debug)]
pub struct InstructionOrigin {
    pub site: SourceSpan,
    pub definition: SourceSpan,
    pub expansion: Vec<SourceSpan>,
    pub operands: Option<(SourceSpan, SourceSpan)>,
}

pub type Origins = BTreeMap<InstructionId, InstructionOrigin>;

impl InstructionOrigin {
    /// Select a semantic operand while retaining the expansion's definition site.
    pub fn for_operand(&self, destination: bool) -> Self {
        let mut origin = self.clone();
        if let Some((source, target)) = &self.operands {
            origin.site = if destination {
                target.clone()
            } else {
                source.clone()
            };
        }
        origin
    }

    /// Render an owned operation or operand site after its submission has retired.
    pub fn report(&self, mut report: Report, destination: Option<bool>) -> LocatedReport {
        let site = destination
            .and_then(|destination| {
                self.operands.as_ref().map(|operands| {
                    if destination {
                        &operands.1
                    } else {
                        &operands.0
                    }
                })
            })
            .unwrap_or(&self.site);
        report.diagnostic.span = site.span;
        let labels = std::mem::take(&mut report.labels);
        let mut located = LocatedReport::new(site.source.clone(), report);
        for label in labels {
            located = located.label(self.definition.source.at(label.span), label.message);
        }
        if self.definition != *site {
            located = located.label(
                self.definition.clone(),
                "expanded operation is defined here",
            );
        }
        located
    }
}
