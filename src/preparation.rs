//! Metadata-only preparation of the structured IR.
//!
//! Type observations are premises at an instruction's epoch, not a license to
//! read payloads. Preparation never captures a snapshot, starts a user program,
//! opens a stream connection, or configures a process-resource view.

mod certificate;
mod display;
mod inspect;
mod optimize;
mod state;
mod types;

use std::collections::{BTreeMap, HashMap};
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;

use crate::endpoint::{Backend, Location};
use crate::hir::effects::OperationFacts;
use crate::hir::graph::{self, PlanGraph, PlanningContext};
use crate::hir::{self, Input, Instruction, InstructionId, InstructionKind, Output, Region};
use crate::language::DestinationMode;
use crate::plan::{AccessScope, FileClass, Resource};
use crate::syntax::{Report, Span};

use certificate::same_program;
use inspect::{Observations, resolve_region};
pub use optimize::{Rewrite, RewriteKind, Value};
use state::State;
use types::Types;

/// The semantic operand responsible for a preparation failure.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Operand {
    Operation,
    Source,
    Destination,
}

/// An owned diagnostic with a structural site, independent of message wording.
#[derive(Clone, Debug)]
pub struct Diagnostic {
    pub instruction: Option<InstructionId>,
    pub operand: Operand,
    pub report: Box<Report>,
}

impl Diagnostic {
    fn at(node: &Instruction, operand: Operand, report: Report) -> Self {
        Self {
            instruction: Some(node.id),
            operand,
            report: Box::new(report),
        }
    }

    fn general(report: Report) -> Self {
        Self {
            instruction: None,
            operand: Operand::Operation,
            report: Box::new(report),
        }
    }
}

/// An inspected and rewritten program plus the exact proof premises it used.
#[derive(Clone, Debug)]
pub struct PreparedProgram {
    pub original: Arc<hir::Program>,
    pub program: hir::Program,
    pub graph: PlanGraph,
    pub facts: BTreeMap<InstructionId, OperationFacts>,
    pub context: PlanningContext,
    pub rewrites: Vec<Rewrite>,
    witness: Witness,
}

#[derive(Clone, Debug)]
struct Witness {
    semantic: Arc<hir::Program>,
    original: Arc<hir::Program>,
    original_context: PlanningContext,
    resources: HashMap<Location, Resource>,
}

impl PreparedProgram {
    pub(crate) fn semantic_program(&self) -> &hir::Program {
        &self.witness.semantic
    }

    /// Replay rewrites and graph obligations using captured metadata proofs.
    /// No filesystem or network operation is performed by verification.
    pub fn verify(&self) -> Result<(), Vec<Report>> {
        if !Arc::ptr_eq(&self.original, &self.witness.original)
            || self.facts != self.context.operation_facts
        {
            return Err(vec![Report::new(
                "preparation premises were changed after inspection",
                Span::default(),
            )]);
        }
        let (program, rewrites) = optimize::run(
            &self.original,
            &rewrite_facts(
                &self.witness.original_context,
                &self.original,
                &self.witness.resources,
            ),
            &self.witness.resources,
            &self.witness.original_context.host_unstable,
            false,
        );
        if !same_program(&program, &self.program)
            || rewrites != self.rewrites
            || self.context != optimize::refined_context(&self.witness.original_context, &rewrites)
        {
            return Err(vec![Report::new(
                "rewrite certificate does not match the inspected program",
                Span::default(),
            )]);
        }
        self.graph
            .verify_with_context(&self.program, &self.context)
            .map_err(|issues| {
                issues
                    .into_iter()
                    .map(|issue| Report::new(issue.message, Span::default()))
                    .collect()
            })
    }

    pub fn is_execution_ready(&self) -> bool {
        self.graph.is_execution_ready() && verified_programs(&self.program.body, &self.context)
    }
}

fn verified_programs(region: &Region, context: &PlanningContext) -> bool {
    region.instructions.iter().all(|node| match &node.kind {
        InstructionKind::Spawn { process, .. } => context.process_effects.contains_key(process),
        InstructionKind::Transaction { body, .. } => verified_programs(body, context),
        _ => true,
    })
}

pub fn prepare(program: &hir::Program, live: bool) -> Result<PreparedProgram, Vec<Report>> {
    prepare_with_context(program, live, PlanningContext::default())
}

/// Runtime-owned version/type observations override the host's current metadata.
/// A caller must establish subprocess mediation before supplying its certificate.
pub fn prepare_with_context(
    program: &hir::Program,
    live: bool,
    context: PlanningContext,
) -> Result<PreparedProgram, Vec<Report>> {
    prepare_with_context_detailed(program, live, context)
        .map_err(|errors| errors.into_iter().map(|error| *error.report).collect())
}

/// Preparation diagnostics retaining instruction identity and operand ownership.
pub fn prepare_detailed(
    program: &hir::Program,
    live: bool,
) -> Result<PreparedProgram, Vec<Diagnostic>> {
    prepare_with_context_detailed(program, live, PlanningContext::default())
}

