use std::collections::{BTreeSet, HashSet};
use std::path::PathBuf;

use super::*;
use crate::endpoint::Location;
use crate::hir::{Argument, Input, Instruction, InstructionKind, Output, ReadPort, Region};
use crate::language::DestinationMode;
use crate::relocatable::{ExportValue, Namespace, Slot};

const MAX_OPERATIONS: usize = 100_000;

pub(super) fn module(module: &ObjectModule) -> Result<(), ObjectError> {
    if module.format_version != FORMAT_VERSION || module.language_version != "0.5" {
        return Err(ObjectError::new(
            "unsupported object format or language version",
        ));
    }
    let mut supported = FEATURES
        .iter()
        .map(|name| (*name).to_owned())
        .collect::<BTreeSet<_>>();
    if !module.source_modules.is_empty() {
        supported.insert(SOURCE_MODULES_FEATURE.into());
    }
    if module.features.iter().cloned().collect::<BTreeSet<_>>() != supported
        || module.features.len() != supported.len()
    {
        return Err(ObjectError::new(
            "unsupported, missing or duplicate object feature",
        ));
    }
    text(&module.main_module, "module symbol")?;
    source_modules(module)?;
    let mut state = ProgramState::default();
    state.region(&module.program.body)?;
    let nodes = nodes(&module.program);
    let by_id = nodes
        .iter()
        .map(|node| (node.id, *node))
        .collect::<BTreeMap<_, _>>();
    let mut relocations = BTreeSet::new();
    for relocation in &module.relocations {
        if !relocations.insert((relocation.instruction, relocation.slot)) {
            return Err(ObjectError::new("duplicate object relocation"));
        }
        let node = by_id
            .get(&relocation.instruction)
            .ok_or_else(|| ObjectError::new("relocation references an undefined instruction"))?;
        let backend = slot_backend(node, relocation.slot)?;
        path_expression(&relocation.path)?;
        anchor_backend(&relocation.path.anchor, &backend)?;
        if !slot_path(node, relocation.slot)?.is_empty() {
            return Err(ObjectError::new(
                "relocated object operand contains a stale physical binding",
            ));
        }
    }
    for node in &nodes {
        operands(node, &relocations)?;
    }
    exports(module, &state)?;
    debug(module, &by_id)?;
    let tables = tables::derive(module)?;
    if symbol_set(&module.symbols)? != symbol_set(&tables.symbols)?
        || import_set(&module.imports)? != import_set(&tables.imports)?
        || !constraint_set(&module.constraints)?.is_subset(&constraint_set(&tables.constraints)?)
    {
        return Err(ObjectError::new(
            "object symbols, imports or constraints do not match its typed operations",
        ));
    }
    Ok(())
}

fn symbol_set(symbols: &[Symbol]) -> Result<BTreeMap<&str, &SymbolKind>, ObjectError> {
    let mut table = BTreeMap::new();
    for symbol in symbols {
        if table.insert(symbol.name.as_str(), &symbol.kind).is_some() {
            return Err(ObjectError::new("duplicate object symbol"));
        }
    }
    Ok(table)
}

type ImportSet<'a> = BTreeMap<&'a str, (&'a ImportKind, BTreeSet<InstructionId>)>;

fn import_set(imports: &[Import]) -> Result<ImportSet<'_>, ObjectError> {
    let mut table = BTreeMap::new();
    for import in imports {
        let users = import.users.iter().copied().collect::<BTreeSet<_>>();
        if users.len() != import.users.len()
            || table
                .insert(import.symbol.as_str(), (&import.requirement, users))
                .is_some()
        {
            return Err(ObjectError::new("duplicate object import or import user"));
        }
    }
    Ok(table)
}

fn constraint_set(
    constraints: &[Constraint],
) -> Result<BTreeSet<(InstructionId, InstructionId)>, ObjectError> {
    let mut pairs = BTreeSet::new();
    for constraint in constraints {
        let Constraint::GuardedOverwrite { first, last } = constraint;
        if !pairs.insert((*first, *last)) {
            return Err(ObjectError::new("duplicate object optimization constraint"));
        }
    }
    Ok(pairs)
}

#[derive(Default)]
struct ProgramState {
    instructions: BTreeSet<InstructionId>,
    processes: BTreeSet<ProcessId>,
    consumed: HashSet<(ProcessId, u8)>,
    patterns: HashSet<String>,
}

