//! Pure lexical instantiation of a validated typed module.
//!
//! This pass substitutes lexical context and fresh program identities only.
//! Filesystem observations and PATH searches belong to the interpreter's current
//! basic block, after the previous block has finished.

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::compiler::CompileContext;
use crate::compiler::structured::Session;
use crate::endpoint::{Backend, Location};
use crate::hir::graph::{self, PlanGraph};
use crate::hir::{
    self, Argument, Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, Region,
};
use crate::object::ObjectModule;
use crate::relocatable::{Anchor, Export, ExportValue, Relocation, Slot};
use crate::source::{Origins, Source, SourceMap, SourceSpan};

/// Bindings for one module instantiation. Overrides are read only on demand.
#[derive(Clone, Debug)]
pub struct Context {
    pub pwd: PathBuf,
    pub module_file: Option<PathBuf>,
    pub home: Option<PathBuf>,
    pub environment: BTreeMap<String, String>,
    pub modules: BTreeMap<String, PathBuf>,
    pub endpoint_cwds: HashMap<Backend, String>,
}

impl Context {
    pub fn new(context: &CompileContext) -> Self {
        Self {
            pwd: context.pwd.clone(),
            module_file: context.source_file.clone(),
            home: None,
            environment: BTreeMap::new(),
            modules: BTreeMap::new(),
            endpoint_cwds: HashMap::new(),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Obligation {
    OpenEndpoint {
        instruction: InstructionId,
        backend: Backend,
    },
    SearchPath {
        instruction: InstructionId,
        name: String,
    },
    ForeignHome {
        instruction: InstructionId,
        backend: Backend,
    },
    InheritedEndpointCwd {
        instruction: InstructionId,
        backend: Backend,
    },
    ProgramLaunch {
        instruction: InstructionId,
        process: ProcessId,
        backend: Backend,
    },
}

/// Bound semantic IR and owned debug information, never a trusted runtime plan.
#[derive(Clone, Debug)]
pub struct ContextualizedModule {
    pub program: hir::Program,
    pub graph: PlanGraph,
    pub source: Arc<Source>,
    pub sources: SourceMap,
    pub origins: Origins,
    pub obligations: Vec<Obligation>,
}

#[derive(Clone, Debug)]
pub struct ContextError {
    pub message: String,
    pub help: Vec<String>,
    pub instruction: Option<InstructionId>,
    pub site: Option<SourceSpan>,
}

impl ContextError {
    fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            help: Vec::new(),
            instruction: None,
            site: None,
        }
    }

    fn hint(mut self, hint: impl Into<String>) -> Self {
        self.help.push(hint.into());
        self
    }

    fn at(mut self, id: InstructionId, origins: &Origins) -> Self {
        self.instruction = Some(id);
        self.site = origins.get(&id).map(|origin| origin.site.clone());
        self
    }

    fn required_by(mut self, id: InstructionId) -> Self {
        self.instruction = Some(id);
        self
    }
}

impl fmt::Display for ContextError {
    fn fmt(&self, output: &mut fmt::Formatter<'_>) -> fmt::Result {
        output.write_str(&self.message)
    }
}

impl std::error::Error for ContextError {}

/// Validate, instantiate and atomically install the module's runtime exports.
pub fn contextualize(
    module: &ObjectModule,
    context: &Context,
    session: &mut Session,
) -> Result<ContextualizedModule, ContextError> {
    module
        .validate()
        .map_err(|cause| ContextError::new(format!("invalid object module: {cause}")))?;
    validate_context(context)?;
    let (source, sources, origins, export_sites) = module
        .debug
        .materialize()
        .map_err(|cause| ContextError::new(format!("invalid object debug information: {cause}")))?;
    let mut candidate = session.clone();
    let (mut program, mut obligations) = bind_program(
        &module.program,
        &module.relocations,
        &module.main_module,
        context,
        &origins,
    )?;
    let mut exports = module.exports.clone();
    bind_exports(&mut exports, &module.main_module, context)?;
    let maps = IdMaps::new(&program, candidate.object_id_bases())?;
    maps.program(&mut program)?;
    maps.exports(&mut exports)?;
    collect_obligations(&program.body, &mut obligations);
    let graph = graph::build(&program);
    candidate
        .import_object_exports(&exports, &export_sites, &program)
        .map_err(|report| {
            let mut error = ContextError::new(report.diagnostic.message);
            error.help = report.help;
            error.site = export_sites
                .values()
                .find(|site| site.span == report.diagnostic.span)
                .cloned();
            error
        })?;
    candidate
        .import_object_consumptions(&program, &origins)
        .map_err(|report| {
            let mut error = ContextError::new(report.diagnostic.message);
            error.help = report.help;
            error
        })?;
    *session = candidate;
    Ok(ContextualizedModule {
        program,
        graph,
        source,
        sources,
        origins,
        obligations,
    })
}

/// Bind compiler-owned semantic IR without capturing or renaming live session
/// identities. Its candidate compiler session remains owned by the caller.
pub fn contextualize_source(
    unit: &crate::pipeline::RelocatableCompilation,
    context: &Context,
    planning: graph::PlanningContext,
) -> Result<ContextualizedModule, ContextError> {
    let mut context = context.clone();
    for (name, file) in &unit.module_bindings {
        context
            .modules
            .entry(name.clone())
            .or_insert_with(|| file.clone());
    }
    validate_context(&context)?;
    let compiled = &unit.compilation;
    let (program, mut obligations) = bind_program(
        &compiled.program,
        &unit.relocations,
        &unit.main_module,
        &context,
        &compiled.origins,
    )?;
    collect_obligations(&program.body, &mut obligations);
    let graph = graph::build_with_context(&program, &planning);
    Ok(ContextualizedModule {
        program,
        graph,
        source: compiled.source.clone(),
        sources: compiled.sources.clone(),
        origins: compiled.origins.clone(),
        obligations,
    })
}

fn validate_context(context: &Context) -> Result<(), ContextError> {
    if !context.pwd.is_absolute() {
        return Err(ContextError::new(
            "the evaluation working directory must be absolute",
        ));
    }
    Ok(())
}

fn bind_program(
    program: &hir::Program,
    relocations: &[Relocation],
    main_module: &str,
    context: &Context,
    origins: &Origins,
) -> Result<(hir::Program, Vec<Obligation>), ContextError> {
    let mut program = program.clone();
    let paths = instruction_paths(&program.body);
    let mut obligations = Vec::new();
    for relocation in relocations {
        let node = node_mut(
            &mut program.body,
            *paths
                .get(&relocation.instruction)
                .ok_or_else(|| ContextError::new("relocation instruction does not exist"))?,
        )?;
        bind_relocation(node, relocation, main_module, context, &mut obligations)
            .map_err(|error| error.at(relocation.instruction, origins))?;
    }
    bind_endpoints(&mut program.body, context, &mut obligations).map_err(|error| {
        if let Some(id) = error.instruction {
            error.at(id, origins)
        } else {
            error
        }
    })?;
    Ok((program, obligations))
}

fn bind_endpoints(
    region: &mut Region,
    context: &Context,
    obligations: &mut Vec<Obligation>,
) -> Result<(), ContextError> {
    for node in &mut region.instructions {
        let id = node.id;
        let result = match &mut node.kind {
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                if let Input::File { location, .. } = source {
                    bind_endpoint(location, context, id, obligations)?;
                }
                if let Output::File { location, .. } = destination {
                    bind_endpoint(location, context, id, obligations)?;
                }
                Ok(())
            }
            InstructionKind::Spawn {
                executable,
                arguments,
                ..
            } => {
                bind_endpoint(executable, context, id, obligations)?;
                for argument in arguments {
                    if let Argument::Path(location) = argument {
                        bind_endpoint(location, context, id, obligations)?;
                    }
                }
                Ok(())
            }
            _ => Ok(()),
        };
        result.map_err(|mut error: ContextError| {
            error.instruction.get_or_insert(id);
            error
        })?;
    }
    Ok(())
}

