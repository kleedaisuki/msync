use msync::highlight::{BUILTINS, RANGE_ADAPTORS, TokenKind, token_spans};

fn has(source: &str, word: &str, kind: TokenKind) -> bool {
    token_spans(source)
        .iter()
        .any(|token| token.kind == kind && &source[token.span.start..token.span.end] == word)
}

#[test]
fn canonical_view_apis_keep_namespace_and_callable_roles() {
    assert!(BUILTINS.contains(&"$view::"));
    for name in ["of", "filter", "transform", "take", "any_of", "front"] {
        let spelling = format!("$view::{name}");
        assert!(RANGE_ADAPTORS.contains(&spelling.as_str()));
        let source = format!("items | {spelling}");
        assert!(has(&source, "$view::", TokenKind::Namespace));
        assert!(has(&source, name, TokenKind::Binding));
    }
}

#[test]
fn ordered_list_delimiters_do_not_hide_boolean_or_function_items() {
    let source = "items := [true,false,ready(),$local::\"可莉 folder\"]";
    assert!(has(source, "true", TokenKind::Command));
    assert!(has(source, "false", TokenKind::Command));
    assert!(has(source, "ready", TokenKind::Binding));
    assert!(has(source, "$local::", TokenKind::Namespace));
    assert!(has(source, "\"可莉 folder\"", TokenKind::String));
    for source in ["items := [true,", "items := [\"open", "items := [root/<a>]"] {
        let mut cursor = 0;
        for token in token_spans(source) {
            assert_eq!(token.span.start, cursor);
            assert!(token.span.end > cursor);
            assert!(source.is_char_boundary(token.span.end));
            cursor = token.span.end;
        }
        assert_eq!(cursor, source.len());
    }
}

#[test]
fn ipv6_endpoint_brackets_remain_inside_the_endpoint() {
    for source in ["wire => tcp://[::1]:9000", "items := [tcp://[::1]:9000]"] {
        assert!(
            has(source, "tcp://[::1]:9000", TokenKind::Endpoint),
            "{source}"
        );
    }
}

#[test]
fn conditional_scheduling_and_nested_scopes_are_tolerant() {
    for schedule in ["release", "acquire", "acqrel"] {
        for prefix in ["if", "else if"] {
            let source = format!("{prefix} {schedule} true:");
            assert!(has(&source, schedule, TokenKind::Command), "{source}");
        }
    }
    let source = "config := $wsl::$local::codex(config.toml)";
    for scope in ["$wsl::", "$local::"] {
        assert!(has(source, scope, TokenKind::Namespace));
    }
    assert!(has(source, "codex", TokenKind::Binding));
    assert!(has("scope := $wsl::$local", "$local", TokenKind::Namespace));

    let source = format!("{}item", "$local::".repeat(4096));
    let tokens = token_spans(&source);
    assert_eq!(tokens.len(), 4097);
    assert_eq!(tokens.last().unwrap().span.end, source.len());
}
