use msync::highlight::{BUILTINS, KEYWORDS, RANGE_ADAPTORS, TokenKind, token_spans};

fn has(source: &str, word: &str, kind: TokenKind) -> bool {
    token_spans(source)
        .iter()
        .any(|token| token.kind == kind && &source[token.span.start..token.span.end] == word)
}

#[test]
fn explicit_scopes_and_native_functions_keep_declaration_roles() {
    assert!(KEYWORDS.contains(&"scope"));
    assert!(KEYWORDS.contains(&"native"));
    assert!(has("scope hello:", "scope", TokenKind::Command));
    assert!(has("scope hello:", "hello", TokenKind::Binding));
    let native = "native exists(path) -> Bool = \"fs.exists\"";
    assert!(has(native, "native", TokenKind::Command));
    assert!(has(native, "exists", TokenKind::Binding));
    assert!(has(native, "path", TokenKind::Binding));
    assert!(has("if exists(path):", "exists", TokenKind::Command));
}

#[test]
fn standard_library_scopes_and_ranges_have_nested_namespace_roles() {
    for module in ["fs", "path", "bool", "string", "ranges"] {
        assert!(BUILTINS.contains(&format!("$std::{module}::").as_str()));
    }
    assert!(RANGE_ADAPTORS.contains(&"$std::ranges::filter"));
    let source = "items | $std::ranges::filter(keep)";
    assert!(has(source, "$std::", TokenKind::Namespace));
    assert!(has(source, "ranges::", TokenKind::Namespace));
    assert!(has(source, "filter", TokenKind::Binding));
    let source = "if $std::fs::is_directory(path):";
    assert!(has(source, "$std::", TokenKind::Namespace));
    assert!(has(source, "fs::", TokenKind::Namespace));
    assert!(has(source, "is_directory", TokenKind::Binding));
}

#[test]
fn incomplete_namespace_and_native_edits_partition_utf8_source() {
    for source in [
        "scope 可莉:\n    codex := ~/.codex\n",
        "native exists(path) -> Bool = \"fs.exists\"",
        "[$wsl, ssh://forge] | $std::ranges::take(1)",
    ] {
        for end in source
            .char_indices()
            .map(|(offset, _)| offset)
            .chain([source.len()])
        {
            let text = &source[..end];
            let mut cursor = 0;
            for token in token_spans(text) {
                assert_eq!(token.span.start, cursor);
                assert!(token.span.end > cursor);
                assert!(text.is_char_boundary(token.span.end));
                cursor = token.span.end;
            }
            assert_eq!(cursor, end);
        }
    }
}
