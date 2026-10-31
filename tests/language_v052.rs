use msync::highlight::{self, TokenKind};
use msync::language::{self, Expr, ExprKind, StatementKind, Submission, SubmissionBuffer};
use proptest::prelude::*;

#[test]
fn symbol_return_and_file_directives_preserve_owned_utf8_spans() {
    let source = "symbol 地址(space, item):\r\n    global root\r\n    child := outer(inner(space), item)\r\n    return child\r\nexecute scripts/<.+\\.msync>\r\ninclude \"shared file.msync\"\r\n";
    let document = language::parse(source).unwrap();
    let StatementKind::Symbol {
        name,
        parameters,
        body,
    } = &document.statements[0].kind
    else {
        panic!()
    };
    assert_eq!(name.text, "地址");
    assert_eq!(parameters.len(), 2);
    assert!(matches!(
        &body[2].kind,
        StatementKind::Return(Expr {
            kind: ExprKind::Path(_),
            ..
        })
    ));
    let StatementKind::Execute(path) = &document.statements[1].kind else {
        panic!()
    };
    assert_eq!(
        &source[path.span.start..path.span.end],
        "scripts/<.+\\.msync>"
    );
    let StatementKind::Include(path) = &document.statements[2].kind else {
        panic!()
    };
    assert!(matches!(&path.kind, ExprKind::Literal(text) if text == "shared file.msync"));
    assert_eq!(&source[name.span.start..name.span.end], "地址");
    let formatted = language::format(source).unwrap();
    assert_eq!(language::format(&formatted).unwrap(), formatted);
}

#[test]
fn release_acquire_and_acqrel_map_to_the_existing_scheduling_contract() {
    for (source, expected) in [
        ("release a -> b", (true, false)),
        ("acquire a -> b", (false, true)),
        ("acqrel a -> b", (true, true)),
        ("release acquire a -> b", (true, true)),
        ("async await a -> b", (true, true)),
    ] {
        let document = language::parse(source).unwrap();
        let StatementKind::Scheduled {
            asynchronous,
            awaited,
            ..
        } = &document.statements[0].kind
        else {
            panic!()
        };
        assert_eq!((*asynchronous, *awaited), expected);
    }
    let document = language::parse("release\nacquire\nacquire Program\n").unwrap();
    assert!(matches!(document.statements[0].kind, StatementKind::Async));
    assert!(matches!(
        document.statements[1].kind,
        StatementKind::Await(None)
    ));
    assert!(
        matches!(&document.statements[2].kind, StatementKind::Await(Some(name)) if name.text == "Program")
    );
    assert_eq!(
        language::format("async\nawait\nawait async a->b\n").unwrap(),
        "release\nacquire\nacqrel a -> b\n"
    );
}

#[test]
fn duplicate_effects_and_incomplete_directives_have_precise_diagnostics() {
    for source in [
        "release release a -> b",
        "acqrel acquire a -> b",
        "async release a -> b",
    ] {
        let errors = language::parse(source).unwrap_err();
        assert!(errors[0].diagnostic.message.contains("duplicate"));
    }
    for source in ["acqrel", "return", "include", "execute"] {
        assert!(language::parse(source).is_err(), "{source}");
    }
    for source in [
        "release symbol make():",
        "acquire return item",
        "acqrel include file.msync",
    ] {
        assert!(language::parse(source).is_err(), "{source}");
    }
}

#[test]
fn symbol_blocks_accumulate_in_repl_without_exposing_partial_definitions() {
    let mut buffer = SubmissionBuffer::default();
    for line in ["symbol root(space):", "    return space::~"] {
        assert_eq!(buffer.push_line(line).unwrap(), Submission::Pending);
    }
    let Submission::Ready(source) = buffer.push_line("").unwrap() else {
        panic!()
    };
    assert!(matches!(
        language::parse(&source).unwrap().statements[0].kind,
        StatementKind::Symbol { .. }
    ));
    for line in [
        "symbol-file -> output",
        "\"symbol text\" -> output",
        "# symbol foo():",
    ] {
        assert!(matches!(
            buffer.push_line(line).unwrap(),
            Submission::Ready(_)
        ));
    }
}

fn source_expression(source: &str) -> Expr {
    let document = language::parse(&format!("{source} -> output\n")).unwrap();
    let StatementKind::Transfer { source, .. } = &document.statements[0].kind else {
        panic!()
    };
    source.clone()
}

#[test]
fn symbol_value_calls_compose_on_either_side_of_path_operators() {
    let value = source_expression("pick($wsl)/child");
    assert!(
        matches!(value.kind, ExprKind::Join { base, .. } if matches!(base.kind, ExprKind::Call { .. }))
    );
    let value = source_expression("base/pick($wsl)");
    assert!(
        matches!(value.kind, ExprKind::Join { suffix: Some(suffix), .. } if matches!(suffix.kind, ExprKind::Call { .. }))
    );
    let value = source_expression("pick($pwd)::pwd/child");
    assert!(
        matches!(value.kind, ExprKind::Join { base, .. } if matches!(base.kind, ExprKind::Select { .. }))
    );
    let document = language::parse("Program => $pwd::pwd / \"tool name\"(\"argument\")\n").unwrap();
    let StatementKind::Binding { value, .. } = &document.statements[0].kind else {
        panic!()
    };
    assert!(
        matches!(&value.kind, ExprKind::Call { callee, .. } if matches!(callee.kind, ExprKind::Join { .. }))
    );
    let document = language::parse("Program => executable($wsl)(\"argument\")\n").unwrap();
    let StatementKind::Binding { value, .. } = &document.statements[0].kind else {
        panic!()
    };
    assert!(
        matches!(&value.kind, ExprKind::Call { callee, .. } if matches!(callee.kind, ExprKind::Call { .. }))
    );
}