impl ProgramState {
    fn region(&mut self, region: &Region) -> Result<(), ObjectError> {
        for node in &region.instructions {
            if self.instructions.len() >= MAX_OPERATIONS
                || node.id.0 > u32::MAX as usize
                || !self.instructions.insert(node.id)
            {
                return Err(ObjectError::new(
                    "object operation limit or unique instruction identity is invalid",
                ));
            }
            self.instruction(node)?;
        }
        Ok(())
    }

    fn instruction(&mut self, node: &Instruction) -> Result<(), ObjectError> {
        match &node.kind {
            InstructionKind::Spawn {
                process,
                executable,
                arguments,
                ..
            } => {
                if process.0 > u32::MAX as usize || !self.processes.insert(*process) {
                    return Err(ObjectError::new(
                        "duplicate or out-of-range object process identity",
                    ));
                }
                if !matches!(
                    executable.backend,
                    Backend::Local
                        | Backend::Path
                        | Backend::Env
                        | Backend::Wsl
                        | Backend::Ssh { .. }
                ) {
                    return Err(ObjectError::new(
                        "object program executable has an invalid endpoint type",
                    ));
                }
                for argument in arguments {
                    if let Argument::Text(value) = argument {
                        no_nul(value, "program argument")?;
                    }
                }
            }
            InstructionKind::Transfer {
                source,
                destination,
            } => self.transfer(source, destination)?,
            InstructionKind::Barrier { .. } | InstructionKind::Await { process: None } => {}
            InstructionKind::Await {
                process: Some(process),
            } => self.process(*process)?,
        }
        Ok(())
    }

    fn process(&self, process: ProcessId) -> Result<(), ObjectError> {
        if !self.processes.contains(&process) {
            return Err(ObjectError::new(
                "object port references a process without a preceding local launch",
            ));
        }
        Ok(())
    }

    fn consume(&mut self, process: ProcessId, port: u8) -> Result<(), ObjectError> {
        self.process(process)?;
        if !self.consumed.insert((process, port)) {
            return Err(ObjectError::new(
                "object streaming port has more than one consumer or producer",
            ));
        }
        Ok(())
    }

    fn transfer(&mut self, source: &Input, destination: &Output) -> Result<(), ObjectError> {
        match source {
            Input::Port {
                process,
                port: ReadPort::Stdout,
            } => self.consume(*process, 1)?,
            Input::Port {
                process,
                port: ReadPort::Stderr,
            } => self.consume(*process, 2)?,
            Input::Port { process, .. } => self.process(*process)?,
            Input::File {
                pattern: Some(pattern),
                ..
            } => {
                if self.patterns.insert(pattern.clone()) {
                    regex::Regex::new(pattern).map_err(|cause| {
                        ObjectError::new(format!("invalid object selector: {cause}"))
                    })?;
                }
                if !matches!(
                    destination,
                    Output::File {
                        mode: DestinationMode::Container,
                        ..
                    }
                ) {
                    return Err(ObjectError::new(
                        "object selector requires a container destination",
                    ));
                }
            }
            _ => {}
        }
        match destination {
            Output::Stdin(process) => self.consume(*process, 0)?,
            Output::File {
                location:
                    Location {
                        backend: Backend::Http { .. } | Backend::Path,
                        ..
                    },
                ..
            } => {
                return Err(ObjectError::new(
                    "object destination is a readonly namespace",
                ));
            }
            _ => {}
        }
        Ok(())
    }
}

fn exports(module: &ObjectModule, state: &ProgramState) -> Result<(), ObjectError> {
    let mut keys = BTreeSet::new();
    for export in &module.exports {
        text(&export.name, "export name")?;
        if !keys.insert(tables::export_key(export)) {
            return Err(ObjectError::new("duplicate object export"));
        }
        match &export.value {
            ExportValue::Process { process } | ExportValue::Port { process, .. } => {
                if !state.processes.contains(process) {
                    return Err(ObjectError::new(
                        "object export captures a foreign live process",
                    ));
                }
            }
            ExportValue::Path {
                backend,
                path,
                relocation,
                ..
            } => {
                no_nul(path, "exported path")?;
                if let Some(expression) = relocation {
                    if !path.is_empty() {
                        return Err(ObjectError::new(
                            "relocated export contains a stale physical binding",
                        ));
                    }
                    path_expression(expression)?;
                    anchor_backend(
                        &expression.anchor,
                        backend.as_ref().unwrap_or(&Backend::Local),
                    )?;
                } else if path.is_empty() {
                    return Err(ObjectError::new("exported path has no value or relocation"));
                }
                if let Some(backend) = backend {
                    validate_backend(backend)?;
                }
            }
            ExportValue::Namespace {
                namespace: Namespace::Endpoint { backend },
            } => validate_backend(backend)?,
            _ => {}
        }
    }
    Ok(())
}

