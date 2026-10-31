//! Versioned, untrusted relocatable modules. No host observations are encoded.

pub(crate) mod control;
mod debug;
mod tables;
mod validate;

use std::collections::BTreeMap;
use std::fmt;
use std::sync::Arc;

use serde::{Deserialize, Serialize};

use crate::endpoint::Backend;
use crate::hir::{self, InstructionId, ProcessId};
use crate::relocatable::{Anchor, Export, Relocation};
use crate::source::{Origins, Source, SourceMap, SourceSpan};

pub use debug::{DebugInfo, DebugOrigin, DebugSite, DebugSource, ExportSite};

pub const MAGIC: &[u8] = b"MSYNC-OBJECT\n";
pub const FORMAT_VERSION: u32 = 2;
pub const MAX_OBJECT_BYTES: usize = 64 * 1024 * 1024;
pub const CONTROL_FEATURE: &str = "typed_control_v1";
pub const SOURCE_MODULES_FEATURE: &str = "source_modules_v1";

pub const FEATURES: &[&str] = &[
    "barrier_typed_ir_v2",
    "context_relocations_v1",
    "guarded_overwrites_v1",
    "owned_source_map_v1",
    "runtime_exports_v1",
];

/// Detection is content-based; a damaged payload with a valid header stays an
/// object error rather than being retried through the source parser.
pub fn is_object(bytes: &[u8]) -> bool {
    bytes.starts_with(MAGIC)
        || (!bytes.is_empty() && MAGIC.starts_with(bytes))
        || bytes.starts_with(b"MSYNC-OBJECT\r\n")
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SourceModule {
    pub name: String,
    pub path: SourceModulePath,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum SourceModulePath {
    Absolute { path: String },
    Contextual { path: crate::relocatable::Path },
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ObjectModule {
    pub format_version: u32,
    pub language_version: String,
    pub features: Vec<String>,
    /// An opaque source-module symbol, never an implicit host path binding.
    pub main_module: String,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub source_modules: Vec<SourceModule>,
    pub program: hir::Program,
    pub relocations: Vec<Relocation>,
    pub symbols: Vec<Symbol>,
    pub imports: Vec<Import>,
    pub exports: Vec<Export>,
    pub constraints: Vec<Constraint>,
    pub debug: DebugInfo,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Symbol {
    pub name: String,
    pub kind: SymbolKind,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum SymbolKind {
    Process { process: ProcessId },
    Context { anchor: Anchor, backend: Backend },
    Capability { name: String },
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Import {
    pub symbol: String,
    pub requirement: ImportKind,
    pub users: Vec<InstructionId>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ImportKind {
    Context { anchor: Anchor, backend: Backend },
    Endpoint { backend: Backend },
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum Constraint {
    GuardedOverwrite {
        first: InstructionId,
        last: InstructionId,
    },
}

#[derive(Debug)]
pub struct ObjectError {
    pub message: String,
    pub help: String,
    cause: Option<serde_json::Error>,
}

impl ObjectError {
    pub(crate) fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            help: "Recompile the source with a compatible msync compiler; no module effects have started.".into(),
            cause: None,
        }
    }

    fn json(action: &str, cause: serde_json::Error) -> Self {
        let mut error = Self::new(format!("cannot {action} object module: {cause}"));
        error.cause = Some(cause);
        error
    }
}

impl fmt::Display for ObjectError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for ObjectError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        self.cause
            .as_ref()
            .map(|cause| cause as &dyn std::error::Error)
    }
}

impl ObjectModule {
    #[allow(clippy::too_many_arguments)]
    pub fn from_parts(
        mut program: hir::Program,
        relocations: Vec<Relocation>,
        mut exports: Vec<Export>,
        primary: Arc<Source>,
        sources: SourceMap,
        origins: Origins,
        export_sites: BTreeMap<String, SourceSpan>,
        main_module: String,
    ) -> Result<Self, ObjectError> {
        validate::clear_relocated_fields(&mut program, &relocations)?;
        control::visit_region(&mut program.body, &mut control::ClearInlinePaths)?;
        for export in &mut exports {
            match &mut export.value {
                crate::relocatable::ExportValue::Dynamic { expression } => {
                    control::visit_expr(expression, &mut control::ClearInlinePaths)?
                }
                crate::relocatable::ExportValue::Range { range } => {
                    control::visit_range(range, &mut control::ClearInlinePaths)?
                }
                _ => {}
            }
        }
        for export in &mut exports {
            if let crate::relocatable::ExportValue::Path {
                path,
                relocation: Some(_),
                ..
            } = &mut export.value
            {
                path.clear();
            }
        }
        let export_names = exports
            .iter()
            .map(|export| export.name.as_str())
            .collect::<std::collections::BTreeSet<_>>();
        let export_sites = export_sites
            .into_iter()
            .filter(|(name, _)| export_names.contains(name.as_str()))
            .collect();
        let debug = DebugInfo::from_parts(primary, sources, origins, export_sites);
        let mut module = Self {
            format_version: FORMAT_VERSION,
            language_version: "0.5".into(),
            features: FEATURES.iter().map(|name| (*name).into()).collect(),
            main_module,
            source_modules: Vec::new(),
            program,
            relocations,
            symbols: Vec::new(),
            imports: Vec::new(),
            exports,
            constraints: Vec::new(),
            debug,
        };
        if nodes(&module.program)
            .iter()
            .any(|node| matches!(node.kind, hir::InstructionKind::Control(_)))
            || module.exports.iter().any(|export| {
                matches!(
                    export.value,
                    crate::relocatable::ExportValue::Bool { .. }
                        | crate::relocatable::ExportValue::Dynamic { .. }
                        | crate::relocatable::ExportValue::Range { .. }
                )
            })
        {
            module.language_version = "0.6".into();
            module.features.push(CONTROL_FEATURE.into());
        }
        module
            .relocations
            .sort_by_key(|relocation| (relocation.instruction, relocation.slot));
        module.exports.sort_by_key(tables::export_key);
        let tables = tables::derive(&module)?;
        module.symbols = tables.symbols;
        module.imports = tables.imports;
        module.constraints = tables.constraints;
        module.validate()?;
        Ok(module)
    }

    pub fn with_source_modules(mut self, modules: Vec<SourceModule>) -> Result<Self, ObjectError> {
        if modules.len() > 1024 {
            return Err(ObjectError::new("too many source module bindings"));
        }
        let mut needed = std::collections::BTreeSet::new();
        for relocation in &self.relocations {
            source_module_dependency(&relocation.path, &mut needed);
        }
        let mut paths = control::InlinePaths { paths: Vec::new() };
        control::read_region(&self.program.body, &mut paths)?;
        for export in &self.exports {
            match &export.value {
                crate::relocatable::ExportValue::Dynamic { expression } => {
                    control::read_expr(expression, &mut paths)?
                }
                crate::relocatable::ExportValue::Range { range } => {
                    control::read_range(range, &mut paths)?
                }
                _ => {}
            }
            if let crate::relocatable::ExportValue::Path {
                relocation: Some(path),
                ..
            } = &export.value
            {
                source_module_dependency(path, &mut needed);
            }
        }
        for (_, path) in paths.paths {
            if let Some(path) = path {
                source_module_dependency(path, &mut needed);
            }
        }
        let mut changed = true;
        while changed {
            let before = needed.len();
            for module in &modules {
                if needed.contains(&module.name)
                    && let SourceModulePath::Contextual { path } = &module.path
                {
                    source_module_dependency(path, &mut needed);
                }
            }
            changed = before != needed.len();
        }
        self.source_modules = modules
            .into_iter()
            .filter(|module| needed.contains(&module.name) && module.name != self.main_module)
            .collect();
        self.features
            .retain(|feature| feature != SOURCE_MODULES_FEATURE);
        if !self.source_modules.is_empty() {
            self.features.push(SOURCE_MODULES_FEATURE.into());
        }
        self.validate()?;
        Ok(self)
    }

    pub fn validate(&self) -> Result<(), ObjectError> {
        validate::module(self)
    }

    pub fn encode(&self) -> Result<Vec<u8>, ObjectError> {
        self.validate()?;
        let payload =
            serde_json::to_vec_pretty(self).map_err(|cause| ObjectError::json("encode", cause))?;
        if payload.len() > MAX_OBJECT_BYTES.saturating_sub(MAGIC.len() + 1) {
            return Err(ObjectError::new(
                "object module exceeds the 64MiB format bound",
            ));
        }
        let mut bytes = Vec::with_capacity(MAGIC.len() + payload.len() + 1);
        bytes.extend_from_slice(MAGIC);
        bytes.extend_from_slice(&payload);
        bytes.push(b'\n');
        Ok(bytes)
    }

    pub fn decode(bytes: &[u8]) -> Result<Self, ObjectError> {
        if bytes.len() > MAX_OBJECT_BYTES {
            return Err(ObjectError::new(
                "object module exceeds the 64MiB format bound",
            ));
        }
        let payload = bytes
            .strip_prefix(MAGIC)
            .ok_or_else(|| ObjectError::new("invalid or truncated msync object header"))?;
        let module: Self =
            serde_json::from_slice(payload).map_err(|cause| ObjectError::json("decode", cause))?;
        module.validate()?;
        Ok(module)
    }
}

pub(crate) fn nodes(program: &hir::Program) -> Vec<&hir::Instruction> {
    let mut nodes = Vec::new();
    control::region_nodes(&program.body, &mut nodes);
    nodes
}

fn source_module_dependency(
    path: &crate::relocatable::Path,
    names: &mut std::collections::BTreeSet<String>,
) {
    if let Anchor::ModuleFile { module } | Anchor::ModuleDirectory { module } = &path.anchor {
        names.insert(module.clone());
    }
}
