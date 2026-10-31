use msync::highlight::{KEYWORDS, RANGE_ADAPTORS, TokenKind, highlight, token_spans};

fn has(source: &str, word: &str, kind: TokenKind) -> bool {
    token_spans(source)
        .iter()
        .any(|token| token.kind == kind && &source[token.span.start..token.span.end] == word)
}

#[test]
fn declarations_loop_binders_and_bare_range_consumers_have_stable_roles() {
    for keyword in ["macro", "symbol", "boolean"] {
        let source = format!("{keyword} any(first, .second):");
        assert!(has(&source, keyword, TokenKind::Command));
        for name in ["any", "first", ".second"] {
            assert!(has(&source, name, TokenKind::Binding), "{source}: {name}");
        }
    }
    assert!(has("for file in files:", "file", TokenKind::Binding));
    assert!(has("loop: # note", "loop", TokenKind::Command));
    for spelling in RANGE_ADAPTORS {
        let source = format!("items | {spelling}");
        let (namespace, name) = spelling.split_once("::").unwrap();
        assert!(has(
            &source,
            &format!("{namespace}::"),
            TokenKind::Namespace
        ));
        assert!(has(&source, name, TokenKind::Binding), "{source}");
    }
    for keyword in ["delete", "drop", "if", "else", "for", "loop", "break"] {
        assert!(KEYWORDS.contains(&keyword));
    }
}

#[test]
fn compact_string_comparisons_do_not_turn_into_selector_spans() {
    let source = "ok = \"a\"<\"b\" and \"c\">=\"b\" or \"x\"!=\"y\"\n";
    assert!(
        !token_spans(source)
            .iter()
            .any(|token| token.kind == TokenKind::Selector)
    );
    for operator in ["=", "<", ">=", "!="] {
        assert!(has(source, operator, TokenKind::Operator));
    }
    for source in [
        "root/or/and/not/exists -> destination",
        "remote::true/file -> output",
    ] {
        assert!(
            !token_spans(source)
                .iter()
                .any(|token| token.kind == TokenKind::Command)
        );
    }
}

#[test]
fn selector_escapes_classes_and_incomplete_lines_preserve_source_bytes() {
    for source in [
        "for path in root/<[>#]+\\.txt> | views::take(2): # comment\n",
        "boolean any(a, b):\n    return a or b\n",
        "result = exists (文件) and not false\n",
        "path := root/<[a-z",
        "text = \"unfinished\\",
        "loop: # comment\n    break \"可莉\"\n",
    ] {
        let mut end = 0;
        for token in token_spans(source) {
            assert_eq!(token.span.start, end);
            assert!(token.span.end > end);
            assert!(source.is_char_boundary(token.span.end));
            end = token.span.end;
        }
        assert_eq!(end, source.len());
        assert_eq!(highlight(source, false), source);
    }
}

#[test]
fn cr_only_physical_lines_do_not_extend_comments_or_unfinished_strings() {
    let source = "# comment\rdelete file\rtext = \"unfinished\rfor file in items:\r";
    assert!(has(source, "delete", TokenKind::Command));
    assert!(has(source, "for", TokenKind::Command));
    let pieces: String = token_spans(source)
        .iter()
        .map(|token| &source[token.span.start..token.span.end])
        .collect();
    assert_eq!(pieces, source);
}
