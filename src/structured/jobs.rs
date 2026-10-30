//! Logical REPL jobs group physically owned work connected by native ports.

use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;

use msync::execution::WorkId;
use msync::hir::{Input, InstructionId, InstructionKind, Output, ProcessId};

use super::unit::ExecutionUnit;

pub(super) struct Job {
    pub id: u64,
    work: BTreeMap<WorkId, Work>,
}

struct Work {
    source: Arc<ExecutionUnit>,
    instruction: Option<InstructionId>,
    programs: BTreeSet<ProcessId>,
}

impl Work {
    fn new(id: WorkId, source: &Arc<ExecutionUnit>) -> Self {
        let node = source
            .program
            .body
            .instructions
            .iter()
            .find(|node| match id {
                WorkId::Program(process) => {
                    matches!(node.kind, InstructionKind::Spawn { process: id, .. } if id == process)
                }
                WorkId::Flow { instruction, .. } => node.id == instruction,
            });
        Self {
            source: Arc::clone(source),
            instruction: node.map(|node| node.id),
            programs: node.map_or_else(BTreeSet::new, |node| programs(&node.kind)),
        }
    }
}

fn programs(instruction: &InstructionKind) -> BTreeSet<ProcessId> {
    let (source, target) = match instruction {
        InstructionKind::Spawn { process, .. } => return [*process].into(),
        InstructionKind::Transfer {
            source,
            destination,
            ..
        } => (source, destination),
        _ => return BTreeSet::new(),
    };
    let source = match source {
        Input::Port { process, .. } => Some(*process),
        _ => None,
    };
    let target = match target {
        Output::Stdin(process) => Some(*process),
        _ => None,
    };
    source.into_iter().chain(target).collect()
}

impl Job {
    fn connected(&self, other: &Self) -> bool {
        self.work.values().any(|left| {
            other
                .work
                .values()
                .any(|right| !left.programs.is_disjoint(&right.programs))
        })
    }

    pub fn retain(&mut self, pending: &BTreeSet<WorkId>) {
        self.work.retain(|id, _| pending.contains(id));
    }

    pub fn is_empty(&self) -> bool {
        self.work.is_empty()
    }
}

pub(super) struct Admission {
    pub id: u64,
    pub created: bool,
    pub merged: Vec<u64>,
}

pub(super) fn register(
    jobs: &mut Vec<Job>,
    next_job: &mut u64,
    added: BTreeSet<WorkId>,
    source: &Arc<ExecutionUnit>,
) -> Admission {
    assert!(!added.is_empty(), "a logical job must own admitted work");
    let mut joined = Job {
        id: u64::MAX,
        work: added
            .into_iter()
            .map(|id| (id, Work::new(id, source)))
            .collect(),
    };
    let mut merged = Vec::new();
    while let Some(index) = jobs.iter().position(|job| job.connected(&joined)) {
        let previous = jobs.remove(index);
        joined.id = joined.id.min(previous.id);
        merged.push(previous.id);
        joined.work.extend(previous.work);
    }
    let created = merged.is_empty();
    if created {
        *next_job += 1;
        joined.id = *next_job;
    }
    let id = joined.id;
    merged.retain(|previous| *previous != id);
    jobs.push(joined);
    jobs.sort_unstable_by_key(|job| job.id);
    Admission {
        id,
        created,
        merged,
    }
}

pub(super) fn source(jobs: &[Job], instruction: Option<InstructionId>) -> Option<&ExecutionUnit> {
    let mut sources = jobs
        .iter()
        .flat_map(|job| job.work.values())
        .filter(|work| instruction.is_none() || work.instruction == instruction)
        .map(|work| &work.source);
    let first = sources.next()?;
    // Instruction IDs are local to a compilation. Without an owned origin,
    // an ambiguous ID must not select an unrelated submission's source.
    sources
        .all(|source| Arc::ptr_eq(first, source))
        .then_some(first.as_ref())
}

#[cfg(test)]
mod tests {
    use super::*;
    use msync::compiler::{CompileContext, structured::Session};

    fn compile(compiler: &mut Session, name: &str, text: &str) -> Arc<ExecutionUnit> {
        let planning = msync::hir::graph::PlanningContext {
            live_processes: (0..compiler.object_id_bases()).map(ProcessId).collect(),
            ..Default::default()
        };
        Arc::new(
            msync::pipeline::compile_named(
                compiler,
                text,
                name,
                &CompileContext {
                    pwd: env!("CARGO_MANIFEST_DIR").into(),
                    source_file: None,
                },
                &planning,
            )
            .unwrap()
            .into(),
        )
    }

    fn program(unit: &ExecutionUnit) -> WorkId {
        unit.program
            .body
            .instructions
            .iter()
            .find_map(|node| match node.kind {
                InstructionKind::Spawn { process, .. } => Some(WorkId::Program(process)),
                _ => None,
            })
            .unwrap()
    }