fn bind_endpoint(
    location: &mut Location,
    context: &Context,
    instruction: InstructionId,
    obligations: &mut Vec<Obligation>,
) -> Result<(), ContextError> {
    #[cfg(not(windows))]
    if location.backend == Backend::Local && windows_absolute(&location.path) {
        return Err(ContextError::new("a native Windows absolute path cannot be bound on this platform")
            .hint("Keep absolute literals for their native platform, or use a contextual $pwd/$env path or an explicit remote namespace.").required_by(instruction));
    }
    if !matches!(location.backend, Backend::Wsl | Backend::Ssh { .. })
        || location.path.starts_with('/')
        || location.path == "~"
        || location.path.starts_with("~/")
    {
        return Ok(());
    }
    if let Some(cwd) = context.endpoint_cwds.get(&location.backend) {
        if !cwd.starts_with('/') || cwd.contains('\0') {
            return Err(ContextError::new(
                "an endpoint working-directory binding must be an absolute guest path",
            )
            .required_by(instruction));
        }
        location.path = append(cwd, &location.path);
        return Ok(());
    }
    obligations.push(Obligation::InheritedEndpointCwd {
        instruction,
        backend: location.backend.clone(),
    });
    Ok(())
}

fn instruction_paths(region: &Region) -> BTreeMap<InstructionId, usize> {
    region
        .instructions
        .iter()
        .enumerate()
        .map(|(index, node)| (node.id, index))
        .collect()
}

