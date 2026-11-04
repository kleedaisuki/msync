//! Source-only lexical navigation over the language's owned AST.
//!
//! This index does not evaluate expressions or discover filesystem resources.
//! Type checking remains the compiler's job; details describe syntactically known
//! values and signatures rather than inventing types for generic templates.

use std::collections::{BTreeMap, BTreeSet};
use std::sync::OnceLock;

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
    Native,
    Parameter,
    Iterator,
}

impl SymbolKind {
    pub fn is_callable(self) -> bool {
        matches!(
            self,
            Self::Macro | Self::Symbol | Self::Boolean | Self::Native
        )
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
    namespace_members: BTreeMap<DefinitionId, Names>,
    library_references: Vec<LibraryReference>,
}

#[derive(Clone, Debug)]
enum NamespaceShape {
    Program,
    Endpoint,
    WorkingDirectory,
    File,
    View,
    Library(String),
    Lexical(DefinitionId),
}

/// An actual embedded source module, shared by editor completion and navigation.
pub struct LibraryModule {
    pub name: &'static str,
    pub source: &'static str,
    pub index: SymbolIndex,
}

impl LibraryModule {
    pub fn definitions(&self) -> impl Iterator<Item = &Definition> {
        let parent = self
            .index
            .definitions()
            .iter()
            .find(|definition| {
                definition.parent.is_none()
                    && definition.kind == SymbolKind::Space
                    && definition.name == self.name
            })
            .map(|definition| definition.id);
        self.index.definitions().iter().filter(move |definition| {
            definition.parent == parent && definition.kind != SymbolKind::Parameter
        })
    }
}

#[derive(Clone, Debug)]
pub struct LibraryReference {
    pub module: String,
    pub name: String,
    pub span: Span,
}

pub fn standard_library() -> &'static [LibraryModule] {
    static LIBRARY: OnceLock<Vec<LibraryModule>> = OnceLock::new();
    LIBRARY.get_or_init(|| {
        crate::stdlib::SOURCES
            .iter()
            .map(|(name, source)| {
                let (document, _) = crate::language::parse_recovering(source);
                LibraryModule {
                    name,
                    source,
                    index: SymbolIndex::new(&document),
                }
            })
            .collect()
    })
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

    pub fn is_view_scope(&self, definition: DefinitionId) -> bool {
        matches!(
            self.namespace_shapes.get(&definition),
            Some(NamespaceShape::View)
        )
    }

    pub fn library_references(&self) -> &[LibraryReference] {
        &self.library_references
    }

    pub fn library_reference_at(&self, offset: usize) -> Option<&LibraryReference> {
        self.library_references
            .iter()
            .find(|reference| contains(reference.span, offset))
    }

    pub fn library_namespace_at(&self, namespace: &str, offset: usize) -> Option<String> {
        match self.namespace_at(namespace, offset)? {
            NamespaceShape::View => Some("ranges".into()),
            NamespaceShape::Library(module) => Some(module),
            _ => None,
        }
    }

    pub fn is_view_namespace_at(&self, namespace: &str, offset: usize) -> bool {
        matches!(
            self.namespace_at(namespace, offset),
            Some(NamespaceShape::View)
        )
    }

    pub fn members_at(&self, namespace: &str, offset: usize) -> Option<Vec<&Definition>> {
        let NamespaceShape::Lexical(id) = self.namespace_at(namespace, offset)? else {
            return None;
        };
        let names = self.namespace_members.get(&id)?;
        Some(
            names
                .values
                .values()
                .chain(names.symbols.values())
                .chain(names.macros.values())
                .filter_map(|id| self.definitions.get(*id))
                .collect(),
        )
    }

