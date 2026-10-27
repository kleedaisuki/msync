use super::PreparedProgram;
use crate::hir::effects::OperationFacts;
use crate::hir::graph::{PlanGraph, PlanningContext};
use crate::hir::{Input, InstructionId, InstructionKind, Output, Region};
use std::collections::BTreeMap;
use std::fmt;
impl fmt::Display for PreparedProgram {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        writeln!(
            f,
            "structured plan: {} rewrite(s), execution_ready={}",
            self.rewrites.len(),
            self.is_execution_ready()
        )?;
        display_region(f, &self.program.body, &self.facts, 0)?;
        display_graph(f, &self.graph)?;
        display_program_premises(f, &self.program.body, &self.context)?;
        for rewrite in &self.rewrites {
            writeln!(f, "rewrite #{}: {:?}", rewrite.instruction.0, rewrite.kind)?;
        }
        for pending in &self.graph.refinements {
            writeln!(
                f,
                "refine #{}: {} ({:?})",
                pending.instruction.0, pending.location, pending.kind
            )?;
        }
        Ok(())
    }
}

fn display_graph(f: &mut fmt::Formatter<'_>, graph: &PlanGraph) -> fmt::Result {
    for edge in &graph.dependencies {
        writeln!(
            f,
            "needs #{} <- #{} ({:?})",
            edge.after.0, edge.before.0, edge.wait
        )?;
    }
    for cohort in &graph.co_start {
        writeln!(
            f,
            "co-start program[{}]: {:?}",
            cohort.process.0, cohort.members
        )?;
    }
    for node in &graph.nodes {
        if let Some(children) = &node.children {
            display_graph(f, children)?;
        }
    }
    Ok(())
}

fn display_program_premises(
    f: &mut fmt::Formatter<'_>,
    region: &Region,
    context: &PlanningContext,
) -> fmt::Result {
    for node in &region.instructions {
        match &node.kind {
            InstructionKind::Spawn { process, .. }
                if !context.process_effects.contains_key(process) =>
            {
                writeln!(
                    f,
                    "refine #{}: verified program-resource view required",
                    node.id.0
                )?;
            }
            InstructionKind::Transaction { body, .. } => {
                display_program_premises(f, body, context)?
            }
            _ => {}
        }
    }
    Ok(())
}

fn display_region(
    f: &mut fmt::Formatter<'_>,
    region: &Region,
    facts: &BTreeMap<InstructionId, OperationFacts>,
    depth: usize,
) -> fmt::Result {
    for node in &region.instructions {
        write!(f, "{}{:04} ", "  ".repeat(depth), node.id.0)?;
        match &node.kind {
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                match source {
                    Input::File { location, pattern } => {
                        write!(f, "{location}")?;
                        if let Some(pattern) = pattern {
                            write!(f, " select {pattern:?}")?;
                        }
                    }
                    Input::Literal(text) => write!(f, "{text:?}")?,
                    Input::Port { process, port } => write!(f, "program[{}]::{port:?}", process.0)?,
                }
                match destination {
                    Output::File { location, mode } => write!(f, " -> {location} ({mode:?})")?,
                    Output::Stdin(process) => write!(f, " -> program[{}]::stdin", process.0)?,
                }
                if let Some(row) = facts.get(&node.id) {
                    write!(f, " [{:?} -> {:?}]", row.source, row.destination)?;
                }
                writeln!(f)?;
            }
            InstructionKind::Spawn {
                process,
                executable,
                arguments,
                cwd,
            } => writeln!(
                f,
                "spawn program[{}] {executable} argv={arguments:?} cwd={cwd:?}",
                process.0
            )?,
            InstructionKind::Transaction { transaction, body } => {
                writeln!(f, "transaction[{}]", transaction.0)?;
                display_region(f, body, facts, depth + 1)?;
            }
            InstructionKind::Commit { transaction } => {
                writeln!(f, "commit transaction[{}]", transaction.0)?
            }
            InstructionKind::Rollback { transaction, scope } => {
                writeln!(f, "rollback transaction[{}] {scope:?}", transaction.0)?
            }
        }
    }
    Ok(())
}