fn node_mut(region: &mut Region, index: usize) -> Result<&mut Instruction, ContextError> {
    region
        .instructions
        .get_mut(index)
        .ok_or_else(|| ContextError::new("invalid instruction index"))
}

fn bind_relocation(
    node: &mut Instruction,
    relocation: &Relocation,
    main_module: &str,
    context: &Context,
    obligations: &mut Vec<Obligation>,
) -> Result<(), ContextError> {
    if relocation.slot == Slot::Cwd {
        let InstructionKind::Spawn { cwd, .. } = &mut node.kind else {
            return Err(ContextError::new("cwd relocation does not name a program"));
        };
        *cwd = PathBuf::from(
            bound_path(
                &relocation.path,
                &Backend::Local,
                main_module,
                context,
                Some(node.id),
                obligations,
            )?
            .path,
        );
        return Ok(());
    }
    let location = location_mut(node, relocation.slot)?;
    *location = bound_path(
        &relocation.path,
        &location.backend,
        main_module,
        context,
        Some(relocation.instruction),
        obligations,
    )?;
    Ok(())
}

fn location_mut(node: &mut Instruction, slot: Slot) -> Result<&mut Location, ContextError> {
    match (&mut node.kind, slot) {
        (
            InstructionKind::Transfer {
                source: Input::File { location, .. },
                ..
            },
            Slot::Source,
        )
        | (
            InstructionKind::Transfer {
                destination: Output::File { location, .. },
                ..
            },
            Slot::Destination,
        )
        | (
            InstructionKind::Spawn {
                executable: location,
                ..
            },
            Slot::Executable,
        ) => Ok(location),
        (InstructionKind::Spawn { arguments, .. }, Slot::Argument { index }) => {
            match arguments.get_mut(index) {
                Some(Argument::Path(location)) => Ok(location),
                _ => Err(ContextError::new(
                    "argument relocation does not name a path argument",
                )),
            }
        }
        _ => Err(ContextError::new(
            "relocation slot is incompatible with its operation",
        )),
    }
}

