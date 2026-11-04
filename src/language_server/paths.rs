//! Filesystem suggestions resolved by the ordinary typed compiler, never by a
//! second interpretation of endpoint names. Only local locations are inspected.

use lsp_types::{Command, CompletionItem, CompletionItemKind, CompletionTextEdit, TextEdit};

use crate::compiler::{CompileContext, structured::Session};
use crate::endpoint::Backend;
use crate::highlight::{self, TokenKind};
use crate::language::{self, Document, Expr, ExprKind, StatementKind};
use crate::syntax::Span;

use super::documents::OpenDocument;

pub(super) struct ScopeCache {
    source: String,
    context: CompileContext,
    session: Session,
    target: Option<Expr>,
}

struct Query {
    parent: String,
    operator: &'static str,
    prefix: String,
    replacement: Span,
    literal_directory: Option<String>,
}

pub(super) fn completion(
    document: &mut OpenDocument,
    offset: usize,
    context: &CompileContext,
) -> Result<Option<Vec<CompletionItem>>, String> {
    let Some(query) = query(&document.text.source, offset) else {
        return Ok(None);
    };
    let Some(info) = inspect_completion(
        document,
        &query.parent,
        query.replacement.start,
        if query.operator == "::" {
            crate::types::OperatorTrait::Select
        } else {
            crate::types::OperatorTrait::Join
        },
        context,
    ) else {
        return Ok(None);
    };
    let range = document.text.range(query.replacement);
    let names: std::collections::BTreeSet<_> = document
        .syntax()
        .index
        .visible_at(offset)
        .into_iter()
        .map(|definition| definition.name.clone())
        .collect();
    let mut items: Vec<_> = info
        .members
        .iter()
        .filter(|member| member.starts_with(&query.prefix))
        .map(|member| CompletionItem {
            label: format!("{}::{member}", query.parent),
            kind: Some(CompletionItemKind::PROPERTY),
            detail: Some("inferred Scope member".into()),
            filter_text: Some((*member).into()),
            text_edit: Some(CompletionTextEdit::Edit(TextEdit::new(
                range,
                (*member).into(),
            ))),
            ..CompletionItem::default()
        })
        .collect();
    let Some(location) = info.location else {
        return Ok((!info.members.is_empty()).then_some(items));
    };
    items.extend(super::providers::operand_completion(
        document,
        offset,
        &query.prefix,
        range,
        info.members,
    ));
    if location.backend != Backend::Local {
        // Completing an edit never admits a remote operation or asks for SSH
        // credentials. Remote prefixes remain available to ordinary completion.
        return Ok(Some(items));
    }
    let Some(directory) = crate::runtime::local_path_at(&location, true, Some(&context.pwd))?
    else {
        return Ok(None);
    };
    let entries = match std::fs::read_dir(&directory) {
        Ok(entries) => entries,
        Err(error)
            if matches!(
                error.kind(),
                std::io::ErrorKind::NotFound | std::io::ErrorKind::NotADirectory
            ) =>
        {
            return Ok(Some(items));
        }
        Err(error) => {
            return Err(format!(
                "cannot complete files in '{}': {error}",
                directory.display()
            ));
        }
    };
    for entry in entries {
        let entry = entry.map_err(|error| {
            format!("cannot read an entry in '{}': {error}", directory.display())
        })?;
        let name = entry.file_name();
        let Some(name) = name.to_str().filter(|name| name.starts_with(&query.prefix)) else {
            continue;
        };
        // Follow a symlink for its completion kind, just as normal filesystem
        // navigation does. A dangling symlink is still a useful file suggestion.
        let folder = entry.path().is_dir();
        let mut spelling = if let Some(directory) = &query.literal_directory {
            let mut spelling = String::new();
            language::literal::write_string(&mut spelling, &format!("{directory}{name}"));
            spelling
        } else {
            path_spelling(
                name,
                names.contains(name.split(['/', '\\']).next().unwrap_or(name))
                    || info.members.contains(&name),
            )
        };
        if folder {
            spelling.push('/');
        }
        items.push(CompletionItem {
            label: if folder {
                format!("{name}/")
            } else {
                name.into()
            },
            kind: Some(if folder {
                CompletionItemKind::FOLDER
            } else {
                CompletionItemKind::FILE
            }),
            detail: Some(entry.path().display().to_string()),
            filter_text: Some(name.into()),
            sort_text: Some(format!("{}{name}", if folder { '0' } else { '1' })),
            text_edit: Some(CompletionTextEdit::Edit(TextEdit::new(range, spelling))),
            command: folder.then(|| Command {
                title: "Continue path completion".into(),
                command: "editor.action.triggerSuggest".into(),
                arguments: None,
            }),
            ..CompletionItem::default()
        });
    }
    items.sort_by(|left, right| left.sort_text.cmp(&right.sort_text));
    Ok(Some(items))
}

