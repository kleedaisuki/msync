use std::collections::{BTreeMap, BTreeSet};

use lsp_types::*;

use crate::highlight::{self, TokenKind};
use crate::syntax::Span;
use crate::tooling::{Definition, SymbolKind as Kind};

use super::documents::OpenDocument;

pub(super) const TOKEN_TYPES: &[&str] = &[
    "keyword",
    "operator",
    "string",
    "regexp",
    "comment",
    "namespace",
    "function",
    "variable",
    "parameter",
    "number",
];

pub(super) fn completion(document: &mut OpenDocument, position: Position) -> Vec<CompletionItem> {
    let Some(offset) = document.text.offset(position) else {
        return Vec::new();
    };
    if protected(&document.text.source, offset) {
        return Vec::new();
    }
    let start = word_start(&document.text.source, offset);
    let prefix = document.text.source[start..offset].to_owned();
    let range = document.text.range(Span { start, end: offset });
    let mut items = Vec::new();
    let mut seen = BTreeSet::new();
    for definition in document.syntax().index.visible_at(offset) {
        if definition.name.starts_with(&prefix)
            && seen.insert((definition.name.clone(), definition.kind as u8))
        {
            items.push(CompletionItem {
                label: definition.name.clone(),
                kind: Some(completion_kind(definition.kind)),
                detail: Some(definition.detail.clone()),
                text_edit: Some(CompletionTextEdit::Edit(TextEdit::new(
                    range,
                    definition.name.clone(),
                ))),
                ..CompletionItem::default()
            });
        }
    }
    for name in highlight::KEYWORDS
        .iter()
        .chain(highlight::BUILTINS)
        .chain(highlight::RANGE_ADAPTORS)
    {
        if name.starts_with(&prefix) && seen.insert(((*name).to_owned(), 255)) {
            items.push(CompletionItem {
                label: (*name).into(),
                kind: Some(if name.contains("::") {
                    CompletionItemKind::FUNCTION
                } else {
                    CompletionItemKind::KEYWORD
                }),
                detail: Some(builtin_detail(name)),
                text_edit: Some(CompletionTextEdit::Edit(TextEdit::new(
                    range,
                    (*name).into(),
                ))),
                ..CompletionItem::default()
            });
        }
    }
    items
}

pub(super) fn definition(
    document: &mut OpenDocument,
    uri: &Uri,
    position: Position,
) -> Option<Location> {
    let offset = document.text.offset(position)?;
    let span = document.syntax().index.definition_at(offset)?.selection;
    Some(Location::new(uri.clone(), document.text.range(span)))
}

pub(super) fn hover(document: &mut OpenDocument, position: Position) -> Option<Hover> {
    let offset = document.text.offset(position)?;
    if protected(&document.text.source, offset) {
        return None;
    }
    let declaration = document.syntax().index.definition_at(offset).cloned();
    let (detail, span) = if let Some(declaration) = declaration {
        let span = document
            .syntax()
            .index
            .references()
            .iter()
            .find(|reference| reference.span.start <= offset && offset < reference.span.end)
            .map_or(declaration.selection, |reference| reference.span);
        (declaration.detail, span)
    } else {
        let start = word_start(&document.text.source, offset);
        let end = document.text.source[offset..]
            .char_indices()
            .find(|(_, ch)| !word_character(*ch))
            .map_or(document.text.source.len(), |(index, _)| offset + index);
        let name = &document.text.source[start..end];
        if !highlight::KEYWORDS.contains(&name)
            && !highlight::BUILTINS.contains(&name)
            && !highlight::RANGE_ADAPTORS.contains(&name)
        {
            return None;
        }
        (builtin_detail(name), Span { start, end })
    };
    Some(Hover {
        contents: HoverContents::Markup(MarkupContent {
            kind: MarkupKind::Markdown,
            value: format!("```msync\n{detail}\n```"),
        }),
        range: Some(document.text.range(span)),
    })
}

#[allow(deprecated)]
pub(super) fn symbols(document: &mut OpenDocument) -> Vec<DocumentSymbol> {
    let definitions = document.syntax().index.definitions().to_vec();
    let mut children = BTreeMap::<Option<usize>, Vec<&Definition>>::new();
    for definition in &definitions {
        if definition.kind != Kind::Parameter {
            children
                .entry(definition.parent)
                .or_default()
                .push(definition);
        }
    }
    symbol_children(document, &children, None)
}

#[allow(deprecated)]
fn symbol_children(
    document: &OpenDocument,
    definitions: &BTreeMap<Option<usize>, Vec<&Definition>>,
    parent: Option<usize>,
) -> Vec<DocumentSymbol> {
    definitions
        .get(&parent)
        .into_iter()
        .flatten()
        .map(|definition| {
            let children = symbol_children(document, definitions, Some(definition.id));
            DocumentSymbol {
                name: definition.name.clone(),
                detail: Some(definition.detail.clone()),
                kind: symbol_kind(definition.kind),
                tags: None,
                deprecated: None,
                range: document.text.range(definition.span),
                selection_range: document.text.range(definition.selection),
                children: (!children.is_empty()).then_some(children),
            }
        })
        .collect()
}

