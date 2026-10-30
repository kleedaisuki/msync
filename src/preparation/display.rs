use super::PreparedProgram;
use crate::hir::effects::OperationFacts;
use crate::hir::graph::PlanGraph;
use crate::hir::{Input, InstructionId, InstructionKind, Output};
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
        for block in self.program.body.blocks() {
            writeln!(f, "block {} (contextualized at dispatch):", block.id.0)?;
            display_region(f, block.instructions, &self.facts)?;
        }
        display_graph(f, &self.graph)?;

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
    Ok(())
}

fn display_region(
    f: &mut fmt::Formatter<'_>,
    instructions: &[crate::hir::Instruction],
    facts: &BTreeMap<InstructionId, OperationFacts>,
) -> fmt::Result {
    for node in instructions {
        write!(f, "  {:04} ", node.id.0)?;
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
            InstructionKind::Barrier { kind } => writeln!(f, "barrier {kind:?}")?,
            InstructionKind::Await { process } => writeln!(f, "await {process:?}")?,
        }
    }
    Ok(())
}
