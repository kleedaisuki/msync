use std::collections::{BTreeMap, BTreeSet};

use lsp_types::*;

use crate::highlight::{self, TokenKind};
use crate::syntax::Span;
use crate::tooling::{
    Definition, LibraryModule, LibraryReference, SymbolKind as Kind, standard_library,
};

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
    if let Some(items) = namespace_completion(document, offset) {
        return items;
    }
    let start = completion_start(&document.text.source, offset);
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
                kind: Some(if name.ends_with("::") {
                    CompletionItemKind::MODULE
                } else if name.contains("::") {
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
    if let Some((module, declaration, _)) = library_declaration(document, offset) {
        let text = super::documents::Text::new(module.source.into());
        return Some(Location::new(
            library_uri(module.name),
            text.range(declaration.selection),
        ));
    }
    let span = document.syntax().index.definition_at(offset)?.selection;
    Some(Location::new(uri.clone(), document.text.range(span)))
}

pub(super) fn hover(document: &mut OpenDocument, position: Position) -> Option<Hover> {
    let offset = document.text.offset(position)?;
    if protected(&document.text.source, offset) {
        return None;
    }
    let declaration = document.syntax().index.definition_at(offset).cloned();
    let library = library_declaration(document, offset);
    let (detail, span) = if let Some((_, declaration, span)) = library {
        (declaration.detail.clone(), span)
    } else if let Some(declaration) = declaration {
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
        .chain(
            syntax
                .index
                .library_references()
                .iter()
                .filter_map(|reference| {
                    let (_, definition) = library_symbol(reference)?;
                    Some((reference.span, semantic_kind(definition.kind), 0))
                }),
        )
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
        Kind::Macro | Kind::Symbol | Kind::Boolean | Kind::Native => 6,
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

fn completion_start(source: &str, offset: usize) -> usize {
    let start = word_start(source, offset);
    let word = &source[start..offset];
    if word.starts_with("$view::")
        || word.starts_with("views::")
        || word.starts_with("ranges::")
        || highlight::BUILTINS
            .iter()
            .any(|name| name.starts_with(word))
    {
        return start;
    }
    word.rfind("::")
        .map_or(start, |separator| start + separator + 2)
}

fn namespace_completion(document: &mut OpenDocument, offset: usize) -> Option<Vec<CompletionItem>> {
    let start = word_start(&document.text.source, offset);
    let word = document.text.source[start..offset].to_owned();
    let (namespace, prefix) = word.rsplit_once("::")?;
    // Retain established fully-qualified completion labels for the legacy API.
    if matches!(namespace, "$view" | "views" | "ranges") {
        return None;
    }
    let qualified = namespace.starts_with("$std");
    let replacement = if qualified {
        start
    } else {
        start + namespace.len() + 2
    };
    let range = document.text.range(Span {
        start: replacement,
        end: offset,
    });
    let index = &document.syntax().index;
    let module = index.library_namespace_at(namespace, offset);
    let primitive_view = index.is_view_namespace_at(namespace, offset);
    let members = index.members_at(namespace, offset);
    if module.is_none() && members.is_none() {
        return None;
    }
    let item = |name: &str, detail: String, kind| {
        let label = if qualified {
            format!("{namespace}::{name}")
        } else {
            name.into()
        };
        CompletionItem {
            label: label.clone(),
            kind: Some(kind),
            detail: Some(detail),
            text_edit: Some(CompletionTextEdit::Edit(TextEdit::new(range, label))),
            ..CompletionItem::default()
        }
    };
    if let Some(module) = module {
        if module.is_empty() {
            return Some(
                standard_library()
                    .iter()
                    .filter(|module| module.name.starts_with(prefix))
                    .map(|module| {
                        item(
                            module.name,
                            format!("scope $std::{}", module.name),
                            CompletionItemKind::MODULE,
                        )
                    })
                    .collect(),
            );
        }
        if module == "ranges" && primitive_view {
            return Some(
                highlight::RANGE_ADAPTORS
                    .iter()
                    .filter_map(|name| name.strip_prefix("$std::ranges::"))
                    .filter(|name| name.starts_with(prefix))
                    .map(|name| {
                        item(
                            name,
                            format!("$std::ranges::{name} — typed lazy range API"),
                            CompletionItemKind::FUNCTION,
                        )
                    })
                    .collect(),
            );
        }
        let module = standard_library()
            .iter()
            .find(|entry| entry.name == module)?;
        return Some(
            module
                .definitions()
                .filter(|definition| definition.name.starts_with(prefix))
                .map(|definition| {
                    item(
                        &definition.name,
                        definition.detail.clone(),
                        completion_kind(definition.kind),
                    )
                })
                .collect(),
        );
    }
    Some(
        members?
            .into_iter()
            .filter(|definition| definition.name.starts_with(prefix))
            .map(|definition| {
                item(
                    &definition.name,
                    definition.detail.clone(),
                    completion_kind(definition.kind),
                )
            })
            .collect(),
    )
}

fn library_uri(module: &str) -> Uri {
    format!(
        "msync-stdlib:///{}/{module}.msync",
        env!("CARGO_PKG_VERSION")
    )
    .parse()
    .expect("static library URI")
}

pub(super) fn library_source(uri: &Uri) -> Option<&'static str> {
    let uri = url::Url::parse(uri.as_str()).ok()?;
    if uri.scheme() != "msync-stdlib" {
        return None;
    }
    let module = uri
        .path()
        .strip_prefix(concat!("/", env!("CARGO_PKG_VERSION"), "/"))?
        .strip_suffix(".msync")?;
    standard_library()
        .iter()
        .find(|entry| entry.name == module)
        .map(|module| module.source)
}

fn library_declaration(
    document: &mut OpenDocument,
    offset: usize,
) -> Option<(&'static LibraryModule, &'static Definition, Span)> {
    let reference = document.syntax().index.library_reference_at(offset)?;
    let (module, definition) = library_symbol(reference)?;
    Some((module, definition, reference.span))
}

fn library_symbol(
    reference: &LibraryReference,
) -> Option<(&'static LibraryModule, &'static Definition)> {
    if reference.module.is_empty() {
        let module = standard_library()
            .iter()
            .find(|module| module.name == reference.name)?;
        let definition = module.index.definitions().iter().find(|definition| {
            definition.parent.is_none()
                && definition.name == module.name
                && definition.kind == Kind::Space
        })?;
        return Some((module, definition));
    }
    let module = standard_library()
        .iter()
        .find(|module| module.name == reference.module)?;
    let definition = module
        .definitions()
        .find(|definition| definition.name == reference.name)?;
    Some((module, definition))
}

pub(super) fn signature_help(
    document: &mut OpenDocument,
    position: Position,
) -> Option<SignatureHelp> {
    let offset = document.text.offset(position)?;
    let source = &document.text.source;
    let mut protected = highlight::token_spans(&source[..offset])
        .into_iter()
        .filter(|token| {
            matches!(
                token.kind,
                TokenKind::String | TokenKind::Comment | TokenKind::Selector
            )
        })
        .map(|token| token.span)
        .peekable();
    let mut stack = Vec::new();
    for (index, character) in source[..offset].char_indices() {
        while protected.peek().is_some_and(|span| span.end <= index) {
            protected.next();
        }
        if protected.peek().is_some_and(|span| span.start <= index) {
            continue;
        }
        match character {
            '(' | '[' => stack.push((character, index, 0_u32)),
            ')' | ']' => {
                stack.pop();
            }
            ',' => {
                if let Some(('(', _, parameter)) = stack.last_mut() {
                    *parameter += 1;
                }
            }
            _ => {}
        }
    }
    let (_, open, parameter) = *stack
        .iter()
        .rev()
        .find(|(delimiter, _, _)| *delimiter == '(')?;
    let end = source[..open].trim_end().len();
    let start = word_start(source, end);
    let callee = source[start..end].to_owned();
    let index = &document.syntax().index;
    let definition = if let Some((namespace, member)) = callee.rsplit_once("::") {
        if let Some(module) = index.library_namespace_at(namespace, offset) {
            standard_library()
                .iter()
                .find(|entry| entry.name == module)?
                .definitions()
                .find(|definition| definition.name == member)
        } else {
            index
                .members_at(namespace, offset)?
                .into_iter()
                .find(|definition| definition.name == member && definition.kind.is_callable())
        }
    } else {
        index
            .visible_at(offset)
            .into_iter()
            .find(|definition| definition.name == callee && definition.kind.is_callable())
    }?;
    let parameters = definition.detail.split_once('(')?.1.split_once(')')?.0;
    let parameters: Vec<_> = parameters
        .split(',')
        .map(str::trim)
        .filter(|name| !name.is_empty())
        .map(|name| ParameterInformation {
            label: ParameterLabel::Simple(name.into()),
            documentation: None,
        })
        .collect();
    Some(SignatureHelp {
        signatures: vec![SignatureInformation {
            label: definition.detail.clone(),
            documentation: None,
            parameters: Some(parameters),
            active_parameter: Some(parameter),
        }],
        active_signature: Some(0),
        active_parameter: Some(parameter),
    })
}

fn builtin_detail(name: &str) -> String {
    let detail = match name {
        "scope" => "scope name: — lexical namespace with inferred members",
        "native" => "native name(args) -> Type = implementation — host-implemented typed function",
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
        "if" | "else" => {
            "if bool: / if release bool: / if acquire bool: / if acqrel bool: — indented conditional branches"
        }
        "for" | "in" => "for item in range: — ordered, demand-driven typed range iteration",
        "loop" | "break" => {
            "loop: / break value — nearest-loop exit; value type must agree across breaks"
        }
        "true" | "false" => "bool literal",
        _ if name.starts_with("$view::") => {
            "typed built-in range API; use pipeline or direct application"
        }
        _ if name.starts_with("views::") => {
            "compatibility range adaptor; canonical built-in API is $view::"
        }
        _ if name.starts_with("ranges::") => {
            "compatibility range API; canonical built-in API is $view::"
        }
        _ if name.starts_with('$') => {
            "built-in Scope; :: selects a path expression or composes another Scope"
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
    fn canonical_view_and_scope_qualified_function_completion_keep_prefixes() {
        let mut view = OpenDocument::new("items := [true] | $view::fi".into(), 1);
        let end = view.text.end();
        let result = completion(&mut view, end);
        let filter = result
            .iter()
            .find(|item| item.label == "$view::filter")
            .unwrap();
        assert!(
            filter
                .detail
                .as_deref()
                .unwrap()
                .contains("built-in range API")
        );
        assert!(
            matches!(filter.text_edit.as_ref(), Some(CompletionTextEdit::Edit(edit)) if edit.new_text == "$view::filter" && edit.range.start.character == 18)
        );

        let mut scope = OpenDocument::new("root := $lo".into(), 1);
        let end = scope.text.end();
        assert!(completion(&mut scope, end).iter().any(|item| {
            item.label == "$local::" && item.kind == Some(CompletionItemKind::MODULE)
        }));

        let mut alias = OpenDocument::new("api := $view\nitems := [true] | api::fi".into(), 1);
        let end = alias.text.end();
        assert!(completion(&mut alias, end).iter().any(|item| {
            item.label == "filter"
                && matches!(item.text_edit.as_ref(), Some(CompletionTextEdit::Edit(edit)) if edit.new_text == "filter" && edit.range.start.character == 23)
        }));

        let source = "symbol codex(file):\n    return ~/.codex/file\nconfig := $wsl::cod";
        let mut qualified = OpenDocument::new(source.into(), 1);
        let end = qualified.text.end();
        let result = completion(&mut qualified, end);
        let codex = result.iter().find(|item| item.label == "codex").unwrap();
        assert!(
            matches!(codex.text_edit.as_ref(), Some(CompletionTextEdit::Edit(edit)) if edit.new_text == "codex" && edit.range.start.character == 16)
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
    #[test]
    fn standard_library_completion_navigation_and_signatures_share_source() {
        let mut document =
            OpenDocument::new("fs => $std::fs\nready = fs::is_directory(file)\n".into(), 1);
        let position = Position::new(1, 16);
        let uri: Uri = "file:///workspace/main.msync".parse().unwrap();
        let module_location = definition(&mut document, &uri, Position::new(0, 13)).unwrap();
        let module_source = library_source(&module_location.uri).unwrap();
        let module_text = super::super::documents::Text::new(module_source.into());
        assert_eq!(
            &module_source[module_text.offset(module_location.range.start).unwrap()
                ..module_text.offset(module_location.range.end).unwrap()],
            "fs"
        );
        let location = definition(&mut document, &uri, position).unwrap();
        assert!(location.uri.as_str().starts_with("msync-stdlib:///"));
        let source = library_source(&location.uri).unwrap();
        let editor_uri: Uri = location.uri.as_str().replace(":///", ":/").parse().unwrap();
        assert_eq!(library_source(&editor_uri), Some(source));
        let text = super::super::documents::Text::new(source.into());
        let start = text.offset(location.range.start).unwrap();
        let end = text.offset(location.range.end).unwrap();
        assert_eq!(&source[start..end], "is_directory");
        assert!(matches!(
            hover(&mut document, position).unwrap().contents,
            HoverContents::Markup(_)
        ));
        let mut incomplete = OpenDocument::new("fs => $std::fs\nready = fs::is_d".into(), 2);
        let end = incomplete.text.end();
        let item = completion(&mut incomplete, end)
            .into_iter()
            .find(|item| item.label == "is_directory")
            .unwrap();
        let Some(CompletionTextEdit::Edit(edit)) = item.text_edit else {
            panic!("missing edit")
        };
        assert_eq!(edit.range.start, Position::new(1, 12));
        let mut call = OpenDocument::new("fs => $std::fs\nready = fs::exists(".into(), 3);
        let end = call.text.end();
        let help = signature_help(&mut call, end).unwrap();
        assert_eq!(help.signatures[0].label, "native exists(path) -> Bool");
        assert_eq!(help.active_parameter, Some(0));
        let mut ranges = OpenDocument::new("items := $std::ranges::id".into(), 1);
        let end = ranges.text.end();
        assert!(
            completion(&mut ranges, end)
                .iter()
                .any(|item| item.label == "$std::ranges::identity")
        );
        let mut primitive = OpenDocument::new("api => $view\nitems := api::id".into(), 1);
        let end = primitive.text.end();
        assert!(
            !completion(&mut primitive, end)
                .iter()
                .any(|item| item.label == "identity")
        );
    }

    #[test]
    fn explicit_namespace_completion_does_not_leak_outer_declarations() {
        let mut document = OpenDocument::new("outer := file\nscope hello:\n    codex := ~/.codex\nalias => hello\nother := alias::co".into(), 1);
        let end = document.text.end();
        let items = completion(&mut document, end);
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].label, "codex");
    }
}
