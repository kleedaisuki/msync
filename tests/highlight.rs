use msync::highlight::{TokenKind, highlight, token_spans};

fn pieces(source: &str, kind: TokenKind) -> Vec<&str> {
    token_spans(source)
        .into_iter()
        .filter(|token| token.kind == kind)
        .map(|token| &source[token.span.start..token.span.end])
        .collect()
}

#[test]
fn colon_commands_match_slash_commands_without_coloring_dsl_paths() {
    for name in msync::highlight::COMMANDS {
        let colon = format!(":{}", &name[1..]);
        assert_eq!(pieces(&colon, TokenKind::Command), vec![colon.as_str()]);
        for operator in ["->", ":=", "=>"] {
            let source = format!("{colon}{operator}output");
            assert!(pieces(&source, TokenKind::Command).is_empty(), "{source}");
        }
    }
    let shell = ":bash echo 'a -> b := c'";
    assert_eq!(pieces(shell, TokenKind::Command), vec![":bash"]);
    assert!(pieces(shell, TokenKind::Operator).is_empty());
}

#[test]
fn contextual_lookup_items_are_styled_but_endpoint_paths_are_not_namespace_items() {
    for (source, expected) in [
        ("$pwd::pwd/文件 -> out", "pwd"),
        ("$file::parent/assets -> out", "file"),
        ("$file::this -> out", "file"),
        ("$env::LOCALAPPDATA/tools -> out", "env"),
        ("$env::环境\\tools -> out", "env"),
        ("remote::ordinary/path -> out", "remote"),
        ("$local::ordinary/path -> out", "local"),
        ("$pwd::", "pwd"),
    ] {
        assert_eq!(pieces(source, TokenKind::Namespace), vec![expected]);
        assert_eq!(highlight(source, false), source);
    }
}

#[test]
fn quoted_backslashes_and_doubled_quotes_are_literal_string_content() {
    let source = r#""a\n\r\t\\""\q -> # <[>]>" -> out"#;
    assert!(msync::syntax::parse(source).is_ok());
    let colored = highlight(source, true);
    assert!(colored.contains("\x1b[32m"));
    assert!(
        !colored.contains("\x1b[1;36m"),
        "backslashes are ordinary String bytes: {colored:?}"
    );
    assert_eq!(pieces(source, TokenKind::Operator), vec!["->"]);
    assert_eq!(pieces(source, TokenKind::Comment), Vec::<&str>::new());
    for source in [r"C:\temp\new -> out", r#""unfinished\"#, r#""unfinished\q"#] {
        assert_eq!(highlight(source, false), source);
        assert!(!highlight(source, true).contains("\x1b[1;36m"));
        assert!(!highlight(source, true).contains("\x1b[31m"));
    }
}

#[test]
fn multiline_strings_shield_language_syntax_until_the_shared_closing_quote() {
    let source = "text := \"line one\n# not comment -> $local::path\nline \"\"three\"\"\"/child\n";
    assert_eq!(
        pieces(source, TokenKind::String),
        vec!["\"line one\n# not comment -> $local::path\nline \"\"three\"\"\""]
    );
    assert_eq!(pieces(source, TokenKind::Operator), vec![":=", "/"]);
    assert!(pieces(source, TokenKind::Comment).is_empty());
    assert_eq!(highlight(source, false), source);
}