pub(super) fn semantic_tokens(document: &mut OpenDocument) -> SemanticTokens {
    let syntax = document.syntax();
    let mut overlays: Vec<(Span, u32, u32)> = syntax
        .index
        .definitions()
        .iter()
        .map(|definition| (definition.selection, semantic_kind(definition.kind), 1))
        .chain(syntax.index.references().iter().map(|reference| {
            (
                reference.span,
                semantic_kind(syntax.index.definitions()[reference.definition].kind),
                0,
            )
        }))
        .collect();
    overlays.sort_by_key(|(span, _, _)| (span.start, span.end));
    overlays.dedup_by_key(|(span, _, _)| (span.start, span.end));
    let mut spans = overlays.clone();
    for token in highlight::token_spans(&document.text.source) {
        let kind = match token.kind {
            TokenKind::Command => 0,
            TokenKind::Operator => 1,
            TokenKind::String => 2,
            TokenKind::Selector => 3,
            TokenKind::Comment => 4,
            TokenKind::Namespace | TokenKind::Endpoint => 5,
            TokenKind::Binding => 7,
            TokenKind::Text
                if document.text.source[token.span.start..token.span.end]
                    .bytes()
                    .all(|byte| byte.is_ascii_digit()) =>
            {
                9
            }
            TokenKind::Text => continue,
        };
        let mut start = token.span.start;
        let begin = overlays.partition_point(|(span, _, _)| span.end <= start);
        for (overlay, _, _) in overlays[begin..]
            .iter()
            .take_while(|(span, _, _)| span.start < token.span.end)
        {
            if start < overlay.start {
                spans.push((
                    Span {
                        start,
                        end: overlay.start.min(token.span.end),
                    },
                    kind,
                    0,
                ));
            }
            start = start.max(overlay.end);
        }
        if start < token.span.end {
            spans.push((
                Span {
                    start,
                    end: token.span.end,
                },
                kind,
                0,
            ));
        }
    }
    spans.sort_by_key(|(span, _, _)| (span.start, span.end));
    let mut data = Vec::with_capacity(spans.len());
    let mut previous = Position::new(0, 0);
    for (span, kind, modifiers) in spans {
        let Some(source) = document.text.source.get(span.start..span.end) else {
            continue;
        };
        let mut start = span.start;
        for line in source.split_inclusive(['\r', '\n']) {
            let length = line.trim_end_matches(['\r', '\n']).len();
            if length != 0 {
                let position = document.text.position(start);
                let end = document.text.position(start + length);
                data.push(SemanticToken {
                    delta_line: position.line - previous.line,
                    delta_start: if position.line == previous.line {
                        position.character - previous.character
                    } else {
                        position.character
                    },
                    length: end.character - position.character,
                    token_type: kind,
                    token_modifiers_bitset: modifiers,
                });
                previous = position;
            }
            start += line.len();
        }
    }
    SemanticTokens {
        result_id: Some(document.version.to_string()),
        data,
    }
}

fn semantic_kind(kind: Kind) -> u32 {
    match kind {
        Kind::Macro | Kind::Symbol | Kind::Boolean => 6,
        Kind::Space => 5,
        Kind::Parameter | Kind::Iterator => 8,
        _ => 7,
    }
}

fn completion_kind(kind: Kind) -> CompletionItemKind {
    if kind.is_callable() {
        CompletionItemKind::FUNCTION
    } else if kind == Kind::Space {
        CompletionItemKind::MODULE
    } else {
        CompletionItemKind::VARIABLE
    }
}

fn symbol_kind(kind: Kind) -> SymbolKind {
    if kind.is_callable() {
        SymbolKind::FUNCTION
    } else if kind == Kind::Space {
        SymbolKind::NAMESPACE
    } else {
        SymbolKind::VARIABLE
    }
}

fn protected(source: &str, offset: usize) -> bool {
    highlight::token_spans(&source[..offset])
        .last()
        .is_some_and(|token| {
            matches!(
                token.kind,
                TokenKind::String | TokenKind::Comment | TokenKind::Selector
            )
        })
}

fn word_character(ch: char) -> bool {
    ch.is_alphanumeric() || matches!(ch, '_' | '-' | '.' | ':' | '$')
}

fn word_start(source: &str, offset: usize) -> usize {
    source[..offset]
        .char_indices()
        .rev()
        .find(|(_, ch)| !word_character(*ch))
        .map_or(0, |(index, ch)| index + ch.len_utf8())
}