/// Context-aware preparation with structural diagnostic sites.
pub fn prepare_with_context_detailed(
    program: &hir::Program,
    live: bool,
    context: PlanningContext,
) -> Result<PreparedProgram, Vec<Diagnostic>> {
    let Observation {
        resolved,
        mut context,
        observations,
    } = observe_program(program, live, context)?;
    let resources = resources(&resolved, &observations);
    let facts = rewrite_facts(&context, &resolved, &resources);
    let (optimized, rewrites) =
        optimize::run(&resolved, &facts, &resources, &context.host_unstable, true);
    let original_context = context.clone();
    context = optimize::refined_context(&context, &rewrites);
    let graph = graph::build_with_context(&optimized, &context);
    if !graph.issues.is_empty() {
        return Err(graph
            .issues
            .iter()
            .map(|issue| Diagnostic {
                instruction: issue.instructions.first().copied(),
                operand: Operand::Operation,
                report: Box::new(Report::new(&issue.message, Span::default())),
            })
            .collect());
    }
    let original = Arc::new(resolved);
    let witness = Witness {
        semantic: Arc::new(program.clone()),
        original: original.clone(),
        original_context,
        resources,
    };
    let result = PreparedProgram {
        original,
        program: optimized,
        graph,
        facts: context.operation_facts.clone(),
        context,
        rewrites,
        witness,
    };
    result.verify().map_err(|errors| {
        errors
            .into_iter()
            .map(Diagnostic::general)
            .collect::<Vec<_>>()
    })?;
    Ok(result)
}

struct Observation {
    resolved: hir::Program,
    context: PlanningContext,
    observations: Observations,
}

pub(crate) fn observe_runtime(
    program: &hir::Program,
    context: PlanningContext,
) -> Result<(Arc<hir::Program>, PlanningContext), Vec<Diagnostic>> {
    let observed = observe_program(program, true, context)?;
    Ok((Arc::new(observed.resolved), observed.context))
}

fn observe_program(
    program: &hir::Program,
    live: bool,
    mut context: PlanningContext,
) -> Result<Observation, Vec<Diagnostic>> {
    let mut resolved = program.clone();
    let mut initial = State::initial(&context);
    let mut observations = Observations::new(initial.clone(), context.host_unstable.clone(), live);
    resolve_region(&mut resolved.body, &mut observations, &context)?;
    observations.complete(live)?;
    initial.extend_independent(&observations.independent_endpoints);
    context.host_unstable = observations.host_unstable.clone();
    let mut state = initial.clone();
    let mut interpreter = Types {
        observations: &observations,
        context: &mut context,
        histories: BTreeMap::new(),
        errors: Vec::new(),
        live,
    };
    interpreter.region(&resolved.body, &mut state, None);
    if !interpreter.errors.is_empty() {
        return Err(interpreter.errors);
    }
    Ok(Observation {
        resolved,
        context,
        observations,
    })
}

fn rewrite_facts(
    context: &PlanningContext,
    program: &hir::Program,
    resources: &HashMap<Location, Resource>,
) -> BTreeMap<InstructionId, OperationFacts> {
    fn retain(
        region: &Region,
        facts: &mut BTreeMap<InstructionId, OperationFacts>,
        resources: &HashMap<Location, Resource>,
    ) {
        for node in &region.instructions {
            match &node.kind {
                InstructionKind::Transfer {
                    source,
                    destination,
                } => {
                    let source_verified = !matches!(source, Input::File { location, .. } if !resources.contains_key(location));
                    let destination_verified = !matches!(destination, Output::File { location, .. } if !resources.contains_key(location));
                    if !source_verified || !destination_verified {
                        facts.remove(&node.id);
                    }
                }
                InstructionKind::Transaction { body, .. } => retain(body, facts, resources),
                _ => {}
            }
        }
    }
    let mut facts = context
        .operation_facts
        .iter()
        .filter(|(id, _)| {
            !context.pinned_inputs.contains_key(id) && !context.namespace_merges.contains_key(id)
        })
        .map(|(id, facts)| (*id, *facts))
        .collect();
    retain(&program.body, &mut facts, resources);
    facts
}

