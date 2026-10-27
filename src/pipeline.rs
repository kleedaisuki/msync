//! Atomic structured frontend submission: syntax, expansion and effect obligations.
//!
//! This is deliberately separate from runtime preparation: building a compilation
//! never inspects a file or launches a subprocess. Execution adapters must refine
//! unknown file kinds and process effects before applying stronger rewrites.

use crate::compiler::CompileContext;
use crate::compiler::structured::Session;
use crate::hir::graph::{self, PlanGraph};
use crate::hir::{self, Instruction, InstructionId, InstructionKind, ProcessId, Region};
use crate::language::{self, Document};
use crate::syntax::{Report, Span};

#[derive(Clone, Debug)]
pub struct Compilation {
    pub document: Document,
    pub program: hir::Program,
    pub graph: PlanGraph,
}

/// Compile one submission, committing lexical state only when every pass succeeds.
pub fn compile(
    session: &mut Session,
    source: &str,
    context: &CompileContext,
    live_processes: &[ProcessId],
) -> Result<Compilation, Vec<Report>> {
    let document = language::parse(source)?;
    let mut candidate = session.clone();
    let program = candidate.compile(&document, context)?;
    let graph = graph::build_with_live_processes(&program, live_processes.iter().copied());
    if !graph.issues.is_empty() {
        return Err(graph
            .issues
            .iter()
            .map(|issue| {
                let mut sites = issue
                    .instructions
                    .iter()
                    .filter_map(|id| instruction(&program.body, *id));
                let site = sites.next().map_or(Span::default(), |node| node.span);
                let mut report = Report::new(&issue.message, site);
                for node in sites {
                    report = report.label(node.span, "this operation participates in the conflict");
                }
                report.help("Keep both ends of a live program stream in the same checkpoint region; do not cross a commit with a pipe needed to finish that program.")
            })
            .collect());
    }
    *session = candidate;
    Ok(Compilation {
        document,
        program,
        graph,
    })
}

fn instruction(region: &Region, id: InstructionId) -> Option<&Instruction> {
    for node in &region.instructions {
        if node.id == id {
            return Some(node);
        }
        if let InstructionKind::Transaction { body, .. } = &node.kind
            && let Some(found) = instruction(body, id)
        {
            return Some(found);
        }
    }
    None
}
