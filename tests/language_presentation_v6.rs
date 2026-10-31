use msync::highlight::{TokenKind, highlight, token_spans};
use msync::language::{self, Version};
use proptest::prelude::*;

#[test]
fn v6_formats_nested_control_flow_and_lexical_declarations() {
    let source = r#"boolean any(a,b,c):
	return a or b or c
macro sync():
    symbol identity(x):
        return x
    ready = true and not false
    if ready:
        delete "old file"
    else if exists "new file":
        drop ready, identity
    else:
        loop:
            break
for item in files/<.+> | views::filter(keep):
    item -> output/
result = loop:
    break "done"
"#;
    let formatted = language::format(source).unwrap();
    assert!(formatted.contains("boolean any(a, b, c):\n"));
    assert!(formatted.contains("    else if "));
    assert!(formatted.contains("result = loop:\n    break \"done\"\n"));
    assert_eq!(language::format(&formatted).unwrap(), formatted);
}

#[test]
fn highlighting_distinguishes_comparisons_and_path_selectors() {
    let source = "ready = not false and \"可莉\" < \"琴\" or exists file\nfor file in root/<[a-z]+> | views::filter(keep):\n    delete file\n";
    let tokens = token_spans(source);
    let pieces = |kind: TokenKind| -> Vec<&str> {
        tokens
            .iter()
            .filter(|token| token.kind == kind)
            .map(|token| &source[token.span.start..token.span.end])
            .collect()
    };
    assert_eq!(pieces(TokenKind::Selector), vec!["<[a-z]+>"]);
    assert!(pieces(TokenKind::Operator).contains(&"<"));
    for keyword in ["not", "false", "and", "or", "exists", "for", "in", "delete"] {
        assert!(pieces(TokenKind::Command).contains(&keyword), "{keyword}");
    }
    assert_eq!(highlight(source, false), source);
    let mut cursor = 0;
    for token in tokens {
        assert_eq!(token.span.start, cursor);
        assert!(source.is_char_boundary(token.span.end));
        cursor = token.span.end;
    }
    assert_eq!(cursor, source.len());
}

#[test]
fn v6_is_default_with_v5_grammar_aliases_retained() {
    assert_eq!(Version::default().to_string(), "0.6");
    for version in ["0.5", "0.5.2", "0.6", "0.6.0"] {
        assert_eq!(version.parse::<Version>().unwrap(), Version::Structured);
    }
}

proptest! {
    #[test]
    fn boolean_expression_formatting_is_idempotent(a in any::<bool>(), b in any::<bool>(), c in any::<bool>()) {
        let source = format!("ok = ({a} or {b}) and not ({c} == {a})\n");
        let formatted = language::format(&source).unwrap();
        prop_assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
}