    fn namespace_at(&self, namespace: &str, offset: usize) -> Option<NamespaceShape> {
        let mut components = namespace.split("::").map(str::trim);
        let first = components.next()?;
        let name = first.strip_prefix('$').map_or(first, str::trim);
        let lexical = self
            .visible_at(offset)
            .into_iter()
            .find(|definition| definition.name == name);
        let builtin = if first.starts_with('$') {
            format!("${name}")
        } else {
            first.into()
        };
        let mut shape = if let Some(definition) = lexical {
            self.namespace_shapes.get(&definition.id)?.clone()
        } else {
            match builtin.as_str() {
                "$std" => NamespaceShape::Library(String::new()),
                "$view" | "views" | "ranges" => NamespaceShape::View,
                _ => return None,
            }
        };
        for component in components {
            shape = self.member_shape(&shape, component)?;
        }
        Some(shape)
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
            S::Scope { name, body } => self.namespace(
                name,
                body,
                statement.span,
                end,
                environment,
                Site { scope, parent },
            ),
            S::Native {
                name,
                parameters,
                result,
                ..
            } => {
                let detail = format!(
                    "native {}({}){}",
                    name.text,
                    parameters
                        .iter()
                        .map(|parameter| parameter.text.as_str())
                        .collect::<Vec<_>>()
                        .join(", "),
                    if matches!(result, crate::language::NativeType::Inferred) {
                        String::new()
                    } else {
                        format!(" -> {}", native_type_name(result))
                    }
                );
                let id = self.definition(name, SymbolKind::Native, statement.span, detail, parent);
                self.install(
                    environment,
                    scope,
                    Namespace::Symbol,
                    name,
                    id,
                    statement.span.end,
                );
            }
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
        let (mut kind, mut detail) = binding_detail(name, op, value, pure_call);
        if op != BindingOp::Space
            && let ExprKind::Path(path) = &match &value.kind {
                ExprKind::Reference { operand, .. } => operand.as_ref(),
                _ => value,
            }
            .kind
            && path.qualifier.is_none()
            && let Some(id) = environment.names.values.get(&path.path)
        {
            let source = self.definitions[*id]
                .detail
                .split_whitespace()
                .next()
                .unwrap_or("value");
            let known = match source {
                "bool" | "string" | "path" | "range" | "scope" | "space" | "program" => source,
                _ => "value",
            };
            detail = format!("{known} {}", name.text);
        }
        let shape = self.binding_shape(value, kind, environment);
        if matches!(
            shape,
            Some(
                NamespaceShape::Endpoint
                    | NamespaceShape::WorkingDirectory
                    | NamespaceShape::File
                    | NamespaceShape::View
                    | NamespaceShape::Library(_)
                    | NamespaceShape::Lexical(_)
            )
        ) {
            kind = SymbolKind::Space;
            detail = format!("scope {}", name.text);
        }
        let id = self.definition(name, kind, span, detail, parent);
        if let Some(shape) = shape {
            self.namespace_shapes.insert(id, shape);
        }
        self.install(environment, scope, Namespace::Value, name, id, span.end);
    }

