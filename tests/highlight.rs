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
        ("$pwd::pwd/文件 -> out", "$pwd::pwd"),
        ("$file::parent/assets -> out", "$file::parent"),
        ("$file::this -> out", "$file::this"),
        ("$env::LOCALAPPDATA/tools -> out", "$env::LOCALAPPDATA"),
        ("$env::环境\\tools -> out", "$env::环境"),
        ("remote::ordinary/path -> out", "remote::"),
        ("$local::ordinary/path -> out", "$local::"),
        ("$pwd::", "$pwd::"),
    ] {
        assert_eq!(pieces(source, TokenKind::Namespace), vec![expected]);
        assert_eq!(highlight(source, false), source);
    }
}

#[test]
fn quoted_escapes_are_distinct_without_reinterpreting_literals_or_unfinished_input() {
    let source = r#""a\n\r\t\\\"\q -> # <[>]>" -> out"#;
    assert!(msync::syntax::parse(source).is_ok());
    let colored = highlight(source, true);
    for escape in [r"\n", r"\r", r"\t", r"\\", r#"\""#] {
        assert!(
            colored.contains(&format!("\x1b[1;36m{escape}\x1b[0m")),
            "{colored:?}"
        );
    }
    assert!(!colored.contains("\x1b[1;36m\\q"));
    assert_eq!(pieces(source, TokenKind::Operator), vec!["->"]);
    assert_eq!(pieces(source, TokenKind::Comment), Vec::<&str>::new());
    for source in [r"C:\temp\new -> out", r#""unfinished\"#, r#""unfinished\q"#] {
        assert_eq!(highlight(source, false), source);
        assert!(!highlight(source, true).contains("\x1b[1;36m"));
        assert!(!highlight(source, true).contains("\x1b[31m"));
    }
}
