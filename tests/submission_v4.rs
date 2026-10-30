use std::path::PathBuf;

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::language::{self, Submission, SubmissionBuffer};
use msync::pipeline;

#[test]
fn a_compound_prefix_never_reaches_compilation_before_the_blank_line() {
    let mut input = SubmissionBuffer::default();
    let mut session = Session::new();
    let context = CompileContext {
        pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
        source_file: None,
    };
    for line in ["macro write(value):", "    value -> 输出.txt"] {
        assert_eq!(input.push_line(line).unwrap(), Submission::Pending);
        assert!(session.bindings().is_empty());
    }
    let Submission::Ready(source) = input.push_line("").unwrap() else {
        panic!()
    };
    pipeline::compile(&mut session, &source, &context, &[]).unwrap();
    assert_eq!(session.bindings(), ["macro write(value)"]);
    let Submission::Ready(call) = input.push_line("write(\"可莉\")").unwrap() else {
        panic!()
    };
    let result = pipeline::compile(&mut session, &call, &context, &[]).unwrap();
    assert_eq!(result.program.body.instructions.len(), 1);
}

#[test]
fn cancellation_discards_the_entire_block_and_next_input_is_independent() {
    let mut input = SubmissionBuffer::default();
    input.push_line("macro discarded():").unwrap();
    input.push_line("    \"never copied\" -> target").unwrap();
    assert!(input.cancel());
    assert!(!input.cancel());
    assert!(!input.is_pending());
    assert_eq!(
        input.push_line("/help").unwrap(),
        Submission::Ready("/help\n".into())
    );
}

#[test]
fn eof_keeps_source_offsets_and_allows_the_parser_to_diagnose_an_incomplete_block() {
    let mut input = SubmissionBuffer::default();
    input.push_line("macro 不完整(): # unfinished\r\n").unwrap();
    let source = input.finish().unwrap();
    assert_eq!(source, "macro 不完整(): # unfinished\n");
    let reports = language::parse(&source).unwrap_err();
    assert!(!reports.is_empty());
    assert!(
        reports
            .iter()
            .all(|report| report.diagnostic.span.end <= source.len())
    );
    assert!(input.finish().is_none());
}

#[test]
fn comments_and_quoted_header_words_are_not_compound_input() {
    let mut input = SubmissionBuffer::default();
    for line in [
        "# macro foo():",
        "\"transaction A:\" -> file",
        "config := macro",
    ] {
        assert!(matches!(
            input.push_line(line).unwrap(),
            Submission::Ready(_)
        ));
        assert!(!input.is_pending());
    }
    assert_eq!(input.push_line("  \t").unwrap(), Submission::Empty);
    input.push_line("macro delayed(): # root context").unwrap();
    input.push_line("    # no premature release").unwrap();
    input.push_line("    \"字节\" -> file").unwrap();
    let Submission::Ready(source) = input.push_line("  ").unwrap() else {
        panic!()
    };
    assert!(language::parse(&source).is_ok());
}

#[test]
fn malformed_multiline_editor_events_do_not_lose_or_submit_a_pending_prefix() {
    let mut input = SubmissionBuffer::default();
    input.push_line("macro discarded():").unwrap();
    assert!(input.push_line("    async\n/quit").is_err());
    assert_eq!(input.finish().unwrap(), "macro discarded():\n");
}