fn validate(
    node: &Instruction,
    source: &Input,
    destination: &Output,
    facts: OperationFacts,
    live: bool,
) -> Result<(), Diagnostic> {
    let source_error = |report| Diagnostic::at(node, Operand::Source, report);
    let destination_error = |report| Diagnostic::at(node, Operand::Destination, report);
    if let Input::File { location, pattern } = source {
        if facts.source == FileClass::Missing
            && (live || matches!(location.backend, Backend::Local))
        {
            return Err(source_error(endpoint_error(
                "source does not exist and no preceding operation produces it",
                location,
                node.span,
            )));
        }
        if pattern.is_some() && !matches!(facts.source, FileClass::Directory | FileClass::Unknown) {
            return Err(source_error(Report::new(
                "a selector requires a directory source",
                node.span,
            )));
        }
    }
    let Output::File { location, mode } = destination else {
        return if facts.source == FileClass::Directory {
            Err(source_error(Report::new(
                "a directory cannot be written to program stdin",
                node.span,
            )))
        } else {
            Ok(())
        };
    };
    if matches!(location.backend, Backend::Http { .. } | Backend::Path) {
        return Err(destination_error(endpoint_error(
            "destination is read-only",
            location,
            node.span,
        )));
    }
    if *mode == DestinationMode::Exact
        && facts.source == FileClass::Directory
        && facts.destination == FileClass::Regular
    {
        return Err(destination_error(endpoint_error(
            "a directory cannot replace an existing ordinary file",
            location,
            node.span,
        )));
    }
    match mode {
        DestinationMode::Exact if facts.destination == FileClass::Directory => Err(destination_error(endpoint_error(
            "an existing directory requires explicit '/' container or '/.' replacement syntax",
            location,
            node.span,
        ).help("To insert the source into the directory, append '/'; use '/.' to replace its tree."))),
        DestinationMode::Container if !matches!(source, Input::File { .. }) => Err(destination_error(Report::new(
            "a container insertion needs a file or directory source basename",
            node.span,
        ))),
        DestinationMode::Container
            if matches!(facts.destination, FileClass::Regular | FileClass::Stream) =>
        {
            Err(destination_error(endpoint_error(
                "a container destination must be a directory or missing",
                location,
                node.span,
            )))
        }
        DestinationMode::ReplaceTree
            if !matches!(facts.source, FileClass::Directory | FileClass::Unknown) =>
        {
            Err(source_error(Report::new(
                "'/.' replacement requires a directory source",
                node.span,
            )))
        }
        DestinationMode::ReplaceTree
            if !matches!(
                facts.destination,
                FileClass::Directory | FileClass::Missing | FileClass::Unknown
            ) =>
        {
            Err(destination_error(endpoint_error(
                "'/.' replacement requires a directory destination or missing path",
                location,
                node.span,
            )))
        }
        _ if facts.source == FileClass::Directory && facts.destination == FileClass::Stream => {
            Err(source_error(Report::new(
                "a directory cannot be written to a pipe or socket",
                node.span,
            ).help("Select an ordinary file, or explicitly serialize the directory with a program before connecting its stdout.")))
        }
        _ => Ok(()),
    }
}

fn endpoint_error(message: impl Into<String>, location: &Location, span: Span) -> Report {
    Report::new(message, span).help(format!("Resolved endpoint: {location}"))
}

fn parents(location: &Location) -> Vec<Location> {
    if !matches!(location.backend, Backend::Local) {
        return Vec::new();
    }
    Path::new(&location.path)
        .ancestors()
        .skip(1)
        .map(|parent| Location {
            backend: Backend::Local,
            path: parent.to_string_lossy().into_owned(),
        })
        .collect()
}

fn absolute(path: &Path) -> std::io::Result<PathBuf> {
    let absolute = std::path::absolute(path)?;
    let mut clean = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                clean.pop();
            }
            part => clean.push(part.as_os_str()),
        }
    }
    Ok(clean)
}

fn resources(program: &hir::Program, observations: &Observations) -> HashMap<Location, Resource> {
    fn capture(
        location: &Location,
        observations: &Observations,
        cache: &mut crate::plan::ResourceSnapshots,
    ) -> Option<Resource> {
        if let Some(canonical) = observations.canonical_remote.get(location) {
            Some(cache.capture(location, canonical, AccessScope::Exact))
        } else if observations.verified.contains(location) {
            Some(cache.capture(location, location, AccessScope::Exact))
        } else {
            crate::plan::logical_remote_resource(location, AccessScope::Exact)
        }
    }

    fn walk(
        region: &Region,
        observations: &Observations,
        cache: &mut crate::plan::ResourceSnapshots,
        output: &mut HashMap<Location, Resource>,
    ) {
        for node in &region.instructions {
            match &node.kind {
                InstructionKind::Transfer {
                    source,
                    destination,
                } => {
                    if let Input::File { location, .. } = source
                        && !output.contains_key(location)
                        && let Some(resource) = capture(location, observations, cache)
                    {
                        output.entry(location.clone()).or_insert(resource);
                    }
                    if let Output::File { location, .. } = destination
                        && !output.contains_key(location)
                        && let Some(resource) = capture(location, observations, cache)
                    {
                        output.entry(location.clone()).or_insert(resource);
                    }
                }
                InstructionKind::Transaction { body, .. } => {
                    walk(body, observations, cache, output)
                }
                _ => {}
            }
        }
    }
    let mut output = HashMap::new();
    let mut cache = crate::plan::ResourceSnapshots::default();
    walk(&program.body, observations, &mut cache, &mut output);
    for (native, wsl) in &observations.independent_endpoints {
        if let Some(other) = output.get(wsl).cloned()
            && let Some(resource) = output.get_mut(native)
        {
            resource.prove_disjoint(&other);
        }
    }
    output
}
