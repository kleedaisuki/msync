#[allow(
    dead_code,
    reason = "one-shot diagnostics use a subset of the shared subprocess helper"
)]
#[path = "support/journey_process.rs"]
mod journey_process;

use std::fs;
use std::path::Path;

use msync::source::{LocatedReport, Source};
use msync::syntax::{Diagnostic, Report, Span};

#[test]
fn cli_diagnostics_report_exact_unicode_columns_for_lf_crlf_and_cr() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::Builder::new()
        .prefix("source-lines-v6-")
        .tempdir_in(temporary)
        .unwrap();
    for ending in ["\n", "\r\n", "\r"] {
        let source = format!("# 可莉🎇{ending}flag = \"🎇\" == true{ending}");
        fs::write(root.path().join("entry.msync"), source).unwrap();
        let output = journey_process::Process::start(journey_process::Process::command(
            root.path(),
            &["--color", "never", "check", "entry.msync"],
        ))
        .finish();
        assert!(!output.status.success());
        let diagnostic = String::from_utf8(output.stderr).unwrap();
        assert!(
            diagnostic.contains("entry.msync:2:12"),
            "{ending:?}: {diagnostic}"
        );
        assert!(
            diagnostic.contains("2 | flag = \"🎇\" == true"),
            "{diagnostic}"
        );
        assert!(!diagnostic.contains("\\rflag"), "{diagnostic}");
    }
}

#[test]
fn primary_and_related_renderers_share_physical_line_boundaries() {
    for ending in ["\n", "\r\n", "\r"] {
        let parent = Source::new(
            "parent.msync",
            format!("# 可莉🎇{ending}include \"child.msync\"{ending}"),
        );
        let child = Source::new(
            "child.msync",
            format!("# 可莉🎇{ending}flag = \"🎇\" == true{ending}"),
        );
        let start = child.text.find("==").unwrap();
        let include = parent.text.find("\"child.msync\"").unwrap();
        let report = LocatedReport::new(
            child.clone(),
            Report::new(
                "invalid comparison",
                Span {
                    start,
                    end: start + 2,
                },
            ),
        )
        .label(
            parent.at(Span {
                start: include,
                end: include + 13,
            }),
            "included here",
        );
        let rendered = report.render(false);
        assert!(rendered.contains("child.msync:2:12"), "{rendered}");
        assert!(rendered.contains("parent.msync:2:9"), "{rendered}");
        let simple = msync::syntax::render_diagnostic(
            &child.text,
            &Diagnostic {
                message: "invalid comparison".into(),
                span: Span {
                    start,
                    end: start + 2,
                },
            },
        );
        assert!(simple.contains(" --> 2:12"), "{simple}");
    }
}