fn debug(
    module: &ObjectModule,
    nodes: &BTreeMap<InstructionId, &Instruction>,
) -> Result<(), ObjectError> {
    module.debug.validate()?;
    let origins = module
        .debug
        .origins
        .iter()
        .map(|origin| (origin.instruction, origin))
        .collect::<BTreeMap<_, _>>();
    if origins.keys().copied().collect::<BTreeSet<_>>() != nodes.keys().copied().collect() {
        return Err(ObjectError::new(
            "object source origins do not cover exactly its operations",
        ));
    }
    for (id, node) in nodes {
        let origin = origins[id];
        if node.span != origin.site.span
            || node.expansion
                != origin
                    .expansion
                    .iter()
                    .map(|site| site.span)
                    .collect::<Vec<_>>()
        {
            return Err(ObjectError::new(
                "object instruction spans disagree with their owned source origins",
            ));
        }
    }
    let names = module
        .exports
        .iter()
        .map(|export| export.name.as_str())
        .collect::<BTreeSet<_>>();
    if module
        .debug
        .export_sites
        .iter()
        .any(|site| !names.contains(site.name.as_str()))
    {
        return Err(ObjectError::new(
            "object export debug site references an undefined symbol",
        ));
    }
    Ok(())
}

pub(super) fn slot_backend(node: &Instruction, slot: Slot) -> Result<Backend, ObjectError> {
    match slot {
        Slot::Cwd if matches!(node.kind, InstructionKind::Spawn { .. }) => Ok(Backend::Local),
        _ => slot_location(node, slot).map(|location| location.backend.clone()),
    }
}

fn slot_path(node: &Instruction, slot: Slot) -> Result<&str, ObjectError> {
    match (&node.kind, slot) {
        (InstructionKind::Spawn { cwd, .. }, Slot::Cwd) => cwd
            .to_str()
            .ok_or_else(|| ObjectError::new("object working directory is not UTF-8")),
        _ => Ok(&slot_location(node, slot)?.path),
    }
}

fn slot_location(node: &Instruction, slot: Slot) -> Result<&Location, ObjectError> {
    match (&node.kind, slot) {
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
            match arguments.get(index) {
                Some(Argument::Path(location)) => Ok(location),
                _ => Err(ObjectError::new(
                    "object argument relocation does not select a path argument",
                )),
            }
        }
        _ => Err(ObjectError::new(
            "object relocation selects an incompatible instruction slot",
        )),
    }
}

fn operands(
    node: &Instruction,
    relocations: &BTreeSet<(InstructionId, Slot)>,
) -> Result<(), ObjectError> {
    match &node.kind {
        InstructionKind::Transfer {
            source,
            destination,
        } => {
            if let Input::File { location, .. } = source {
                operand(node.id, Slot::Source, location, relocations)?;
            }
            if let Output::File { location, .. } = destination {
                operand(node.id, Slot::Destination, location, relocations)?;
            }
        }
        InstructionKind::Spawn {
            executable,
            arguments,
            cwd,
            ..
        } => {
            operand(node.id, Slot::Executable, executable, relocations)?;
            for (index, argument) in arguments.iter().enumerate() {
                if let Argument::Path(location) = argument {
                    operand(node.id, Slot::Argument { index }, location, relocations)?;
                }
            }
            let cwd = Location {
                backend: Backend::Local,
                path: cwd
                    .to_str()
                    .ok_or_else(|| ObjectError::new("object cwd is not UTF-8"))?
                    .to_owned(),
            };
            operand(node.id, Slot::Cwd, &cwd, relocations)?;
        }
        _ => {}
    }
    Ok(())
}

