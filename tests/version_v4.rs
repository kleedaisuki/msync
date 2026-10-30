use msync::language::{self, Version, select_version};

#[test]
fn caller_selected_fallback_is_explicit_and_never_guessed_from_file_existence() {
    assert_eq!(Version::default(), Version::Structured);
    for text in [
        "\"source.txt\" -> result\n",
        "\"success\" -> result\n",
        "macro copy(x):\n    x -> out\n",
    ] {
        assert_eq!(
            select_version(text, None, Version::Structured).unwrap(),
            Version::Structured
        );
        assert_eq!(
            select_version(text, Some(Version::Structured), Version::Structured).unwrap(),
            Version::Structured
        );
    }
}

#[test]
fn explicit_header_or_mode_selects_structured_grammar_without_removing_source_bytes() {
    let text = "# 文档\r\n# msync: 0.5\r\n\"可莉\" -> output\r\n";
    assert_eq!(
        select_version(text, None, Version::Structured).unwrap(),
        Version::Structured
    );
    let parsed = language::parse(text).unwrap();
    let transfer = parsed.statements.last().unwrap();
    assert_eq!(
        &text[transfer.span.start..transfer.span.end],
        "\"可莉\" -> output"
    );
    assert_eq!(
        select_version("x -> y", None, Version::Structured).unwrap(),
        Version::Structured
    );
}

#[test]
fn removed_duplicate_and_late_headers_are_rejected_before_execution() {
    for removed in ["0.3", "0.3.1", "0.4", "0.4.1"] {
        let text = format!("# msync: {removed}\n\"file\" -> out\n");
        let reports =
            select_version(&text, Some(Version::Structured), Version::Structured).unwrap_err();
        assert_eq!(
            &text[reports[0].diagnostic.span.start..reports[0].diagnostic.span.end],
            removed
        );
        let help = reports[0].help.join(" ");
        assert!(help.contains("literal bytes"));
        assert!(help.contains("$pwd"));
        assert!(help.contains("release") && help.contains("acquire"));
    }
    assert!(select_version("# msync: 0.5\n# msync: 0.5\n", None, Version::Structured).is_err());
    assert!(
        select_version(
            "source -> target\n# msync: 0.5\n",
            None,
            Version::Structured
        )
        .is_err()
    );
}

#[test]
fn unknown_or_empty_versions_have_bounded_actionable_spans() {
    for text in [
        "# msync: 0.3\n",
        "# msync: 0.4\n",
        "# msync:\n",
        "# msync:   \n",
        "# msync: monkey\n",
    ] {
        let reports = select_version(text, None, Version::Structured).unwrap_err();
        assert!(reports.iter().all(|report| !report.help.is_empty()
            && report.diagnostic.span.start <= report.diagnostic.span.end
            && report.diagnostic.span.end <= text.len()));
    }
}

#[test]
fn header_round_trips_through_structured_formatter_and_patch_versions_share_grammar() {
    let text = "# msync: 0.5.0\nmacro copy(x):\n\tx -> out\ncopy(\"value\")\n";
    let formatted = language::format(text).unwrap();
    assert_eq!(
        select_version(&formatted, None, Version::Structured).unwrap(),
        Version::Structured
    );
    for removed in ["0.3", "0.3.1", "0.4", "0.4.1"] {
        assert!(removed.parse::<Version>().is_err());
    }
    assert_eq!("0.5.12".parse::<Version>().unwrap(), Version::Structured);
    assert!("0.5garbage".parse::<Version>().is_err());
    assert!("0.5.".parse::<Version>().is_err());
}