#[test]
fn new_directive_words_are_highlighted_without_reclassifying_paths_or_strings() {
    for source in [
        "symbol f(space):",
        "return space::~",
        "execute script.msync",
        "include common.msync",
        "release acquire a -> b",
        "acqrel a -> b",
    ] {
        let tokens = highlight::token_spans(source);
        assert!(
            tokens.iter().any(|token| token.kind == TokenKind::Command),
            "{source}"
        );
    }
    for source in [
        "symbol-file -> output",
        "$pwd::pwd/release -> output",
        "\"acquire\" -> output",
        "# include ignored",
    ] {
        assert!(
            !highlight::token_spans(source)
                .iter()
                .any(|token| token.kind == TokenKind::Command),
            "{source}"
        );
    }
}

fn semantic_document(source: &str) -> language::Document {
    fn names(names: &mut [language::Name]) {
        for name in names {
            name.span = Default::default();
        }
    }
    fn expression(value: &mut Expr) {
        value.span = Default::default();
        value.source = msync::syntax::OperandSource::unmapped(Default::default());
        match &mut value.kind {
            ExprKind::Path(path) => {
                if let Some(name) = &mut path.qualifier {
                    name.span = Default::default();
                }
            }
            ExprKind::Literal(_) | ExprKind::Bool(_) => {}
            ExprKind::Not(value) | ExprKind::Exists(value) => expression(value),
            ExprKind::Binary {
                left,
                right,
                operator,
                ..
            } => {
                expression(left);
                expression(right);
                *operator = Default::default();
            }
            ExprKind::Loop { body } => statements(body),
            ExprKind::Call { callee, arguments } => {
                expression(callee);
                for argument in arguments {
                    expression(argument);
                }
            }
            ExprKind::Join {
                base,
                suffix,
                operator,
                ..
            } => {
                expression(base);
                if let Some(suffix) = suffix {
                    expression(suffix);
                }
                *operator = Default::default();
            }
            ExprKind::Select {
                namespace,
                item,
                operator,
            } => {
                expression(namespace);
                expression(item);
                *operator = Default::default();
            }
        }
    }
    fn statements(body: &mut [language::Statement]) {
        for statement in body {
            statement.span = Default::default();
            match &mut statement.kind {
                StatementKind::Binding { name, value, .. } => {
                    name.span = Default::default();
                    expression(value);
                }
                StatementKind::Transfer {
                    source,
                    destination,
                } => {
                    expression(source);
                    expression(destination);
                }
                StatementKind::Macro {
                    name,
                    parameters,
                    body,
                }
                | StatementKind::Symbol {
                    name,
                    parameters,
                    body,
                }
                | StatementKind::Boolean {
                    name,
                    parameters,
                    body,
                } => {
                    name.span = Default::default();
                    names(parameters);
                    statements(body);
                }
                StatementKind::Return(value)
                | StatementKind::Execute(value)
                | StatementKind::Include(value)
                | StatementKind::Delete(value)
                | StatementKind::Call(value) => expression(value),
                StatementKind::Global(values) | StatementKind::Drop(values) => names(values),
                StatementKind::If {
                    condition,
                    then_body,
                    else_body,
                } => {
                    expression(condition);
                    statements(then_body);
                    statements(else_body);
                }
                StatementKind::For { name, range, body } => {
                    name.span = Default::default();
                    expression(range);
                    statements(body);
                }
                StatementKind::Loop { body } => statements(body),
                StatementKind::Break(Some(value)) => expression(value),
                StatementKind::Break(None) => {}
                StatementKind::Scheduled { statement, .. } => {
                    statements(std::slice::from_mut(statement.as_mut()))
                }
                StatementKind::Await(Some(name)) => name.span = Default::default(),
                StatementKind::Await(None) | StatementKind::Async | StatementKind::Comment(_) => {}
            }
        }
    }
    let mut document = language::parse(source).unwrap();
    statements(&mut document.statements);
    document
}

#[test]
fn postfix_call_and_dynamic_namespace_chains_are_bounded() {
    for source in [
        format!("f{} -> output\n", "()".repeat(1024)),
        format!("f(){} -> output\n", "::item".repeat(1024)),
    ] {
        let errors = language::parse(&source).unwrap_err();
        assert!(
            errors
                .iter()
                .any(|error| error.diagnostic.message.contains("too deep"))
        );
    }
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(64))]
    #[test]
    fn composed_symbol_values_format_idempotently(component in "[a-z一-龥]{1,24}", level in 1usize..8) {
        let mut value = "$wsl".to_owned();
        for _ in 0..level { value = format!("identity({value})"); }
        let source = format!("symbol identity(space):\n    return space\n{value}::root / \"{component}\" -> output\n");
        let formatted = language::format(&source).unwrap();
        prop_assert_eq!(semantic_document(&source), semantic_document(&formatted));
        prop_assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
}

#[test]
fn symbol_header_errors_name_the_symbol_definition_not_a_macro() {
    let reports = language::parse("symbol missing:\n    return value\n").unwrap_err();
    assert!(reports[0].diagnostic.message.contains("symbol parameters"));
    let reports =
        language::parse("symbol duplicate(value, value):\n    return value\n").unwrap_err();
    assert!(
        reports[0]
            .diagnostic
            .message
            .contains("duplicate symbol parameter")
    );
    let source = "identity($pwd)::pwd";
    let value = source_expression(source);
    let ExprKind::Select { operator, .. } = value.kind else {
        panic!()
    };
    assert_eq!(&source[operator.start..operator.end], "::");
}