fn operand(
    id: InstructionId,
    slot: Slot,
    location: &Location,
    relocations: &BTreeSet<(InstructionId, Slot)>,
) -> Result<(), ObjectError> {
    validate_backend(&location.backend)?;
    no_nul(&location.path, "operand path")?;
    if location.path.is_empty() {
        if matches!(location.backend, Backend::Tcp { .. } | Backend::Http { .. })
            && !relocations.contains(&(id, slot))
        {
            return Ok(());
        }
        if !relocations.contains(&(id, slot)) {
            return Err(ObjectError::new(
                "object operand is unbound without a relocation",
            ));
        }
        return Ok(());
    }
    if matches!(location.backend, Backend::Local) && !absolute(&location.path)
        || matches!(location.backend, Backend::Env | Backend::Path)
        || matches!(location.backend, Backend::Local)
            && (location.path == "~" || location.path.starts_with("~/"))
    {
        return Err(ObjectError::new(
            "context-dependent object operand has no symbolic relocation",
        ));
    }
    if matches!(location.backend, Backend::Tcp { .. }) && location.path != "/" {
        return Err(ObjectError::new("object TCP endpoint has an invalid path"));
    }
    Ok(())
}

fn path_expression(path: &crate::relocatable::Path) -> Result<(), ObjectError> {
    no_nul(&path.suffix, "relocation suffix")?;
    if absolute(&path.suffix) || path.suffix == "~" || path.suffix.starts_with("~/") {
        return Err(ObjectError::new(
            "object relocation suffix replaces its anchor",
        ));
    }
    match &path.anchor {
        Anchor::ModuleFile { module } | Anchor::ModuleDirectory { module } => {
            text(module, "module import")?
        }
        Anchor::Environment { name } | Anchor::SearchPath { name } => {
            text(name, "lookup import")?;
            if name.contains(['/', '\\']) {
                return Err(ObjectError::new(
                    "object lookup symbol contains path separators",
                ));
            }
        }
        _ => {}
    }
    if matches!(path.anchor, Anchor::ModuleFile { .. }) && !path.suffix.is_empty() {
        return Err(ObjectError::new(
            "object module-file symbol cannot have child components",
        ));
    }
    Ok(())
}

fn anchor_backend(anchor: &Anchor, backend: &Backend) -> Result<(), ObjectError> {
    let compatible = match anchor {
        Anchor::WorkingDirectory | Anchor::ModuleFile { .. } | Anchor::ModuleDirectory { .. } => {
            *backend == Backend::Local
        }
        Anchor::Environment { .. } => matches!(backend, Backend::Env | Backend::Local),
        Anchor::SearchPath { .. } => matches!(backend, Backend::Path | Backend::Local),
        Anchor::Home => !matches!(backend, Backend::Tcp { .. }),
    };
    if !compatible {
        return Err(ObjectError::new(
            "object relocation anchor conflicts with its endpoint",
        ));
    }
    Ok(())
}

fn validate_backend(backend: &Backend) -> Result<(), ObjectError> {
    match backend {
        Backend::Ssh { host, user, .. } => {
            host_name(host)?;
            if let Some(user) = user {
                text(user, "SSH user")?;
                if user.contains(['@', '/', '\\', ':']) || user.chars().any(char::is_whitespace) {
                    return Err(ObjectError::new("invalid object SSH user"));
                }
            }
        }
        Backend::Tcp { host, .. } => host_name(host)?,
        Backend::Http { base } => {
            let url = url::Url::parse(base)
                .map_err(|_| ObjectError::new("invalid object HTTP base URL"))?;
            if !matches!(url.scheme(), "http" | "https")
                || url.host_str().is_none()
                || !url.username().is_empty()
                || url.password().is_some()
            {
                return Err(ObjectError::new("invalid object HTTP endpoint"));
            }
        }
        _ => {}
    }
    Ok(())
}

fn host_name(host: &str) -> Result<(), ObjectError> {
    text(host, "endpoint host")?;
    if host.starts_with('-')
        || host.contains(['/', '\\', '@', '#', '?'])
        || host.chars().any(char::is_whitespace)
    {
        return Err(ObjectError::new("invalid object endpoint host"));
    }
    Ok(())
}

