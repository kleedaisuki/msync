//! Bounded, definition-time expansion provenance, independent of executable IR.

use super::*;

const FRAME_LIMIT: usize = 16;
const VALUE_LIMIT: usize = 160;

pub(super) type ExpansionMap = imbl::OrdMap<String, ExpansionTrace>;

#[derive(Clone, Debug)]
struct Frame {
    site: SourceSpan,
    name: String,
    kind: &'static str,
    value: String,
    origin: Origin,
}

#[derive(Clone, Copy, Debug)]
pub(super) enum Origin {
    Item,
    Parameter,
    SymbolParameter,
    Imported,
}

#[derive(Clone, Debug, Default)]
pub(super) struct ExpansionTrace {
    frames: Vec<Arc<Frame>>,
    truncated: bool,
}

impl ExpansionTrace {
    pub fn new(name: &str, value: &Value, site: SourceSpan, inputs: Self, origin: Origin) -> Self {
        let mut trace = Self::default();
        trace.frames.push(Arc::new(Frame {
            site,
            name: short(name),
            kind: match value {
                Value::Space(_) => "space alias",
                Value::Process(_) => "program",
                _ => "item",
            },
            value: describe(value),
            origin,
        }));
        trace.extend(&inputs);
        trace
    }

    pub fn declaration(&self) -> Option<SourceSpan> {
        self.frames.first().map(|frame| frame.site.clone())
    }

    fn extend(&mut self, other: &Self) {
        self.truncated |= other.truncated;
        for frame in &other.frames {
            if self.frames.iter().any(|seen| Arc::ptr_eq(seen, frame)) {
                continue;
            }
            if self.frames.len() == FRAME_LIMIT {
                self.truncated = true;
                continue;
            }
            self.frames.push(Arc::clone(frame));
        }
    }

    pub fn inputs(expression: &Expr, scope: &Scope) -> Self {
        let mut trace = Self::default();
        trace.expression(expression, scope);
        trace
    }

    fn item(&mut self, name: &str, scope: &Scope) {
        if let Some(trace) = scope.expansions.get(name) {
            self.extend(trace);
        }
    }

    fn expression(&mut self, expression: &Expr, scope: &Scope) {
        match &expression.kind {
            ExprKind::Path(path) => {
                let item = path.path.split(['/', '\\']).next().unwrap_or(&path.path);
                if let Some(name) = &path.qualifier {
                    self.item(&name.text, scope);
                    if expression.source.content.start > expression.source.span.start {
                        return;
                    }
                    let lookup = match scope.values.get(&name.text) {
                        Some(Value::Space(Space::Endpoint(_))) => true,
                        Some(Value::Space(Space::Pwd)) => item != "pwd",
                        Some(_) => false,
                        None => match name.text.as_str() {
                            "$local" | "$wsl" | "$path" | "$env" => true,
                            "$pwd" => item != "pwd",
                            _ => false,
                        },
                    };
                    if !lookup {
                        return;
                    }
                }
                self.item(item, scope);
            }
            ExprKind::Join { base, suffix, .. } => {
                self.expression(base, scope);
                if let Some(suffix) = suffix {
                    self.expression(suffix, scope);
                }
            }
            ExprKind::Select {
                namespace, item, ..
            } => {
                self.expression(namespace, scope);
                // Computed namespace members may be intrinsic names; do not
                // invent a lexical RHS lookup when only a member is selected.
                let _ = item;
            }
            ExprKind::Call { callee, arguments } => {
                if !symbols::is_call(expression, scope) {
                    self.expression(callee, scope);
                }
                for argument in arguments {
                    self.expression(argument, scope);
                }
            }
            ExprKind::Binary { left, right, .. } => {
                self.expression(left, scope);
                self.expression(right, scope);
            }
            ExprKind::Not(value) | ExprKind::Exists(value) => self.expression(value, scope),
            ExprKind::Loop { .. } | ExprKind::Bool(_) | ExprKind::Literal(_) => {}
        }
    }
}

pub(super) struct Failure {
    report: Box<Report>,
    trace: ExpansionTrace,
    located: Option<Box<LocatedReport>>,
}

impl From<Report> for Failure {
    fn from(report: Report) -> Self {
        Self {
            report: Box::new(report),
            trace: ExpansionTrace::default(),
            located: None,
        }
    }
}

impl Failure {
    pub fn located(report: LocatedReport) -> Self {
        Self {
            report: report.report.clone(),
            trace: ExpansionTrace::default(),
            located: Some(Box::new(report)),
        }
    }