    fn flow(unit: &ExecutionUnit, submission: u64) -> WorkId {
        let instruction = unit
            .program
            .body
            .instructions
            .iter()
            .find(|node| matches!(node.kind, InstructionKind::Transfer { .. }))
            .unwrap()
            .id;
        WorkId::Flow {
            submission,
            instruction,
        }
    }

    #[test]
    fn a_later_program_flow_joins_its_existing_job_and_retains_its_own_source() {
        let mut compiler = Session::new();
        let launch = compile(
            &mut compiler,
            "launch.msync",
            "async child => $path::cat()\n",
        );
        let feed = compile(
            &mut compiler,
            "feed.msync",
            "wire => tcp://127.0.0.1:12345\nwire::/ -> child::stdin\n",
        );
        let process = program(&launch);
        let input = flow(&feed, 2);
        let WorkId::Flow { instruction, .. } = input else {
            unreachable!()
        };
        let mut jobs = Vec::new();
        let mut next = 0;
        assert!(register(&mut jobs, &mut next, [process].into(), &launch).created);
        let admission = register(&mut jobs, &mut next, [input].into(), &feed);
        assert_eq!(admission.id, 1);
        assert!(!admission.created);
        assert!(admission.merged.is_empty());
        assert_eq!(next, 1);
        assert_eq!(jobs.len(), 1);
        assert_eq!(jobs[0].work.len(), 2);
        assert_eq!(
            source(&jobs, Some(instruction))
                .unwrap()
                .source
                .name
                .as_ref(),
            "feed.msync"
        );
        assert!(source(&jobs, None).is_none());

        let retained = Arc::downgrade(&feed);
        drop(feed);
        assert!(retained.upgrade().is_some());
        jobs[0].retain(&[process].into());
        assert!(retained.upgrade().is_none());
        assert_eq!(
            source(&jobs, None).unwrap().source.name.as_ref(),
            "launch.msync"
        );
    }

    #[test]
    fn independent_physical_flows_keep_distinct_jobs() {
        let mut compiler = Session::new();
        let one = compile(
            &mut compiler,
            "one.msync",
            "wire => tcp://127.0.0.1:12345\nwire::/ -> one\n",
        );
        let two = compile(&mut compiler, "two.msync", "wire::/ -> two\n");
        let mut jobs = Vec::new();
        let mut next = 0;
        let first = register(&mut jobs, &mut next, [flow(&one, 1)].into(), &one);
        let second = register(&mut jobs, &mut next, [flow(&two, 2)].into(), &two);
        assert_eq!((first.id, second.id), (1, 2));
        assert!(first.created && second.created);
        assert_eq!(jobs.len(), 2);
        let WorkId::Flow { instruction, .. } = flow(&one, 1) else {
            unreachable!()
        };
        assert!(source(&jobs, Some(instruction)).is_none());
    }

    #[test]
    fn a_program_bridge_joins_both_jobs_under_the_oldest_identity() {
        let mut compiler = Session::new();
        let one = compile(&mut compiler, "one.msync", "async one => $path::cat()\n");
        let two = compile(&mut compiler, "two.msync", "async two => $path::cat()\n");
        let bridge = compile(&mut compiler, "bridge.msync", "one::stdout -> two::stdin\n");
        let mut jobs = Vec::new();
        let mut next = 0;
        register(&mut jobs, &mut next, [program(&one)].into(), &one);
        register(&mut jobs, &mut next, [program(&two)].into(), &two);
        let admission = register(&mut jobs, &mut next, [flow(&bridge, 3)].into(), &bridge);
        assert_eq!(admission.id, 1);
        assert!(!admission.created);
        assert_eq!(admission.merged, [2]);
        assert_eq!(next, 2);
        assert_eq!(jobs.len(), 1);
        assert_eq!(jobs[0].id, 1);
        assert_eq!(jobs[0].work.len(), 3);
    }

    #[test]
    fn an_unadmitted_port_use_does_not_attach_an_independent_flow_to_its_job() {
        let mut compiler = Session::new();
        let launch = compile(
            &mut compiler,
            "launch.msync",
            "async child => $path::cat()\n",
        );
        let mixed = compile(
            &mut compiler,
            "mixed.msync",
            "wire => tcp://127.0.0.1:12345\nwire::/ -> output\nchild::stdout -> later\n",
        );
        let mut jobs = Vec::new();
        let mut next = 0;
        register(&mut jobs, &mut next, [program(&launch)].into(), &launch);
        let admission = register(&mut jobs, &mut next, [flow(&mixed, 2)].into(), &mixed);
        assert_eq!(admission.id, 2);
        assert!(admission.created);
        assert_eq!(jobs.len(), 2);
    }
}