fn inspect_completion(
    document: &mut OpenDocument,
    projection: &str,
    offset: usize,
    operator: crate::types::OperatorTrait,
    context: &CompileContext,
) -> Option<crate::compiler::structured::PathCompletionInfo> {
    let line = document.text.source[..offset]
        .rfind(['\n', '\r'])
        .map_or(0, |index| index + 1);
    let suffix = &document.text.source[line..offset];
    let indent = suffix.len() - suffix.trim_start().len();
    if indent == 0 {
        prepare_scope(document, line, context);
        return document
            .path_scope
            .as_ref()?
            .session
            .inspect_path_parent(&expression(projection)?, operator, context)
            .ok();
    }
    let source = format!(
        "{}{}__editor_file_query := {projection}\n",
        &document.text.source[..line],
        &suffix[..indent]
    );
    if document
        .path_scope
        .as_ref()
        .is_none_or(|cache| cache.source != source || cache.context != *context)
    {
        let (prefix, _) = language::parse_recovering(&source);
        fn query_path(statements: &[language::Statement]) -> Option<String> {
            for statement in statements.iter().rev() {
                match &statement.kind {
                    StatementKind::Binding { name, .. } if name.text == "__editor_file_query" => {
                        return Some(format!("${}", name.text));
                    }
                    StatementKind::Scope { name, body } => {
                        if let Some(member) = query_path(body) {
                            return Some(format!("${}::{member}", name.text));
                        }
                    }
                    _ => {}
                }
            }
            None
        }
        let Some(target) = query_path(&prefix.statements).and_then(|path| expression(&path)) else {
            // An unselected generic template has no argument environment yet;
            // its root/builtin path expressions can still be statically known.
            prepare_scope(document, line, context);
            return document
                .path_scope
                .as_ref()?
                .session
                .inspect_path_parent(&expression(projection)?, operator, context)
                .ok();
        };
        let mut session = Session::new();
        for statement in prefix.statements {
            if declaration(&statement.kind) {
                let _ = session.compile(
                    &Document {
                        statements: vec![statement],
                    },
                    context,
                );
            }
        }
        document.path_scope = Some(ScopeCache {
            source,
            context: context.clone(),
            session,
            target: Some(target),
        });
    }
    let cache = document.path_scope.as_ref()?;
    cache
        .session
        .inspect_path_parent(cache.target.as_ref()?, operator, context)
        .ok()
}

fn declaration(kind: &StatementKind) -> bool {
    matches!(
        kind,
        StatementKind::Binding { .. }
            | StatementKind::Scope { .. }
            | StatementKind::Native { .. }
            | StatementKind::Macro { .. }
            | StatementKind::Symbol { .. }
            | StatementKind::Boolean { .. }
            | StatementKind::Include(_)
            | StatementKind::Drop(_)
    )
}

pub(super) fn binding_type(
    document: &mut OpenDocument,
    selection: Span,
    context: &CompileContext,
) -> Option<crate::types::Type> {
    fn target(statements: &[language::Statement], selection: Span) -> Option<(Expr, usize)> {
        for statement in statements {
            match &statement.kind {
                StatementKind::Binding { name, value, .. } if name.span == selection => {
                    return Some((value.clone(), statement.span.start));
                }
                StatementKind::Scope { name, body } => {
                    if let Some((member, _)) = member_name(body, selection) {
                        return Some((
                            expression(&format!("${}::{member}", name.text))?,
                            statement.span.end,
                        ));
                    }
                }
                _ => {}
            }
        }
        None
    }
    fn member_name(statements: &[language::Statement], selection: Span) -> Option<(String, usize)> {
        for statement in statements {
            match &statement.kind {
                StatementKind::Binding { name, .. } if name.span == selection => {
                    return Some((format!("${}", name.text), statement.span.end));
                }
                StatementKind::Scope { name, body } => {
                    if let Some((member, end)) = member_name(body, selection) {
                        return Some((format!("${}::{member}", name.text), end));
                    }
                }
                _ => {}
            }
        }
        None
    }
    let (expression, before) = target(&document.syntax().document.statements, selection)?;
    inspect(document, &expression, before, context).map(|info| info.ty)
}

pub(super) fn inspect(
    document: &mut OpenDocument,
    expression: &Expr,
    before: usize,
    context: &CompileContext,
) -> Option<crate::compiler::structured::ExpressionInfo> {
    prepare_scope(document, before, context);
    document
        .path_scope
        .as_ref()?
        .session
        .inspect_expression(expression, context)
        .ok()
}

fn prepare_scope(document: &mut OpenDocument, before: usize, context: &CompileContext) {
    let preamble = document
        .syntax()
        .document
        .statements
        .iter()
        .take_while(|statement| statement.span.end <= before)
        .last()
        .map_or(0, |statement| statement.span.end);
    let source = &document.text.source[..preamble];
    if document
        .path_scope
        .as_ref()
        .is_none_or(|cache| cache.source != source || cache.context != *context)
    {
        let (prefix, _) = language::parse_recovering(source);
        let mut session = Session::new();
        // Unfinished or unrelated erroneous declarations must not erase the
        // usable context before them. Compilation does not execute any effects.
        for statement in prefix.statements {
            if declaration(&statement.kind) {
                let _ = session.compile(
                    &Document {
                        statements: vec![statement],
                    },
                    context,
                );
            }
        }
        document.path_scope = Some(ScopeCache {
            source: source.into(),
            context: context.clone(),
            session,
            target: None,
        });
    }
}

