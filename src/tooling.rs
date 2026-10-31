//! Source-only lexical navigation over the language's owned AST.
//!
//! This index does not evaluate expressions or discover filesystem resources.
//! Type checking remains the compiler's job; details describe syntactically known
//! values and signatures rather than inventing types for generic templates.

use std::collections::{BTreeMap, BTreeSet};

use crate::language::{BindingOp, Block, Document, Expr, ExprKind, Name, Statement, StatementKind};
use crate::syntax::Span;

/// Stable within one index/document version, not across text edits.
pub type DefinitionId = usize;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SymbolKind {
    Binding,
    Space,
    Program,
    Macro,
    Symbol,
    Boolean,
    Parameter,
    Iterator,
}

impl SymbolKind {
    pub fn is_callable(self) -> bool {
        matches!(self, Self::Macro | Self::Symbol | Self::Boolean)
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Definition {
    pub id: DefinitionId,
    pub name: String,
    pub kind: SymbolKind,
    pub span: Span,
    pub selection: Span,
    pub detail: String,
    /// The containing template, when this declaration is nested in one.
    pub parent: Option<DefinitionId>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Reference {
    pub span: Span,
    pub definition: DefinitionId,
}

#[derive(Clone, Debug, Default)]
pub struct SymbolIndex {
    definitions: Vec<Definition>,
    references: Vec<Reference>,
    scopes: Vec<Scope>,
    namespace_shapes: BTreeMap<DefinitionId, NamespaceShape>,
}

#[derive(Clone, Copy, Debug)]
enum NamespaceShape {
    Program,
    WorkingDirectory,
    File,
}

#[derive(Clone, Copy)]
struct Site {
    scope: usize,
    parent: Option<DefinitionId>,
}

struct Template<'a> {
    name: &'a Name,
    parameters: &'a [Name],
    body: &'a Block,
    kind: SymbolKind,
    span: Span,
    end: usize,
}

#[derive(Clone, Debug, Default)]
struct Names {
    values: BTreeMap<String, DefinitionId>,
    macros: BTreeMap<String, DefinitionId>,
    symbols: BTreeMap<String, DefinitionId>,
}

impl Names {
    fn map(&self, namespace: Namespace) -> &BTreeMap<String, DefinitionId> {
        match namespace {
            Namespace::Value => &self.values,
            Namespace::Macro => &self.macros,
            Namespace::Symbol => &self.symbols,
        }
    }

    fn map_mut(&mut self, namespace: Namespace) -> &mut BTreeMap<String, DefinitionId> {
        match namespace {
            Namespace::Value => &mut self.values,
            Namespace::Macro => &mut self.macros,
            Namespace::Symbol => &mut self.symbols,
        }
    }

    fn imported(&self, name: &str) -> Option<(Namespace, DefinitionId)> {
        [Namespace::Value, Namespace::Macro, Namespace::Symbol]
            .into_iter()
            .find_map(|namespace| self.map(namespace).get(name).map(|id| (namespace, *id)))
    }
}

#[derive(Clone, Copy, Debug)]
enum Namespace {
    Value,
    Macro,
    Symbol,
}

#[derive(Clone, Debug, Default)]
struct Environment {
    names: Names,
    captured: Names,
    imported: BTreeSet<String>,
    local_templates: BTreeSet<String>,
}

#[derive(Clone, Debug)]
struct Scope {
    span: Span,
    depth: usize,
    initial: Names,
    events: Vec<Event>,
}

#[derive(Clone, Debug)]
struct Event {
    offset: usize,
    namespace: Namespace,
    name: String,
    definition: Option<DefinitionId>,
}

impl SymbolIndex {
    pub fn new(document: &Document) -> Self {
        let mut index = Self::default();
        let mut environment = Environment::default();
        let root = index.scope(
            Span {
                start: 0,
                end: usize::MAX,
            },
            0,
            &environment,
        );
        index.block(&document.statements, &mut environment, root, None);
        index
            .references
            .sort_by_key(|reference| (reference.span.start, reference.span.end));
        index
    }

    pub fn definitions(&self) -> &[Definition] {
        &self.definitions
    }

    pub fn references(&self) -> &[Reference] {
        &self.references
    }

    /// Resolve the declaration token or a reference under a byte offset.
    pub fn definition_at(&self, offset: usize) -> Option<&Definition> {
        let reference = self
            .references
            .iter()
            .find(|reference| contains(reference.span, offset));
        if let Some(reference) = reference {
            return self.definitions.get(reference.definition);
        }
        self.definitions
            .iter()
            .find(|definition| contains(definition.selection, offset))
    }

    /// Return declarations visible at this byte offset, including separate
    /// callable/value namespaces when they intentionally share one spelling.
    pub fn visible_at(&self, offset: usize) -> Vec<&Definition> {
        let Some(scope) = self
            .scopes
            .iter()
            .filter(|scope| contains(scope.span, offset))
            .max_by_key(|scope| (scope.depth, scope.span.start))
        else {
            return Vec::new();
        };
        let mut names = scope.initial.clone();
        for event in scope
            .events
            .iter()
            .take_while(|event| event.offset <= offset)
        {
            let table = names.map_mut(event.namespace);
            match event.definition {
                Some(id) => {
                    table.insert(event.name.clone(), id);
                }
                None => {
                    table.remove(&event.name);
                }
            }
        }
        names
            .values
            .values()
            .chain(names.macros.values())
            .chain(names.symbols.values())
            .filter_map(|id| self.definitions.get(*id))
            .collect()
    }

    fn scope(&mut self, span: Span, depth: usize, environment: &Environment) -> usize {
        let id = self.scopes.len();
        self.scopes.push(Scope {
            span,
            depth,
            initial: environment.names.clone(),
            events: Vec::new(),
        });
        id
    }

    fn definition(
        &mut self,
        name: &Name,
        kind: SymbolKind,
        span: Span,
        detail: String,
        parent: Option<DefinitionId>,
    ) -> DefinitionId {
        let id = self.definitions.len();
        self.definitions.push(Definition {
            id,
            name: name.text.clone(),
            kind,
            span,
            selection: name.span,
            detail,
            parent,
        });
        id
    }

    fn reference(&mut self, span: Span, definition: DefinitionId) {
        self.references.push(Reference { span, definition });
    }

    fn install(
        &mut self,
        environment: &mut Environment,
        scope: usize,
        namespace: Namespace,
        name: &Name,
        definition: DefinitionId,
        offset: usize,
    ) {
        environment
            .names
            .map_mut(namespace)
            .insert(name.text.clone(), definition);
        self.scopes[scope].events.push(Event {
            offset,
            namespace,
            name: name.text.clone(),
            definition: Some(definition),
        });
    }

    fn block(
        &mut self,
        body: &Block,
        environment: &mut Environment,
        scope: usize,
        parent: Option<DefinitionId>,
    ) {
        for (position, statement) in body.iter().enumerate() {
            let end = body
                .get(position + 1)
                .map_or(self.scopes[scope].span.end, |next| next.span.start);
            self.statement(statement, end, environment, scope, parent);
        }
    }

    fn statement(
        &mut self,
        statement: &Statement,
        end: usize,
        environment: &mut Environment,
        scope: usize,
        parent: Option<DefinitionId>,
    ) {
        use StatementKind as S;
        match &statement.kind {
            S::Binding { name, op, value } => self.binding(
                name,
                *op,
                value,
                statement.span,
                environment,
                Site { scope, parent },
            ),
            S::Macro {
                name,
                parameters,
                body,
            } => self.template(
                Template {
                    name,
                    parameters,
                    body,
                    kind: SymbolKind::Macro,
                    span: statement.span,
                    end,
                },
                environment,
                Site { scope, parent },
            ),
            S::Symbol {
                name,
                parameters,
                body,
            } => self.template(
                Template {
                    name,
                    parameters,
                    body,
                    kind: SymbolKind::Symbol,
                    span: statement.span,
                    end,
                },
                environment,
                Site { scope, parent },
            ),
            S::Boolean {
                name,
                parameters,
                body,
            } => self.template(
                Template {
                    name,
                    parameters,
                    body,
                    kind: SymbolKind::Boolean,
                    span: statement.span,
                    end,
                },
                environment,
                Site { scope, parent },
            ),
            S::Transfer {
                source,
                destination,
            } => {
                self.expression(source, environment, scope, parent, false);
                self.expression(destination, environment, scope, parent, false);
            }
            S::Delete(value) | S::Return(value) | S::Include(value) | S::Execute(value) => {
                self.expression(value, environment, scope, parent, false)
            }
            S::Call(value) => self.expression(value, environment, scope, parent, true),
            S::Global(names) => self.globals(names, statement.span.end, environment, scope),
            S::Drop(names) => self.drop_names(names, statement.span.end, environment, scope),
            S::If {
                condition,
                then_body,
                else_body,
            } => {
                self.expression(condition, environment, scope, parent, false);
                let then_end = else_body.first().map_or(end, |next| next.span.start);
                self.inner_block(then_body, then_end, environment, scope, parent, None);
                self.inner_block(else_body, end, environment, scope, parent, None);
            }
            S::For { name, range, body } => {
                self.expression(range, environment, scope, parent, false);
                let id = self.definition(
                    name,
                    SymbolKind::Iterator,
                    statement.span,
                    format!("range item {}", name.text),
                    parent,
                );
                self.inner_block(body, end, environment, scope, parent, Some((name, id)));
            }
            S::Loop { body } => self.inner_block(body, end, environment, scope, parent, None),
            S::Break(Some(value)) => self.expression(value, environment, scope, parent, false),
            S::Await(Some(name)) => self.value_reference(name, environment),
            S::Scheduled { statement, .. } => {
                self.statement(statement, end, environment, scope, parent)
            }
            S::Break(None) | S::Async | S::Await(None) | S::Comment(_) => {}
        }
    }

    fn binding(
        &mut self,
        name: &Name,
        op: BindingOp,
        value: &Expr,
        span: Span,
        environment: &mut Environment,
        site: Site,
    ) {
        let Site { scope, parent } = site;
        self.expression(value, environment, scope, parent, false);
        if op == BindingOp::Assign
            && let Some(id) = environment.names.values.get(&name.text).copied()
        {
            self.reference(name.span, id);
            return;
        }
        let pure_call = is_pure_call(value, environment);
        let (kind, mut detail) = binding_detail(name, op, value, pure_call);
        if op != BindingOp::Space
            && let ExprKind::Path(path) = &value.kind
            && path.qualifier.is_none()
            && let Some(id) = environment.names.values.get(&path.path)
        {
            let source = self.definitions[*id]
                .detail
                .split_whitespace()
                .next()
                .unwrap_or("value");
            let known = match source {
                "bool" | "string" | "path" | "range" | "space" | "program" => source,
                _ => "value",
            };
            detail = format!("{known} {}", name.text);
        }
        let shape = self.binding_shape(value, kind, environment);
        let id = self.definition(name, kind, span, detail, parent);
        if let Some(shape) = shape {
            self.namespace_shapes.insert(id, shape);
        }
        self.install(environment, scope, Namespace::Value, name, id, span.end);
    }

    fn template(&mut self, template: Template<'_>, outer: &mut Environment, site: Site) {
        let Template {
            name,
            parameters,
            body,
            kind,
            span,
            end,
        } = template;
        let Site { scope, parent } = site;
        let keyword = match kind {
            SymbolKind::Macro => "macro",
            SymbolKind::Boolean => "boolean",
            _ => "symbol",
        };
        let signature = format!(
            "{keyword} {}({})",
            name.text,
            parameters
                .iter()
                .map(|parameter| parameter.text.as_str())
                .collect::<Vec<_>>()
                .join(", ")
        );
        let id = self.definition(name, kind, span, signature, parent);
        let mut inner = template_environment(outer, kind);
        for parameter in parameters {
            let parameter_id = self.definition(
                parameter,
                SymbolKind::Parameter,
                parameter.span,
                format!("parameter {}", parameter.text),
                Some(id),
            );
            inner
                .names
                .values
                .insert(parameter.text.clone(), parameter_id);
        }
        let inner_scope = self.scope(
            Span {
                start: name.span.end,
                end: bounded_end(end, span.end),
            },
            self.scopes[scope].depth + 1,
            &inner,
        );
        self.block(body, &mut inner, inner_scope, Some(id));
        let namespace = if kind == SymbolKind::Macro {
            Namespace::Macro
        } else {
            Namespace::Symbol
        };
        self.install(outer, scope, namespace, name, id, span.end);
        outer.local_templates.insert(name.text.clone());
    }

    fn inner_block(
        &mut self,
        body: &Block,
        end: usize,
        outer: &Environment,
        scope: usize,
        parent: Option<DefinitionId>,
        iterator: Option<(&Name, DefinitionId)>,
    ) {
        let Some(first) = body.first() else {
            return;
        };
        let mut inner = outer.clone();
        if let Some((name, id)) = iterator {
            inner.names.values.insert(name.text.clone(), id);
        }
        let inner_scope = self.scope(
            Span {
                start: first.span.start,
                end: bounded_end(
                    end,
                    body.last().map_or(first.span.end, |last| last.span.end),
                ),
            },
            self.scopes[scope].depth + 1,
            &inner,
        );
        self.block(body, &mut inner, inner_scope, parent);
    }

    fn globals(
        &mut self,
        names: &[Name],
        offset: usize,
        environment: &mut Environment,
        scope: usize,
    ) {
        for name in names {
            let Some((namespace, id)) = environment.captured.imported(&name.text) else {
                continue;
            };
            self.reference(name.span, id);
            let conflict = environment.names.values.contains_key(&name.text)
                || environment.local_templates.contains(&name.text);
            if conflict && !environment.imported.contains(&name.text) {
                continue;
            }
            self.install(environment, scope, namespace, name, id, offset);
            environment.imported.insert(name.text.clone());
        }
    }

    fn drop_names(
        &mut self,
        names: &[Name],
        offset: usize,
        environment: &mut Environment,
        scope: usize,
    ) {
        for name in names {
            for namespace in [Namespace::Value, Namespace::Macro, Namespace::Symbol] {
                if let Some(id) = environment.names.map_mut(namespace).remove(&name.text) {
                    self.reference(name.span, id);
                    self.scopes[scope].events.push(Event {
                        offset,
                        namespace,
                        name: name.text.clone(),
                        definition: None,
                    });
                }
            }
            environment.imported.remove(&name.text);
            environment.local_templates.remove(&name.text);
        }
    }

    fn value_reference(&mut self, name: &Name, environment: &Environment) {
        if let Some(id) = environment.names.values.get(&name.text) {
            self.reference(name.span, *id);
        }
    }

    fn expression(
        &mut self,
        expression: &Expr,
        environment: &mut Environment,
        scope: usize,
        parent: Option<DefinitionId>,
        statement_call: bool,
    ) {
        match &expression.kind {
            ExprKind::Path(path) => {
                self.path_reference(expression, path, environment, false, statement_call)
            }
            ExprKind::Call { callee, arguments } => {
                if range_name(callee, true).is_some() {
                    self.range_arguments(callee, arguments, environment, scope, parent);
                    return;
                }
                if let ExprKind::Path(path) = &callee.kind {
                    self.path_reference(callee, path, environment, true, statement_call);
                } else {
                    self.expression(callee, environment, scope, parent, false);
                }
                for argument in arguments {
                    self.expression(argument, environment, scope, parent, false);
                }
            }
            ExprKind::Join { base, suffix, .. } => {
                self.expression(base, environment, scope, parent, false);
                if let Some(suffix) = suffix {
                    self.expression(suffix, environment, scope, parent, false);
                }
            }
            ExprKind::Select {
                namespace, item, ..
            } => {
                self.expression(namespace, environment, scope, parent, false);
                self.expression(item, environment, scope, parent, false);
            }
            ExprKind::Not(value) | ExprKind::Exists(value) => {
                self.expression(value, environment, scope, parent, false)
            }
            ExprKind::Binary {
                left,
                op: crate::language::BinaryOp::Pipe,
                right,
                ..
            } => {
                self.expression(left, environment, scope, parent, false);
                self.range_adaptor(right, environment, scope, parent);
            }
            ExprKind::Binary { left, right, .. } => {
                self.expression(left, environment, scope, parent, false);
                self.expression(right, environment, scope, parent, false);
            }
            ExprKind::Loop { body } => self.inner_block(
                body,
                expression.span.end.saturating_add(1),
                environment,
                scope,
                parent,
                None,
            ),
            ExprKind::Literal(_) | ExprKind::Bool(_) => {}
        }
    }

    fn range_adaptor(
        &mut self,
        expression: &Expr,
        environment: &mut Environment,
        scope: usize,
        parent: Option<DefinitionId>,
    ) {
        match &expression.kind {
            ExprKind::Call { callee, arguments } if range_name(callee, false).is_some() => {
                self.range_arguments(callee, arguments, environment, scope, parent);
            }
            _ if range_name(expression, false).is_some() => {}
            _ => self.expression(expression, environment, scope, parent, false),
        }
    }

    fn range_arguments(
        &mut self,
        callee: &Expr,
        arguments: &[Expr],
        environment: &mut Environment,
        scope: usize,
        parent: Option<DefinitionId>,
    ) {
        let callback = matches!(
            range_name(callee, false),
            Some(
                "filter"
                    | "transform"
                    | "take_while"
                    | "drop_while"
                    | "any_of"
                    | "any"
                    | "all_of"
                    | "none_of"
                    | "none"
                    | "find_if"
            )
        );
        for (position, argument) in arguments.iter().enumerate() {
            if callback
                && position + 1 == arguments.len()
                && let ExprKind::Path(path) = &argument.kind
                && path.qualifier.is_none()
                && let Some(id) = environment.names.symbols.get(&path.path)
            {
                self.reference(argument.source.content, *id);
                continue;
            }
            self.expression(argument, environment, scope, parent, false);
        }
    }

    fn path_reference(
        &mut self,
        expression: &Expr,
        path: &crate::language::PathExpr,
        environment: &Environment,
        call: bool,
        statement_call: bool,
    ) {
        if let Some(qualifier) = &path.qualifier {
            self.value_reference(qualifier, environment);
            let shape = self.qualifier_shape(&qualifier.text, environment);
            let component = path.path.split(['/', '\\']).next().unwrap_or(&path.path);
            if matches!(shape, Some(NamespaceShape::Program | NamespaceShape::File))
                || matches!(shape, Some(NamespaceShape::WorkingDirectory)) && component == "pwd"
            {
                return;
            }
        }
        if expression.source.content.start > expression.source.span.start {
            return;
        }
        let name = path.path.split(['/', '\\']).next().unwrap_or(&path.path);
        let target = if call && path.qualifier.is_none() {
            environment
                .names
                .symbols
                .get(name)
                .or_else(|| {
                    statement_call
                        .then(|| environment.names.macros.get(name))
                        .flatten()
                })
                .or_else(|| environment.names.values.get(name))
        } else {
            environment.names.values.get(name)
        };
        if let Some(id) = target {
            let start = expression.source.content.start;
            self.reference(
                Span {
                    start,
                    end: start.saturating_add(name.len()),
                },
                *id,
            );
        }
    }
    fn qualifier_shape(&self, name: &str, environment: &Environment) -> Option<NamespaceShape> {
        match name {
            "$pwd" => Some(NamespaceShape::WorkingDirectory),
            "$file" => Some(NamespaceShape::File),
            _ => environment
                .names
                .values
                .get(name)
                .and_then(|id| self.namespace_shapes.get(id))
                .copied(),
        }
    }

    fn binding_shape(
        &self,
        value: &Expr,
        kind: SymbolKind,
        environment: &Environment,
    ) -> Option<NamespaceShape> {
        if kind == SymbolKind::Program {
            return Some(NamespaceShape::Program);
        }
        let ExprKind::Path(path) = &value.kind else {
            return None;
        };
        if path.qualifier.is_some() {
            return None;
        }
        self.qualifier_shape(&path.path, environment)
    }
}

fn contains(span: Span, offset: usize) -> bool {
    span.start <= offset && offset < span.end
}

fn bounded_end(end: usize, last: usize) -> usize {
    if end == usize::MAX {
        last.saturating_add(1)
    } else {
        end
    }
}

fn range_name(expression: &Expr, qualified: bool) -> Option<&str> {
    match &expression.kind {
        ExprKind::Path(path) => match &path.qualifier {
            Some(namespace) if matches!(namespace.text.as_str(), "views" | "ranges") => {
                Some(&path.path)
            }
            None if !qualified => Some(&path.path),
            _ => None,
        },
        ExprKind::Select {
            namespace, item, ..
        } => {
            let ExprKind::Path(namespace) = &namespace.kind else {
                return None;
            };
            let ExprKind::Path(item) = &item.kind else {
                return None;
            };
            (namespace.qualifier.is_none()
                && matches!(namespace.path.as_str(), "views" | "ranges")
                && item.qualifier.is_none())
            .then_some(item.path.as_str())
        }
        _ => None,
    }
}

fn template_environment(outer: &Environment, kind: SymbolKind) -> Environment {
    let mut captured = outer.captured.clone();
    captured.values.extend(outer.names.values.clone());
    captured.macros = outer.names.macros.clone();
    captured.symbols = outer.names.symbols.clone();
    Environment {
        names: Names {
            values: BTreeMap::new(),
            macros: if kind == SymbolKind::Macro {
                outer.names.macros.clone()
            } else {
                BTreeMap::new()
            },
            symbols: outer.names.symbols.clone(),
        },
        captured,
        ..Environment::default()
    }
}

fn binding_detail(
    name: &Name,
    op: BindingOp,
    value: &Expr,
    pure_call: bool,
) -> (SymbolKind, String) {
    let known = match &value.kind {
        ExprKind::Literal(_) => "string",
        ExprKind::Bool(_) | ExprKind::Not(_) | ExprKind::Exists(_) => "bool",
        ExprKind::Binary {
            op: crate::language::BinaryOp::Pipe,
            ..
        } => "range result",
        ExprKind::Binary { .. } => "bool",
        ExprKind::Path(path) if path.path.contains('<') => "range",
        ExprKind::Path(_) | ExprKind::Join { .. } | ExprKind::Select { .. } => "path",
        ExprKind::Loop { .. } | ExprKind::Call { .. } => "value",
    };
    if op == BindingOp::Space {
        if matches!(value.kind, ExprKind::Call { .. }) && !pure_call {
            return (SymbolKind::Program, format!("program {}", name.text));
        }
        return (SymbolKind::Space, format!("space {}", name.text));
    }
    (SymbolKind::Binding, format!("{known} {}", name.text))
}

fn is_pure_call(value: &Expr, environment: &Environment) -> bool {
    let ExprKind::Call { callee, .. } = &value.kind else {
        return false;
    };
    let ExprKind::Path(path) = &callee.kind else {
        return false;
    };
    path.qualifier.is_none() && environment.names.symbols.contains_key(&path.path)
}