fn bound_path(
    path: &crate::relocatable::Path,
    backend: &Backend,
    main_module: &str,
    context: &Context,
    instruction: Option<InstructionId>,
    obligations: &mut Vec<Obligation>,
) -> Result<Location, ContextError> {
    let mut bound_backend = backend.clone();
    let base = match &path.anchor {
        Anchor::WorkingDirectory => utf8(&context.pwd, "working directory")?,
        Anchor::Home if matches!(backend, Backend::Wsl | Backend::Ssh { .. }) => {
            if let Some(instruction) = instruction {
                obligations.push(Obligation::ForeignHome {
                    instruction,
                    backend: backend.clone(),
                });
            }
            "~".into()
        }
        Anchor::Home => local_home(context)?,
        Anchor::ModuleFile { module: name } => {
            utf8(module_file(name, main_module, context)?, "module input")?
        }
        Anchor::ModuleDirectory { module: name } => {
            let file = module_file(name, main_module, context)?;
            utf8(
                file.parent()
                    .ok_or_else(|| ContextError::new("module input has no parent directory"))?,
                "module directory",
            )?
        }
        Anchor::Environment { name } => {
            bound_backend = Backend::Local;
            let value = context
                .environment
                .get(name)
                .cloned()
                .or_else(|| std::env::var(name).ok())
                .ok_or_else(|| {
                    ContextError::new(format!("unresolved environment symbol '{name}'")).hint(
                        "Define the variable in the evaluation environment before loading this module.",
                    )
                })?;
            anchor_local(&value, &context.pwd)?
        }
        Anchor::SearchPath { name } => {
            bound_backend = Backend::Path;
            if let Some(instruction) = instruction {
                obligations.push(Obligation::SearchPath {
                    instruction,
                    name: name.clone(),
                });
            }
            name.clone()
        }
    };
    let path = append(&base, &path.suffix);
    if path.contains('\0') {
        return Err(ContextError::new("a bound path contains NUL"));
    }
    Ok(Location {
        backend: bound_backend,
        path,
    })
}

fn module_file<'a>(
    name: &str,
    main_module: &str,
    context: &'a Context,
) -> Result<&'a Path, ContextError> {
    let file = if name == main_module {
        context.module_file.as_deref()
    } else {
        context.modules.get(name).map(PathBuf::as_path)
    };
    let file = file.ok_or_else(|| ContextError::new(format!("unresolved module-file symbol '{name}'")).hint("Bind this exact module identity in Context.modules; debug filenames are not filesystem bindings."))?;
    if !file.is_absolute() {
        return Err(ContextError::new(format!(
            "module-file symbol '{name}' is not bound to an absolute path"
        ))
        .hint("Anchor this module binding under the evaluation working directory explicitly."));
    }
    Ok(file)
}

fn local_home(context: &Context) -> Result<String, ContextError> {
    if let Some(home) = &context.home {
        return anchor_local(&utf8(home, "home directory")?, &context.pwd);
    }
    for name in ["HOME", "USERPROFILE"] {
        if let Some(value) = context.environment.get(name) {
            return anchor_local(value, &context.pwd);
        }
        if let Ok(value) = std::env::var(name) {
            return anchor_local(&value, &context.pwd);
        }
    }
    Err(ContextError::new("unresolved local home-directory symbol")
        .hint("Set the evaluation home directory or HOME/USERPROFILE."))
}

fn anchor_local(value: &str, pwd: &Path) -> Result<String, ContextError> {
    if value.is_empty() || value.contains('\0') {
        return Err(ContextError::new(
            "a evaluation environment path must be nonempty and NUL-free",
        ));
    }
    if Path::new(value).is_absolute() || windows_absolute(value) {
        return Ok(value.to_owned());
    }
    let bound = pwd.join(value);
    if !bound.is_absolute() {
        return Err(ContextError::new(
            "a evaluation environment path has an unresolved native drive context",
        ));
    }
    utf8(&bound, "environment path")
}

fn windows_absolute(value: &str) -> bool {
    value.starts_with("\\\\")
        || value.as_bytes().get(1) == Some(&b':')
            && value
                .as_bytes()
                .get(2)
                .is_some_and(|byte| matches!(byte, b'/' | b'\\'))
}

fn utf8(path: &Path, subject: &str) -> Result<String, ContextError> {
    path.to_str()
        .map(str::to_owned)
        .ok_or_else(|| ContextError::new(format!("the evaluation {subject} is not UTF-8")))
}

