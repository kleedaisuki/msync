use msync::highlight::{TokenKind, highlight, token_spans};

#[test]
fn unquoted_endpoint_protocol_operator_and_address_have_independent_roles() {
    for (protocol, address) in [
        ("ssh", "forge"),
        ("http", "example.test/assets/index.html"),
        ("https", "example.test:8443/path?q=one#fragment"),
        ("tcp", "[::1]:9000"),
        ("ssh", "user@forge:2222"),
        ("ssh", "user@[2001:db8::1]:2222/path"),
        ("custom+wire.1", "host/segment"),
    ] {
        let source = format!("{protocol}://{address}");
        let pieces: Vec<_> = token_spans(&source)
            .into_iter()
            .map(|token| (token.kind, &source[token.span.start..token.span.end]))
            .collect();
        assert_eq!(
            pieces,
            [
                (TokenKind::Protocol, protocol),
                (TokenKind::Operator, "://"),
                (TokenKind::Endpoint, address)
            ],
            "{source}"
        );
        assert_eq!(highlight(&source, false), source);
        let colored = highlight(&source, true);
        assert!(colored.contains(&format!("\x1b[1;36m{protocol}\x1b[0m")));
        assert!(colored.contains("\x1b[1;33m://\x1b[0m"));
        assert!(colored.contains(&format!("\x1b[34m{address}\x1b[0m")));
    }
}

#[test]
fn endpoint_display_roles_never_split_string_selector_or_shell_literals() {
    for (source, expected) in [
        ("\"ssh://forge/path\"", TokenKind::String),
        ("<https?://[^>]+>", TokenKind::Selector),
        ("# ssh://forge", TokenKind::Comment),
    ] {
        let tokens = token_spans(source);
        assert_eq!(tokens.len(), 1, "{source}");
        assert_eq!(tokens[0].kind, expected);
    }
    let source = "/bash printf '%s' ssh://forge";
    assert!(
        !token_spans(source)
            .iter()
            .any(|token| token.kind == TokenKind::Protocol)
    );
}

#[test]
fn incomplete_endpoint_edits_keep_exact_nonoverlapping_spans() {
    for source in [
        "server => ssh://forge",
        "items := [tcp://[::1]:9000, https://site/path]",
        "copy(ssh://user@[2001:db8::1]:2222, $local::path)",
    ] {
        for end in source
            .char_indices()
            .map(|(offset, _)| offset)
            .chain([source.len()])
        {
            let text = &source[..end];
            let mut cursor = 0;
            for token in token_spans(text) {
                assert_eq!(token.span.start, cursor, "{text}");
                assert!(token.span.end > cursor);
                assert!(text.is_char_boundary(token.span.end));
                cursor = token.span.end;
            }
            assert_eq!(cursor, end);
        }
    }
    assert_eq!(token_spans("ssh://").len(), 2);
}