    pub fn with_expression(mut self, expression: &Expr, scope: &Scope) -> Self {
        if self.located.is_none()
            && self.trace.frames.is_empty()
            && contains(expression.span, self.report.diagnostic.span)
        {
            self.trace = failure_inputs(expression, self.report.diagnostic.span, scope);
        }
        self
    }

    pub fn locate(self, source: Arc<Source>) -> LocatedReport {
        if let Some(located) = self.located {
            return *located;
        }
        let mut located = LocatedReport::new(source, *self.report);
        for frame in self.trace.frames {
            let (kind, site) = match frame.origin {
                Origin::Item => (frame.kind.to_owned(), "bound here"),
                Origin::Parameter => ("macro parameter".to_owned(), "argument bound here"),
                Origin::SymbolParameter => ("symbol parameter".to_owned(), "argument bound here"),
                Origin::Imported => (format!("imported {}", frame.kind), "source origin"),
            };
            located = located.label(
                frame.site.clone(),
                format!("{kind} '{}' expands to {}; {site}", frame.name, frame.value),
            );
        }
        if self.trace.truncated {
            located.report.help.push(format!("Further item expansion frames omitted (limit {FRAME_LIMIT}); inspect the shown bindings or use /bindings."));
        }
        located
    }
}

pub(super) fn resolve(
    expression: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    super::resolve(expression, scope, context)
        .map_err(|failure| failure.with_expression(expression, scope))
}

fn failure_inputs(expression: &Expr, span: Span, scope: &Scope) -> ExpansionTrace {
    let expression = failing_expression(expression, span);
    if matches!(expression.kind, ExprKind::Call { .. }) {
        // Calls rejected as values never resolve their callee or arguments.
        return ExpansionTrace::default();
    }
    ExpansionTrace::inputs(expression, scope)
}

fn failing_expression(expression: &Expr, span: Span) -> &Expr {
    match &expression.kind {
        ExprKind::Join { operator, .. } if *operator == span => expression,
        ExprKind::Join { base, suffix, .. } => {
            if contains(base.span, span) {
                return failing_expression(base, span);
            }
            suffix
                .as_deref()
                .filter(|suffix| contains(suffix.span, span))
                .map_or(expression, |suffix| failing_expression(suffix, span))
        }
        ExprKind::Select {
            namespace,
            item,
            operator,
        } => {
            if *operator == span {
                return expression;
            }
            if contains(namespace.span, span) {
                return failing_expression(namespace, span);
            }
            if contains(item.span, span) {
                return failing_expression(item, span);
            }
            expression
        }
        ExprKind::Call { callee, arguments } => std::iter::once(callee.as_ref())
            .chain(arguments)
            .find(|argument| contains(argument.span, span))
            .map_or(expression, |argument| failing_expression(argument, span)),
        _ => expression,
    }
}

fn contains(outer: Span, inner: Span) -> bool {
    outer.start <= inner.start && inner.end <= outer.end
}

pub(super) fn describe(value: &Value) -> String {
    match value {
        Value::Bool(value) => format!("bool {value}"),
        Value::Dynamic(value) => format!("runtime {:?}", value.ty),
        Value::Range(value) => format!("range<{:?}>", value.item_type),
        Value::Path(path) => path_description(path),
        Value::Literal(text) => format!("string literal {:?}", short(text)),
        Value::Space(space) => format!("space {}", short(&space.to_string())),
        Value::Process(process) => format!("program #{}", process.0),
        Value::Port(process, port) => {
            let item = match port {
                Port::Stdin => "stdin",
                Port::Read(ReadPort::Stdout) => "stdout",
                Port::Read(ReadPort::Stderr) => "stderr",
                Port::Read(ReadPort::ExitCode) => "exit_code",
                Port::Read(ReadPort::Pid) => "pid",
                Port::Read(ReadPort::Meta) => "meta",
            };
            format!("program item #{}::{item}", process.0)
        }
    }
}

pub(super) fn path_description(path: &PathValue) -> String {
    match &path.backend {
        Some(backend) => format!(
            "path item {}::{:?}",
            short(&backend.to_string()),
            short(&path.path)
        ),
        None => format!("endpoint-relative path item {:?}", short(&path.path)),
    }
}

fn short(value: &str) -> String {
    let mut chars = value.chars();
    let mut short: String = chars.by_ref().take(VALUE_LIMIT).collect();
    if chars.next().is_some() {
        short.push('…');
    }
    short
}