fn expression(source: &str) -> Option<Expr> {
    let document = language::parse(&format!("__editor_path := {source}\n")).ok()?;
    let StatementKind::Binding { value, .. } = document.statements.into_iter().next()?.kind else {
        return None;
    };
    Some(value)
}

fn path_spelling(name: &str, shadowed: bool) -> String {
    // Ordinary filenames remain ordinary Path operands. A String is needed
    // only when the grammar or actual lexical/member lookup would change the
    // selected basename's meaning.
    if !shadowed
        && !name.chars().any(char::is_whitespace)
        && let Some(Expr {
            kind: ExprKind::Path(path),
            ..
        }) = expression(name)
        && path.qualifier.is_none()
        && path.path == name
        && path.mode == language::DestinationMode::Exact
        && !name.contains('<')
    {
        return name.into();
    }
    let mut quoted = String::new();
    language::literal::write_string(&mut quoted, name);
    quoted
}

fn query(source: &str, offset: usize) -> Option<Query> {
    let tokens = highlight::token_spans(&source[..offset]);
    if tokens
        .last()
        .is_some_and(|token| matches!(token.kind, TokenKind::Comment | TokenKind::Selector))
    {
        return None;
    }
    let last = tokens.last().copied();
    let mut start = 0;
    let mut stack = Vec::new();
    let mut separator = None;
    for token in tokens {
        let begin = token.span.start;
        let end = token.span.end;
        let spelling = &source[begin..end];
        if token.kind == TokenKind::Operator && matches!(spelling, "/" | "::") {
            separator = Some((start, begin, end));
        } else if token.kind == TokenKind::Operator && spelling != "$"
            || token.kind == TokenKind::Command && !matches!(spelling, "true" | "false")
        {
            start = end;
            separator = None;
        } else if token.kind == TokenKind::Text {
            if spelling.contains(['\n', '\r']) {
                start = end;
                separator = None;
            }
            match spelling {
                "(" | "[" => {
                    stack.push((start, separator));
                    start = end;
                    separator = None;
                }
                ")" | "]" => {
                    if let Some((outer, previous)) = stack.pop() {
                        start = outer;
                        separator = previous;
                    }
                }
                "," => {
                    start = end;
                    separator = None;
                }
                _ => {}
            }
        }
    }
    let Some((start, begin, end)) = separator else {
        let token = last.filter(|token| token.kind == TokenKind::String)?;
        let (directory, prefix, replacement) = literal_prefix(source, token.span.start, offset)?;
        if directory.is_empty() {
            return None;
        }
        let mut parent = String::new();
        language::literal::write_string(&mut parent, &directory);
        return Some(Query {
            parent,
            operator: "/",
            prefix,
            replacement,
            literal_directory: Some(directory),
        });
    };
    let mut parent = source[start..begin].trim().to_owned();
    if parent.is_empty() {
        if &source[begin..end] != "/" {
            return None;
        }
        parent.push('/');
    } else if parent.ends_with("::") {
        parent.push('.');
    }
    let suffix = &source[end..offset];
    let leading = suffix.len() - suffix.trim_start().len();
    let raw = &suffix[leading..];
    let mut operation = if &source[begin..end] == "::" {
        "::"
    } else {
        "/"
    };
    let (prefix, literal_directory, replacement) = if raw.starts_with('"') {
        let (directory, prefix, replacement) = literal_prefix(source, end + leading, offset)?;
        if !directory.is_empty() {
            let mut spelling = String::new();
            language::literal::write_string(&mut spelling, &directory);
            parent = format!("({parent}) {operation} {spelling}");
            operation = "/";
        }
        (prefix, Some(directory), replacement)
    } else {
        if raw.chars().any(char::is_whitespace) || raw.contains(['(', ')', '[', ']', ',', '$', '"'])
        {
            return None;
        }
        (
            raw.into(),
            None,
            Span {
                start: end + leading,
                end: offset,
            },
        )
    };
    Some(Query {
        parent,
        operator: operation,
        prefix,
        literal_directory,
        replacement,
    })
}

fn literal_prefix(source: &str, start: usize, offset: usize) -> Option<(String, String, Span)> {
    let prefix_source = &source[start..offset];
    let prefix_token = language::literal::scan_string(prefix_source, 0)?;
    if prefix_token.span.end != prefix_source.len() {
        return None;
    }
    let (decoded, _) = language::literal::decode_string(prefix_source, prefix_token, 0);
    let full = language::literal::scan_string(source, start)?;
    let replacement = Span {
        start,
        end: if full.terminated {
            full.span.end
        } else {
            offset
        },
    };
    let boundary = decoded
        .rfind(std::path::is_separator)
        .map_or(0, |separator| {
            separator + decoded[separator..].chars().next().unwrap().len_utf8()
        });
    Some((
        decoded[..boundary].into(),
        decoded[boundary..].into(),
        replacement,
    ))
}