    fn namespace(
        &mut self,
        name: &Name,
        body: &Block,
        span: Span,
        end: usize,
        outer: &mut Environment,
        site: Site,
    ) {
        let id = self.definition(
            name,
            SymbolKind::Space,
            span,
            format!("scope {}", name.text),
            site.parent,
        );
        let mut inner = outer.clone();
        let scope = self.scope(
            Span {
                start: name.span.end,
                end: bounded_end(end, span.end),
            },
            self.scopes[site.scope].depth + 1,
            &inner,
        );
        self.block(body, &mut inner, scope, Some(id));
        let owned: BTreeSet<_> = self
            .definitions
            .iter()
            .filter(|definition| definition.parent == Some(id))
            .map(|definition| definition.id)
            .collect();
        for namespace in [Namespace::Value, Namespace::Macro, Namespace::Symbol] {
            inner
                .names
                .map_mut(namespace)
                .retain(|_, id| owned.contains(id));
        }
        self.namespace_members.insert(id, inner.names);
        self.namespace_shapes
            .insert(id, NamespaceShape::Lexical(id));
        self.install(outer, site.scope, Namespace::Value, name, id, span.end);
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
        if let Some((callee, arguments)) = self.range_call(expression, true, environment) {
            self.range_scope_reference(callee, environment);
            self.range_arguments(callee, arguments, environment, scope, parent);
            return;
        }
        if self.range_name(expression, true, environment).is_some() {
            self.range_scope_reference(expression, environment);
            return;
        }
        match &expression.kind {
            ExprKind::Path(path) => {
                self.path_reference(expression, path, environment, false, statement_call)
            }
            ExprKind::Reference { operand, .. } => {
                self.expression(operand, environment, scope, parent, false);
            }
            ExprKind::Call { callee, arguments } => {
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
            ExprKind::Range(items) => {
                for item in items {
                    self.expression(item, environment, scope, parent, false);
                }
            }
            ExprKind::Select {
                namespace, item, ..
            } => {
                self.expression(namespace, environment, scope, parent, false);
                if let Some(shape @ (NamespaceShape::Library(_) | NamespaceShape::Lexical(_))) =
                    self.expression_shape(namespace, environment)
                {
                    self.member_expression(
                        item,
                        &shape,
                        environment,
                        scope,
                        parent,
                        statement_call,
                    );
                } else {
                    self.expression(item, environment, scope, parent, false);
                }
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
        if let Some((callee, arguments)) = self.range_call(expression, false, environment) {
            self.range_scope_reference(callee, environment);
            self.range_arguments(callee, arguments, environment, scope, parent);
        } else if self.range_name(expression, false, environment).is_none() {
            self.expression(expression, environment, scope, parent, false);
        } else {
            self.range_scope_reference(expression, environment);
        }
    }

    fn range_scope_reference(&mut self, expression: &Expr, environment: &Environment) {
        match &expression.kind {
            ExprKind::Reference { operand, .. } => {
                if let ExprKind::Path(path) = &operand.kind {
                    self.path_reference(operand, path, environment, false, false);
                }
            }
            ExprKind::Path(path) => {
                if let Some(namespace) = &path.qualifier {
                    let shape = self.qualifier_shape(&namespace.text, environment);
                    if !matches!(namespace.text.as_str(), "$view" | "views" | "ranges")
                        || matches!(
                            shape,
                            Some(NamespaceShape::View | NamespaceShape::Library(_))
                        )
                    {
                        self.value_reference(namespace, environment);
                    }
                    match shape {
                        Some(NamespaceShape::Library(module)) => {
                            self.library_reference(&module, &path.path, expression.source.content)
                        }
                        Some(NamespaceShape::View) => {
                            self.library_reference("ranges", &path.path, expression.source.content)
                        }
                        _ if matches!(namespace.text.as_str(), "views" | "ranges") => {
                            self.library_reference("ranges", &path.path, expression.source.content)
                        }
                        _ => {}
                    }
                } else {
                    self.library_reference("ranges", &path.path, expression.source.content);
                }
            }
            ExprKind::Select {
                namespace, item, ..
            } => {
                if let ExprKind::Path(path) = &namespace.kind {
                    let reserved = path.qualifier.is_none()
                        && matches!(path.path.as_str(), "$view" | "views" | "ranges");
                    if !reserved
                        || matches!(
                            self.qualifier_shape(&path.path, environment),
                            Some(NamespaceShape::View | NamespaceShape::Library(_))
                        )
                    {
                        self.path_reference(namespace, path, environment, false, false);
                    }
                } else {
                    self.range_scope_reference(namespace, environment);
                }
                if let Some(shape @ (NamespaceShape::Library(_) | NamespaceShape::View)) =
                    self.range_namespace_shape(namespace, environment)
                {
                    let module = match shape {
                        NamespaceShape::Library(module) => module,
                        _ => "ranges".into(),
                    };
                    let callee = match &item.kind {
                        ExprKind::Call { callee, .. } => callee.as_ref(),
                        _ => item,
                    };
                    if let ExprKind::Path(path) = &callee.kind {
                        self.library_reference(&module, &path.path, callee.source.content);
                    }
                }
            }
            _ => {}
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
            self.range_name(callee, false, environment),
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
            if let Some(shape @ (NamespaceShape::Library(_) | NamespaceShape::Lexical(_))) = &shape
            {
                self.member_reference(
                    shape,
                    component,
                    expression.source.content,
                    call,
                    statement_call,
                );
                return;
            }
            if matches!(
                shape,
                Some(NamespaceShape::Program | NamespaceShape::File | NamespaceShape::View)
            ) || matches!(shape, Some(NamespaceShape::WorkingDirectory)) && component == "pwd"
            {
                return;
            }
        }
        if expression.source.content.start > expression.source.span.start {
            return;
        }
        let name = path.path.split(['/', '\\']).next().unwrap_or(&path.path);
        let target = if call {
            environment
                .names
                .symbols
                .get(name)
                .or_else(|| {
                    (statement_call && path.qualifier.is_none())
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
        let lexical = name.strip_prefix('$').unwrap_or(name).trim();
        if let Some(id) = environment.names.values.get(lexical) {
            return self.namespace_shapes.get(id).cloned();
        }
        match name {
            "$local" | "$wsl" | "$path" | "$env" => Some(NamespaceShape::Endpoint),
            "$pwd" => Some(NamespaceShape::WorkingDirectory),
            "$file" => Some(NamespaceShape::File),
            "$view" => Some(NamespaceShape::View),
            "$std" => Some(NamespaceShape::Library(String::new())),
            _ => environment
                .names
                .values
                .get(name)
                .and_then(|id| self.namespace_shapes.get(id))
                .cloned(),
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
        self.expression_shape(value, environment)
    }

    fn expression_shape(
        &self,
        expression: &Expr,
        environment: &Environment,
    ) -> Option<NamespaceShape> {
        match &expression.kind {
            ExprKind::Reference { operand, .. } => {
                let ExprKind::Path(path) = &operand.kind else {
                    return self.expression_shape(operand, environment);
                };
                self.qualifier_shape(&format!("${}", path.path), environment)
            }
            ExprKind::Path(path) => match &path.qualifier {
                Some(namespace) => self.member_shape(
                    &self.qualifier_shape(&namespace.text, environment)?,
                    &path.path,
                ),
                None => self.qualifier_shape(&path.path, environment),
            },
            ExprKind::Select {
                namespace, item, ..
            } => {
                let ExprKind::Path(path) = &item.kind else {
                    return None;
                };
                self.member_shape(&self.expression_shape(namespace, environment)?, &path.path)
            }
            _ => None,
        }
    }

    fn member_shape(&self, namespace: &NamespaceShape, name: &str) -> Option<NamespaceShape> {
        match namespace {
            NamespaceShape::Library(module) if module.is_empty() => {
                Some(NamespaceShape::Library(name.into()))
            }
            NamespaceShape::Lexical(id) => self
                .namespace_members
                .get(id)?
                .values
                .get(name)
                .and_then(|id| self.namespace_shapes.get(id))
                .cloned(),
            _ => None,
        }
    }

    fn library_reference(&mut self, module: &str, name: &str, span: Span) {
        self.library_references.push(LibraryReference {
            module: module.into(),
            name: name.into(),
            span,
        });
    }

    fn member_reference(
        &mut self,
        namespace: &NamespaceShape,
        name: &str,
        span: Span,
        call: bool,
        statement_call: bool,
    ) {
        let span = Span {
            start: span.start,
            end: span.start + name.len(),
        };
        match namespace {
            NamespaceShape::Library(module) => self.library_reference(module, name, span),
            NamespaceShape::Lexical(id) => {
                let Some(names) = self.namespace_members.get(id) else {
                    return;
                };
                let target = if call {
                    names
                        .symbols
                        .get(name)
                        .or_else(|| statement_call.then(|| names.macros.get(name)).flatten())
                        .or_else(|| names.values.get(name))
                } else {
                    names.values.get(name)
                };
                if let Some(id) = target.copied() {
                    self.reference(span, id);
                }
            }
            _ => {}
        }
    }

    fn member_expression(
        &mut self,
        expression: &Expr,
        namespace: &NamespaceShape,
        environment: &mut Environment,
        scope: usize,
        parent: Option<DefinitionId>,
        statement_call: bool,
    ) {
        match &expression.kind {
            ExprKind::Path(path) => {
                let name = path.path.split(['/', '\\']).next().unwrap_or(&path.path);
                self.member_reference(
                    namespace,
                    name,
                    expression.source.content,
                    false,
                    statement_call,
                );
            }
            ExprKind::Call { callee, arguments } => {
                if let ExprKind::Path(path) = &callee.kind {
                    self.member_reference(
                        namespace,
                        &path.path,
                        callee.source.content,
                        true,
                        statement_call,
                    );
                }
                for argument in arguments {
                    self.expression(argument, environment, scope, parent, false);
                }
            }
            _ => self.expression(expression, environment, scope, parent, statement_call),
        }
    }

    fn view_namespace(&self, name: &str, environment: &Environment) -> bool {
        match self.qualifier_shape(name, environment) {
            Some(NamespaceShape::View) => true,
            Some(NamespaceShape::Library(module)) => module == "ranges",
            Some(_) => false,
            None => matches!(name, "views" | "ranges"),
        }
    }

    fn range_namespace_shape(
        &self,
        expression: &Expr,
        environment: &Environment,
    ) -> Option<NamespaceShape> {
        self.expression_shape(expression, environment).or_else(|| {
            let ExprKind::Path(path) = &expression.kind else {
                return None;
            };
            (path.qualifier.is_none() && matches!(path.path.as_str(), "views" | "ranges"))
                .then_some(NamespaceShape::View)
        })
    }

    fn range_call<'e>(
        &self,
        expression: &'e Expr,
        qualified: bool,
        environment: &Environment,
    ) -> Option<(&'e Expr, &'e [Expr])> {
        match &expression.kind {
            ExprKind::Call { callee, arguments }
                if self.range_name(callee, qualified, environment).is_some() =>
            {
                Some((callee, arguments))
            }
            ExprKind::Select { item, .. }
                if self
                    .range_name(expression, qualified, environment)
                    .is_some() =>
            {
                let ExprKind::Call { arguments, .. } = &item.kind else {
                    return None;
                };
                Some((expression, arguments))
            }
            _ => None,
        }
    }

    fn range_name<'e>(
        &self,
        expression: &'e Expr,
        qualified: bool,
        environment: &Environment,
    ) -> Option<&'e str> {
        match &expression.kind {
            ExprKind::Path(path) => match &path.qualifier {
                Some(namespace) if self.view_namespace(&namespace.text, environment) => {
                    Some(&path.path)
                }
                None if !qualified => Some(&path.path),
                _ => None,
            },
            ExprKind::Select {
                namespace, item, ..
            } => {
                let shape = self.range_namespace_shape(namespace, environment)?;
                let item = match &item.kind {
                    ExprKind::Call { callee, .. } => callee.as_ref(),
                    _ => item,
                };
                let ExprKind::Path(item) = &item.kind else {
                    return None;
                };
                (matches!(shape, NamespaceShape::View)
                    || matches!(&shape, NamespaceShape::Library(module) if module == "ranges"))
                .then_some(())
                .filter(|_| item.qualifier.is_none())
                .map(|_| item.path.as_str())
            }
            _ => None,
        }
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
        ExprKind::Range(_) => "range",
        ExprKind::Binary {
            op: crate::language::BinaryOp::Pipe,
            ..
        } => "range result",
        ExprKind::Binary { .. } => "bool",
        ExprKind::Path(path) if path.qualifier.is_none() && path.path.starts_with('$') => "scope",
        ExprKind::Path(path) if path.path.contains('<') => "range",
        ExprKind::Path(_) | ExprKind::Join { .. } | ExprKind::Select { .. } => "path",
        ExprKind::Reference { .. } | ExprKind::Loop { .. } | ExprKind::Call { .. } => "value",
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
    environment.names.symbols.contains_key(&path.path)
}

fn native_type_name(result: &crate::language::NativeType) -> String {
    use crate::language::NativeType;
    match result {
        NativeType::Inferred => "inferred".into(),
        NativeType::Void => "Void".into(),
        NativeType::Bool => "Bool".into(),
        NativeType::String => "String".into(),
        NativeType::Path => "Path".into(),
        NativeType::Scope => "Scope".into(),
        NativeType::Range(item) => format!("Range<{}>", native_type_name(item)),
    }
}
