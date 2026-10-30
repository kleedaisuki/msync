//! Explicit grammar selection without guessing from file existence or tokens.

use std::fmt;
use std::str::FromStr;

use crate::syntax::{Report, Span};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Version {
    #[default]
    Structured,
}

impl fmt::Display for Version {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::Structured => "0.5",
        })
    }
}

impl FromStr for Version {
    type Err = &'static str;

    fn from_str(text: &str) -> Result<Self, Self::Err> {
        let parts = text.split('.').collect::<Vec<_>>();
        if !(2..=3).contains(&parts.len())
            || parts
                .iter()
                .any(|part| part.is_empty() || !part.bytes().all(|ch| ch.is_ascii_digit()))
        {
            return Err(MIGRATION_HELP);
        }
        match (parts[0], parts[1]) {
            ("0", "5") => Ok(Self::Structured),
            _ => Err(MIGRATION_HELP),
        }
    }
}

const MIGRATION_HELP: &str = "Choose syntax 0.5; syntax 0.3 and 0.4 are no longer supported. Quoted sources are literal bytes: use '$pwd::pwd / \"filename\"' for a file path. Replace transaction controls with release and acquire barriers.";

/// Select grammar before any compilation or effects. The header stays in source,
/// so it never shifts diagnostic byte ranges. Unmarked input uses `fallback`.
/// Removed grammar headers are rejected rather than silently reinterpreted.
pub fn select_version(
    source: &str,
    requested: Option<Version>,
    fallback: Version,
) -> Result<Version, Vec<Report>> {
    let mut declared = None;
    let mut reports = Vec::new();
    let mut code_seen = false;
    let mut offset = 0;
    for physical in source.split_inclusive('\n') {
        let line = physical.trim_end_matches(['\r', '\n']);
        let trimmed = line.trim_start();
        let Some(comment) = trimmed.strip_prefix('#') else {
            code_seen |= !trimmed.is_empty();
            offset += physical.len();
            continue;
        };
        let Some(value) = comment.trim_start().strip_prefix("msync:") else {
            offset += physical.len();
            continue;
        };
        let start = offset + line.len() - value.trim_start().len();
        let span = Span {
            start,
            end: start + value.trim().len(),
        };
        let parsed = value.trim().parse::<Version>();
        if code_seen {
            reports.push(Report::new("the syntax header must precede executable statements", span)
                .help("Move '# msync: 0.5' to the initial comment block; a late header cannot change earlier statements."));
        } else if let Err(help) = parsed {
            reports.push(Report::new("unsupported syntax version", span).help(help));
        } else if let Some((_, previous)) = declared {
            reports.push(
                Report::new("a script must have only one syntax header", span)
                    .label(previous, "the first header is here")
                    .help("Keep one '# msync: 0.5' declaration before the code."),
            );
        } else {
            declared = Some((parsed.expect("validated version"), span));
        }
        offset += physical.len();
    }
    if !reports.is_empty() {
        return Err(reports);
    }
    Ok(declared
        .map(|(version, _)| version)
        .or(requested)
        .unwrap_or(fallback))
}
