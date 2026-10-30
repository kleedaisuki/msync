//! Atomic structured frontend submission: syntax, expansion and effect obligations.
//!
//! This is deliberately separate from runtime preparation: building a compilation
//! never inspects a file or launches a subprocess. Execution adapters must refine
//! unknown file kinds and process effects before applying stronger rewrites.

use crate::compiler::CompileContext;
use crate::compiler::structured::Session;
use crate::hir::graph::{self, PlanGraph, PlanningContext};
use crate::hir::{self, InstructionId, ProcessId};
use crate::language::{self, Document};
use crate::source::{LocatedReport, Origins, Source, SourceMap};
use crate::syntax::{Report, Span};
use std::sync::Arc;

/// In-memory relocatable compilation. Physical-looking compatibility operands
/// are not authoritative until their explicit contextual relocations are bound.
#[derive(Clone, Debug)]
pub struct RelocatableCompilation {
    pub compilation: Compilation,
    pub relocations: Vec<crate::relocatable::Relocation>,
    pub exports: Vec<crate::relocatable::Export>,
    pub export_sites: std::collections::BTreeMap<String, crate::source::SourceSpan>,
    pub main_module: String,
    pub module_bindings: std::collections::BTreeMap<String, std::path::PathBuf>,
    pub source_modules: Vec<crate::object::SourceModule>,
}

impl RelocatableCompilation {
    pub fn into_object(self) -> Result<crate::object::ObjectModule, crate::object::ObjectError> {
        crate::object::ObjectModule::from_parts(
            self.compilation.program,
            self.relocations,
            self.exports,
            self.compilation.source,
            self.compilation.sources,
            self.compilation.origins,
            self.export_sites,
            self.main_module,
        )?
        .with_source_modules(self.source_modules)
    }
}

#[derive(Clone, Debug)]
pub struct Compilation {
    pub document: Document,
    pub program: hir::Program,
    pub graph: PlanGraph,
    pub planning: PlanningContext,
    pub source: Arc<Source>,
    pub sources: SourceMap,
    pub origins: Origins,
}

impl Compilation {
    pub fn locate(&self, report: Report) -> LocatedReport {
        LocatedReport::new(self.source.clone(), report)
    }

    pub fn instruction_report(
        &self,
        id: InstructionId,
        report: Report,
        destination: Option<bool>,
    ) -> LocatedReport {
        let Some(origin) = self.origins.get(&id) else {
            return self.locate(report);
        };
        let site = destination
            .and_then(|destination| {
                origin.operands.as_ref().map(|operands| {
                    if destination {
                        &operands.1
                    } else {
                        &operands.0
                    }
                })
            })
            .unwrap_or(&origin.site);
        let mut report = report;
        report.diagnostic.span = site.span;
        let labels = std::mem::take(&mut report.labels);
        let mut located = LocatedReport::new(site.source.clone(), report);
        for label in labels {
            located = located.label(self.source.at(label.span), label.message);
        }
        if origin.definition != *site {
            located = located.label(
                origin.definition.clone(),
                "expanded operation is defined here",
            );
        }
        located
    }
    /// Locate a preparation failure by instruction and semantic operand, not wording.
    pub fn preparation_report(&self, diagnostic: crate::preparation::Diagnostic) -> LocatedReport {
        let Some(instruction) = diagnostic.instruction else {
            return self.locate(*diagnostic.report);
        };
        let destination = match diagnostic.operand {
            crate::preparation::Operand::Source => Some(false),
            crate::preparation::Operand::Destination => Some(true),
            crate::preparation::Operand::Operation => None,
        };
        self.instruction_report(instruction, *diagnostic.report, destination)
    }

    pub fn verify(&self) -> Result<(), Vec<graph::Issue>> {
        self.graph
            .verify_with_context(&self.program, &self.planning)
    }
}

/// Compile one submission, committing lexical state only when every pass succeeds.
pub fn compile(
    session: &mut Session,
    source: &str,
    context: &CompileContext,
    live_processes: &[ProcessId],
) -> Result<Compilation, Vec<Report>> {
    compile_with_context(
        session,
        source,
        context,
        &PlanningContext {
            live_processes: live_processes.to_vec(),
            ..PlanningContext::default()
        },
    )
}

/// Supply premises established by an execution adapter's verified resource view.
/// This function does not establish or discover sandbox capabilities itself.
pub fn compile_with_context(
    session: &mut Session,
    source: &str,
    context: &CompileContext,
    planning: &PlanningContext,
) -> Result<Compilation, Vec<Report>> {
    let name = context
        .source_file
        .as_ref()
        .map(|path| path.to_string_lossy().into_owned())
        .unwrap_or_else(|| "<source>".into());
    compile_named(session, source, &name, context, planning).map_err(|reports| {
        reports
            .into_iter()
            .map(LocatedReport::into_report)
            .collect()
    })
}

/// Compile against owned named text. Macro definitions retain this source across
/// later submissions; no file is reopened to render a foreign definition site.
pub fn compile_named(
    session: &mut Session,
    source: &str,
    name: &str,
    context: &CompileContext,
    planning: &PlanningContext,
) -> Result<Compilation, Vec<LocatedReport>> {
    compile_relocatable_named(session, source, name, context, planning)
        .map(|compiled| compiled.compilation)
}