fn append(base: &str, suffix: &str) -> String {
    if suffix.is_empty() {
        return base.to_owned();
    }
    format!(
        "{}/{}",
        base.trim_end_matches(['/', '\\']),
        suffix.trim_start_matches(['/', '\\'])
    )
}

fn bind_exports(
    exports: &mut [Export],
    main_module: &str,
    context: &Context,
) -> Result<(), ContextError> {
    for export in exports {
        let ExportValue::Path {
            backend,
            path,
            relocation: Some(relocation),
            ..
        } = &mut export.value
        else {
            continue;
        };
        if backend.is_none() && relocation.anchor == Anchor::Home {
            *path = append("~", &relocation.suffix);
            continue;
        }
        let bound = bound_path(
            relocation,
            backend.as_ref().unwrap_or(&Backend::Local),
            main_module,
            context,
            None,
            &mut Vec::new(),
        )?;
        *backend = Some(bound.backend);
        *path = bound.path;
        if let ExportValue::Path { relocation, .. } = &mut export.value {
            *relocation = None;
        }
    }
    Ok(())
}

struct IdMaps {
    processes: BTreeMap<ProcessId, ProcessId>,
}

impl IdMaps {
    fn new(program: &hir::Program, base: usize) -> Result<Self, ContextError> {
        let processes = program
            .body
            .instructions
            .iter()
            .filter_map(|node| match node.kind {
                InstructionKind::Spawn { process, .. } => Some(process),
                _ => None,
            })
            .collect::<BTreeSet<_>>()
            .into_iter()
            .enumerate()
            .map(|(index, old)| {
                base.checked_add(index)
                    .map(|id| (old, ProcessId(id)))
                    .ok_or_else(|| ContextError::new("process identity space is exhausted"))
            })
            .collect::<Result<_, _>>()?;
        Ok(Self { processes })
    }

    fn process(&self, id: &mut ProcessId) -> Result<(), ContextError> {
        *id = *self
            .processes
            .get(id)
            .ok_or_else(|| ContextError::new("process reference has no local definition"))?;
        Ok(())
    }

    fn program(&self, program: &mut hir::Program) -> Result<(), ContextError> {
        self.region(&mut program.body)
    }

    fn region(&self, region: &mut Region) -> Result<(), ContextError> {
        for node in &mut region.instructions {
            match &mut node.kind {
                InstructionKind::Spawn { process, .. } => self.process(process)?,
                InstructionKind::Transfer {
                    source,
                    destination,
                } => {
                    if let Input::Port { process, .. } = source {
                        self.process(process)?;
                    }
                    if let Output::Stdin(process) = destination {
                        self.process(process)?;
                    }
                }
                InstructionKind::Await {
                    process: Some(process),
                } => self.process(process)?,
                InstructionKind::Barrier { .. } | InstructionKind::Await { process: None } => {}
            }
        }
        Ok(())
    }

    fn exports(&self, exports: &mut [Export]) -> Result<(), ContextError> {
        for export in exports {
            match &mut export.value {
                ExportValue::Process { process } | ExportValue::Port { process, .. } => {
                    self.process(process)?
                }
                _ => {}
            }
        }
        Ok(())
    }
}

fn collect_obligations(region: &Region, obligations: &mut Vec<Obligation>) {
    for node in &region.instructions {
        match &node.kind {
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                if let Input::File { location, .. } = source {
                    obligations.push(Obligation::OpenEndpoint {
                        instruction: node.id,
                        backend: location.backend.clone(),
                    });
                }
                if let Output::File { location, .. } = destination {
                    obligations.push(Obligation::OpenEndpoint {
                        instruction: node.id,
                        backend: location.backend.clone(),
                    });
                }
            }
            InstructionKind::Spawn {
                process,
                executable,
                ..
            } => {
                obligations.push(Obligation::ProgramLaunch {
                    instruction: node.id,
                    process: *process,
                    backend: executable.backend.clone(),
                });
            }
            _ => {}
        }
    }
}
