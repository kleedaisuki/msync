//! Name-resolved portable transfers and context-bound transfer snapshots.

use std::collections::BTreeMap;
use std::path::Path;

use serde::{Deserialize, Serialize};

use super::{Backend, CompileContext, Compiler, CopyOp, Location, Program, Space};
use crate::syntax::{OperandSource, Report, Script, Span, StatementKind};

const VERSION: u32 = 1;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Relocatable {
    operations: Vec<Transfer>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Transfer {
    source: PathValue,
    destination: PathValue,
    pattern: Option<String>,
    span: Span,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct PathValue {
    backend: Option<Backend>,
    base: Base,
    path: String,
    is_file: bool,
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
enum Base {
    Literal,
    Pwd,
    File,
    FileParent,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Contextual {
    context: CompileContext,
    program: Program,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "stage", content = "ir", rename_all = "snake_case")]
pub enum Artifact {
    Relocatable(Relocatable),
    Contextual(Contextual),
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Envelope {
    format: String,
    version: u32,
    artifact: Artifact,
}

impl Compiler {
    /// Resolve names and validate types without capturing the caller's cwd/file.
    /// Existing captured path bindings remain captured, as in normal compilation.
    pub fn compile_relocatable(&self, script: &Script) -> Result<Relocatable, Vec<Report>> {
        let context = type_context();
        self.clone().compile_rich_with_context(script, &context)?;
        let mut spaces = self.clone();
        let mut aliases: BTreeMap<_, _> = self
            .aliases
            .iter()
            .map(|(name, alias)| {
                (
                    name.clone(),
                    PathValue {
                        backend: alias.backend.clone(),
                        base: Base::Literal,
                        path: alias.path.clone(),
                        is_file: alias.is_file,
                    },
                )
            })
            .collect();
        let mut operations = Vec::new();
        let mut patterns = std::collections::HashSet::new();
        for statement in &script.statements {
            let result = match &statement.kind {
                StatementKind::Comment(_) => Ok(()),
                StatementKind::Endpoint { name, value } => {
                    spaces.space(value, statement.span).map(|space| {
                        std::sync::Arc::make_mut(&mut spaces.spaces).insert(name.clone(), space);
                    })
                }
                StatementKind::Alias { name, value } => {
                    symbolic(&spaces, &aliases, value, statement.span).map(|value| {
                        aliases.insert(name.clone(), value);
                    })
                }
                StatementKind::Copy {
                    source,
                    destination,
                } => (|| {
                    let mut source = symbolic(&spaces, &aliases, source, statement.span)?;
                    let destination = symbolic(&spaces, &aliases, destination, statement.span)?;
                    let pattern = super::take_selector(&mut source.path, &mut patterns)
                        .map_err(|message| Report::new(message, statement.span))?;
                    operations.push(Transfer {
                        source,
                        destination,
                        pattern,
                        span: statement.span,
                    });
                    Ok(())
                })(),
            };
            result.map_err(|error| vec![error])?;
        }
        Ok(Relocatable { operations })
    }
}

fn symbolic(
    compiler: &Compiler,
    aliases: &BTreeMap<String, PathValue>,
    value: &str,
    span: Span,
) -> Result<PathValue, Report> {
    let (backend, path) = match value.split_once("::") {
        Some((name, path)) => match compiler.space(name, span)? {
            Space::Endpoint(backend) => (Some(backend), path),
            namespace => {
                let item = path.split(['/', '\\']).next().unwrap_or(path);
                let base = match (namespace, item) {
                    (Space::Pwd, "pwd") => Base::Pwd,
                    (Space::File, "this") => Base::File,
                    (Space::File, "parent") => Base::FileParent,
                    _ => return Err(Report::new("invalid contextual namespace item", span)),
                };
                return Ok(PathValue {
                    backend: Some(Backend::Local),
                    base,
                    path: path[item.len()..].to_owned(),
                    is_file: matches!(base, Base::File),
                });
            }
        },
        None => (None, value),
    };
    let name = path.split(['/', '\\']).next().unwrap_or(path);
    let Some(alias) = aliases.get(name) else {
        return Ok(PathValue {
            backend,
            base: Base::Literal,
            path: path.to_owned(),
            is_file: false,
        });
    };
    let mut result = alias.clone();
    result.backend = backend.or(result.backend);
    let suffix = &path[name.len()..];
    if !suffix.is_empty() {
        result.path = format!("{}{}", result.path.trim_end_matches(['/', '\\']), suffix);
    }
    Ok(result)
}

impl PathValue {
    fn bind(&self, context: &CompileContext, span: Span) -> Result<Location, Report> {
        if !matches!(self.base, Base::Literal)
            && (self.backend != Some(Backend::Local)
                || self.is_file != matches!(self.base, Base::File))
        {
            return Err(Report::new("invalid contextual path root in IR", span));
        }
        let path = match self.base {
            Base::Literal => self.path.clone(),
            base => {
                let namespace = if matches!(base, Base::Pwd) {
                    Space::Pwd
                } else {
                    Space::File
                };
                let item = match base {
                    Base::Pwd => "pwd",
                    Base::File => "this",
                    _ => "parent",
                };
                let value = format!("{namespace}::{item}{}", self.path);
                let path = value.split_once("::").unwrap().1;
                Compiler::namespace_item(
                    namespace,
                    path,
                    &value,
                    &OperandSource::unmapped(span),
                    context,
                )?
                .path
            }
        };
        Ok(Location {
            backend: self.backend.clone().unwrap_or(Backend::Local),
            path,
        })
    }
}

impl Relocatable {
    fn materialize(&self, context: &CompileContext) -> Result<Program, Vec<Report>> {
        context.validate().map_err(|report| vec![report])?;
        let operations = self
            .operations
            .iter()
            .map(|operation| {
                Ok(CopyOp {
                    source: operation.source.bind(context, operation.span)?,
                    destination: operation.destination.bind(context, operation.span)?,
                    pattern: operation.pattern.clone(),
                    span: operation.span,
                })
            })
            .collect::<Result<Vec<_>, Report>>()
            .map_err(|report| vec![report])?;
        let program = Program { operations };
        validate_program(&program, context)?;
        Ok(program)
    }

    pub fn bind(&self, context: &CompileContext) -> Result<Contextual, Vec<Report>> {
        Contextual::capture(&self.materialize(context)?, context)
    }
}

impl Contextual {
    /// Freeze local cwd/HOME/ENV/PATH values, not filesystem kinds or dependency proofs.
    pub fn capture(program: &Program, context: &CompileContext) -> Result<Self, Vec<Report>> {
        context.validate().map_err(|report| vec![report])?;
        validate_program(program, context)?;
        let mut program = program.clone();
        for operation in &mut program.operations {
            freeze(&mut operation.source, true, context, operation.span)?;
            freeze(&mut operation.destination, false, context, operation.span)?;
        }
        Ok(Self {
            context: context.clone(),
            program,
        })
    }

    pub fn program(&self) -> &Program {
        &self.program
    }
    pub fn context(&self) -> &CompileContext {
        &self.context
    }

    fn validate(&self) -> Result<(), Vec<Report>> {
        self.context.validate().map_err(|error| vec![error])?;
        validate_program(&self.program, &self.context)?;
        for operation in &self.program.operations {
            for location in [&operation.source, &operation.destination] {
                let invalid = match location.backend {
                    Backend::Local => !Path::new(&location.path).is_absolute(),
                    Backend::Path | Backend::Env => true,
                    _ => false,
                };
                if invalid {
                    return Err(vec![Report::new(
                        "contextual IR requires resolved absolute local paths",
                        operation.span,
                    )]);
                }
            }
        }
        Ok(())
    }
}

fn freeze(
    location: &mut Location,
    source: bool,
    context: &CompileContext,
    span: Span,
) -> Result<(), Vec<Report>> {
    let Some(path) = crate::runtime::local_path_at(location, source, Some(&context.pwd))
        .map_err(|message| vec![Report::new(message, span)])?
    else {
        return Ok(());
    };
    let trailing = location.path.ends_with('/');
    let path = if path.is_absolute() {
        path
    } else {
        context.pwd.join(path)
    };
    let path = std::path::absolute(path).map_err(|error| {
        vec![Report::new(
            format!("cannot resolve contextual IR path: {error}"),
            span,
        )]
    })?;
    location.path = path
        .into_os_string()
        .into_string()
        .map_err(|_| vec![Report::new("resolved IR path is not Unicode", span)])?;
    if trailing && !location.path.ends_with('/') {
        location.path.push('/');
    }
    location.backend = Backend::Local;
    Ok(())
}

fn validate_program(program: &Program, context: &CompileContext) -> Result<(), Vec<Report>> {
    let statements = program
        .operations
        .iter()
        .enumerate()
        .flat_map(|(id, operation)| {
            let source_name = format!("ir_source_{id}");
            let destination_name = format!("ir_destination_{id}");
            let mut source = format!("{source_name}::{}", operation.source.path);
            if let Some(pattern) = &operation.pattern {
                if !source.ends_with('/') {
                    source.push('/');
                }
                source.push_str(&format!("<{pattern}>"));
            }
            let copy = crate::syntax::Statement {
                kind: StatementKind::Copy {
                    source,
                    destination: format!("{destination_name}::{}", operation.destination.path),
                },
                span: operation.span,
                operands: None,
            };
            // Named spaces avoid confusing an IPv6 URL's '::' with the DSL's
            // endpoint/path delimiter. Validate backend values as declarations.
            let endpoint = |name, backend: &Backend| crate::syntax::Statement {
                kind: StatementKind::Endpoint {
                    name,
                    value: backend.to_string(),
                },
                span: operation.span,
                operands: None,
            };
            [
                endpoint(source_name, &operation.source.backend),
                endpoint(destination_name, &operation.destination.backend),
                copy,
            ]
        })
        .collect();
    Compiler::new()
        .compile_rich_with_context(&Script { statements }, context)
        .map(|_| ())
}

fn type_context() -> CompileContext {
    // A type witness only: these paths never reach the emitted IR, and no metadata
    // is read. Context availability is checked when the artifact is bound.
    let root = if cfg!(windows) {
        Path::new("C:\\")
    } else {
        Path::new("/")
    };
    CompileContext {
        pwd: root.to_owned(),
        source_file: Some(root.join("msync-type-witness")),
    }
}

impl Artifact {
    pub fn to_json(&self) -> Result<String, String> {
        self.validate()?;
        serde_json::to_string_pretty(&Envelope {
            format: "msync-ir".to_owned(),
            version: VERSION,
            artifact: self.clone(),
        })
        .map(|text| format!("{text}\n"))
        .map_err(|error| error.to_string())
    }

    pub fn from_json(text: &str) -> Result<Self, String> {
        let envelope: Envelope =
            serde_json::from_str(text).map_err(|error| format!("invalid IR JSON: {error}"))?;
        if envelope.format != "msync-ir" || envelope.version != VERSION {
            return Err(format!(
                "unsupported IR format/version: {} {} (expected msync-ir {VERSION})",
                envelope.format, envelope.version
            ));
        }
        envelope.artifact.validate()?;
        Ok(envelope.artifact)
    }

    fn validate(&self) -> Result<(), String> {
        let reports = match self {
            Self::Relocatable(ir) => ir.materialize(&type_context()).map(|_| ()),
            Self::Contextual(ir) => ir.validate(),
        };
        reports.map_err(|reports| {
            reports
                .into_iter()
                .map(|report| report.diagnostic.message)
                .collect::<Vec<_>>()
                .join("\n")
        })
    }

    pub fn bind(&self, context: &CompileContext) -> Result<Contextual, Vec<Report>> {
        match self {
            Self::Relocatable(ir) => ir.bind(context),
            Self::Contextual(ir) => {
                ir.validate()?;
                Ok(ir.clone())
            }
        }
    }
}