fn no_nul(value: &str, subject: &str) -> Result<(), ObjectError> {
    if value.contains('\0') {
        return Err(ObjectError::new(format!("object {subject} contains NUL")));
    }
    Ok(())
}

fn text(value: &str, subject: &str) -> Result<(), ObjectError> {
    no_nul(value, subject)?;
    if value.is_empty() {
        return Err(ObjectError::new(format!("object {subject} is empty")));
    }
    Ok(())
}

fn absolute(path: &str) -> bool {
    path.starts_with(['/', '\\'])
        || (path.as_bytes().get(1) == Some(&b':')
            && path
                .as_bytes()
                .get(2)
                .is_some_and(|byte| matches!(byte, b'/' | b'\\')))
}

pub(super) fn clear_relocated_fields(
    program: &mut hir::Program,
    relocations: &[Relocation],
) -> Result<(), ObjectError> {
    let mut pending = relocations
        .iter()
        .map(|relocation| (relocation.instruction, relocation.slot))
        .collect::<BTreeSet<_>>();
    if pending.len() != relocations.len() {
        return Err(ObjectError::new("duplicate object relocation"));
    }
    clear_region(&mut program.body, &mut pending);
    if !pending.is_empty() {
        return Err(ObjectError::new(
            "object relocation has no matching path slot",
        ));
    }
    Ok(())
}

fn clear_region(region: &mut Region, pending: &mut BTreeSet<(InstructionId, Slot)>) {
    for node in &mut region.instructions {
        match &mut node.kind {
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                if let Input::File { location, .. } = source {
                    clear(node.id, Slot::Source, location, pending);
                }
                if let Output::File { location, .. } = destination {
                    clear(node.id, Slot::Destination, location, pending);
                }
            }
            InstructionKind::Spawn {
                executable,
                arguments,
                cwd,
                ..
            } => {
                clear(node.id, Slot::Executable, executable, pending);
                for (index, argument) in arguments.iter_mut().enumerate() {
                    if let Argument::Path(location) = argument {
                        clear(node.id, Slot::Argument { index }, location, pending);
                    }
                }
                if pending.remove(&(node.id, Slot::Cwd)) {
                    *cwd = PathBuf::new();
                }
            }
            _ => {}
        }
    }
}

fn clear(
    id: InstructionId,
    slot: Slot,
    location: &mut Location,
    pending: &mut BTreeSet<(InstructionId, Slot)>,
) {
    if pending.remove(&(id, slot)) {
        location.path.clear();
    }
}

fn source_modules(module: &ObjectModule) -> Result<(), ObjectError> {
    if module.source_modules.len() > 1024 {
        return Err(ObjectError::new("too many source module bindings"));
    }
    let mut names = BTreeSet::new();
    for binding in &module.source_modules {
        text(&binding.name, "source module symbol")?;
        if binding.name == module.main_module || !names.insert(binding.name.clone()) {
            return Err(ObjectError::new("duplicate or main source module binding"));
        }
        match &binding.path {
            SourceModulePath::Absolute { path } => {
                no_nul(path, "source module path")?;
                if !absolute(path) {
                    return Err(ObjectError::new(
                        "captured source module path must be absolute",
                    ));
                }
            }
            SourceModulePath::Contextual { path } => {
                path_expression(path)?;
                if matches!(path.anchor, Anchor::SearchPath { .. }) {
                    return Err(ObjectError::new(
                        "source module bindings cannot require a filesystem PATH search",
                    ));
                }
            }
        }
    }
    let mut ready = BTreeSet::from([module.main_module.clone()]);
    for _ in 0..=module.source_modules.len() {
        let before = ready.len();
        for binding in &module.source_modules {
            let parent = match &binding.path {
                SourceModulePath::Contextual {
                    path:
                        crate::relocatable::Path {
                            anchor:
                                Anchor::ModuleFile { module } | Anchor::ModuleDirectory { module },
                            ..
                        },
                } => Some(module),
                _ => None,
            };
            if parent.is_none_or(|parent| ready.contains(parent)) {
                ready.insert(binding.name.clone());
            }
        }
        if ready.len() == module.source_modules.len() + 1 {
            return Ok(());
        }
        if ready.len() == before {
            return Err(ObjectError::new(
                "cyclic or unresolved source module binding",
            ));
        }
    }
    Err(ObjectError::new("source module dependency bound exceeded"))
}