/// Compile one source unit while retaining the contextual path expressions that
/// an interpreter can contextualize without filesystem observations or process capabilities.
pub fn compile_relocatable_named(
    session: &mut Session,
    source: &str,
    name: &str,
    context: &CompileContext,
    planning: &PlanningContext,
) -> Result<RelocatableCompilation, Vec<LocatedReport>> {
    let source = Source::new(name, source);
    let document = {
        let mut trace = crate::telemetry::span("parse");
        trace.attribute("source.path", name);
        trace.metric("bytes", source.text.len() as u64);
        let result = language::parse(&source.text).map_err(|reports| {
            reports
                .into_iter()
                .map(|report| LocatedReport::new(source.clone(), report))
                .collect::<Vec<_>>()
        });
        trace.finish_result(&result);
        trace.metric(
            "errors",
            result.as_ref().err().map_or(0, |errors| errors.len()) as u64,
        );
        result?
    };
    let mut trace = crate::telemetry::span("compile");
    trace.attribute("source.path", name);
    let result = compile_document_relocatable(session, document, source, context, planning);
    trace.finish_result(&result);
    trace.metric(
        "errors",
        result.as_ref().err().map_or(0, |errors| errors.len()) as u64,
    );
    if let Ok(compilation) = &result {
        trace.metric("operations", compilation.compilation.origins.len() as u64);
    }
    result
}

/// Emit a closed executable object; macro templates are compile-time definitions,
/// not an embedded source program to be reparsed by the object loader.
pub fn compile_object_named(
    session: &mut Session,
    source: &str,
    name: &str,
    context: &CompileContext,
) -> Result<crate::object::ObjectModule, Vec<LocatedReport>> {
    let mut candidate = session.clone();
    let compilation = compile_relocatable_named(
        &mut candidate,
        source,
        name,
        context,
        &PlanningContext::default(),
    )?;
    let debug = compilation.compilation.source.clone();
    let module = compilation.into_object().map_err(|error| {
        vec![LocatedReport::new(
            debug,
            Report::new(error.message, Span::default()).help(error.help),
        )]
    })?;
    *session = candidate;
    Ok(module)
}

fn compile_document_relocatable(
    session: &mut Session,
    document: Document,
    source: Arc<Source>,
    context: &CompileContext,
    planning: &PlanningContext,
) -> Result<RelocatableCompilation, Vec<LocatedReport>> {
    let mut candidate = session.clone();
    let lowered = candidate.compile_named(&document, context, source.clone())?;
    let program = lowered.program;
    let graph = graph::build_with_context(&program, planning);
    if !graph.issues.is_empty() {
        return Err(graph
            .issues
            .iter()
            .map(|issue| {
                let mut sites = issue.instructions.iter().filter_map(|id| lowered.origins.get(id));
                let mut report = Report::new(&issue.message, Span::default())
                    .help("Connect each live program stream only once, and acquire the program after arranging the flows needed for it to finish.");
                let mut located = if let Some(origin) = sites.next() {
                    report.diagnostic.span = origin.site.span;
                    origin.report(report, None)
                } else { LocatedReport::new(source.clone(), report) };
                for origin in sites {
                    located = located.label(origin.site.clone(), "this operation participates in the conflict");
                }
                located
            })
            .collect());
    }
    let exports = candidate.object_exports(&program);
    let export_sites = candidate.object_export_sites();
    *session = candidate;
    let mut sources = SourceMap::default();
    sources.insert(source.clone());
    for site in export_sites.values() {
        sources.insert(site.source.clone());
    }
    for origin in lowered.origins.values() {
        sources.insert(origin.site.source.clone());
        sources.insert(origin.definition.source.clone());
        for site in &origin.expansion {
            sources.insert(site.source.clone());
        }
        if let Some((left, right)) = &origin.operands {
            sources.insert(left.source.clone());
            sources.insert(right.source.clone());
        }
    }
    let main_module = context
        .source_file
        .as_ref()
        .map(|path| path.to_string_lossy().into_owned())
        .unwrap_or_else(|| source.name.to_string());
    let mut module_bindings = std::collections::BTreeMap::new();
    for relocation in &lowered.relocations {
        let module = match &relocation.path.anchor {
            crate::relocatable::Anchor::ModuleFile { module }
            | crate::relocatable::Anchor::ModuleDirectory { module } => module,
            _ => continue,
        };
        // These keys were emitted by the lowering source-context operation,
        // not inferred from a path prefix or the diagnostic source filename.
        module_bindings.insert(module.clone(), std::path::PathBuf::from(module));
    }
    Ok(RelocatableCompilation {
        compilation: Compilation {
            document,
            program,
            graph,
            planning: planning.clone(),
            source,
            sources,
            origins: lowered.origins,
        },
        relocations: lowered.relocations,
        exports,
        export_sites,
        main_module,
        module_bindings,
        source_modules: lowered.source_modules,
    })
}