fn builtin_detail(name: &str) -> String {
    let detail = match name {
        "boolean" => "boolean name(args): — bool-returning lexical function; return is mandatory",
        "symbol" => "symbol name(args): — pure lexical value function",
        "macro" => "macro name(args): — lexical executable template",
        "global" => "global name — import a prior binding from definition-time lexical ancestors",
        "drop" => "drop name, other — remove bindings without deleting resources",
        "delete" => {
            "delete path — unlink one file at execution; never recursively removes a directory"
        }
        "exists" => "exists path — execution-time path existence query returning bool",
        "and" | "or" => "short-circuit boolean operator; both operands require bool",
        "not" => "not bool — boolean negation; comparisons bind more tightly",
        "if" | "else" => "if bool: / else if bool: / else: — indented conditional branches",
        "for" | "in" => "for item in range: — demand-driven path/string range iteration",
        "loop" | "break" => {
            "loop: / break value — nearest-loop exit; value type must agree across breaks"
        }
        "true" | "false" => "bool literal",
        _ if name.starts_with("views::") => {
            "lazy path/string range adaptor; apply as range | adaptor(arguments)"
        }
        _ if name.starts_with("ranges::") => {
            "typed range source or scalar consumer; use pipeline or direct application"
        }
        _ if name.starts_with('$') => {
            "built-in namespace capability; select an explicit path item with ::"
        }
        _ => "msync language keyword",
    };
    detail.into()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn completion_uses_recovering_ast_and_respects_lexical_context() {
        let mut document = OpenDocument::new("ready = true\nresult = rea".into(), 1);
        let items = completion(&mut document, Position::new(1, 12));
        assert!(items.iter().any(|item| item.label == "ready"));
        let mut comment = OpenDocument::new("# del".into(), 1);
        assert!(completion(&mut comment, Position::new(0, 5)).is_empty());
        assert!(hover(&mut comment, Position::new(0, 3)).is_none());
        let mut range = OpenDocument::new("files := root/<.+> | views::fi".into(), 1);
        let end = range.text.end();
        assert!(
            completion(&mut range, end)
                .iter()
                .any(|item| item.label == "views::filter")
        );
    }

    #[test]
    fn recovering_first_operand_completion_keeps_template_parameters_isolated() {
        let source = "outside = true\nmacro copy(源文件):\n    源\nafter = true\n";
        assert!(crate::language::parse(source).is_err());
        let mut document = OpenDocument::new(source.into(), 1);
        let inside = completion(&mut document, Position::new(2, 5));
        assert!(inside.iter().any(|item| item.label == "源文件"));
        let inside_all = completion(&mut document, Position::new(2, 4));
        assert!(!inside_all.iter().any(|item| item.label == "outside"));
        let after = completion(&mut document, Position::new(3, 0));
        assert!(after.iter().any(|item| item.label == "outside"));
        assert!(!after.iter().any(|item| item.label == "源文件"));
        let (ast, _) = crate::language::parse_recovering(source);
        assert_eq!(ast.statements[1].span.end, source.find("\nafter").unwrap());
        assert!(source.is_char_boundary(ast.statements[1].span.end));
        let complete = "macro copy(source):\n    source -> target\nafter = true\n";
        let parsed = crate::language::parse(complete).unwrap();
        let recovered = crate::language::parse_recovering(complete).0;
        assert_eq!(parsed, recovered);
        assert_eq!(
            parsed.statements[0].span.end,
            complete.find("\nafter").unwrap()
        );
        let mut imported = OpenDocument::new(
            "root = true\nmacro imported():\n    global root\n    roo\n".into(),
            1,
        );
        assert!(
            completion(&mut imported, Position::new(3, 7))
                .iter()
                .any(|item| item.label == "root")
        );
    }

    #[test]
    fn hover_range_is_the_reference_not_its_definition() {
        let mut document = OpenDocument::new("ready = true\nif ready:\n    delete old\n".into(), 1);
        let hover = hover(&mut document, Position::new(1, 4)).unwrap();
        assert_eq!(
            hover.range.unwrap(),
            Range::new(Position::new(1, 3), Position::new(1, 8))
        );
        let uri = "file:///project/main.msync".parse().unwrap();
        let location = definition(&mut document, &uri, Position::new(1, 4)).unwrap();
        assert_eq!(location.range.start, Position::new(0, 0));
    }

    #[test]
    fn semantic_tokens_are_nonoverlapping_utf16_and_include_astral_strings() {
        let mut document = OpenDocument::new(
            "name = \"🎇\"\nboolean ready(x):\n    return x\nif ready(true):\n    delete name\n"
                .into(),
            2,
        );
        let tokens = semantic_tokens(&mut document);
        let mut line = 0;
        let mut column = 0;
        let mut previous_end = Position::new(0, 0);
        let mut found = false;
        for token in tokens.data {
            line += token.delta_line;
            column = if token.delta_line == 0 {
                column + token.delta_start
            } else {
                token.delta_start
            };
            let start = Position::new(line, column);
            assert!(start >= previous_end);
            let end = Position::new(line, column + token.length);
            assert!(document.text.offset(start).is_some() && document.text.offset(end).is_some());
            if token.token_type == 2 && line == 0 {
                assert_eq!(token.length, 4);
                found = true;
            }
            previous_end = end;
        }
        assert!(found);
        let symbols = symbols(&mut document);
        assert!(symbols.iter().any(|symbol| symbol.name == "ready"));
    }
}
