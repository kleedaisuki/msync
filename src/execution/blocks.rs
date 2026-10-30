//! A deterministic ready frontier for one interpreted basic block.

use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::fmt;

use crate::hir::InstructionId;
use crate::hir::graph::{PlanGraph, Wait};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum State {
    Pending,
    Running,
    Complete,
}

#[derive(Debug, PartialEq, Eq)]
pub(super) struct ScheduleError(pub String);

impl fmt::Display for ScheduleError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for ScheduleError {}

pub(super) struct BlockSchedule {
    order: Vec<InstructionId>,
    states: BTreeMap<InstructionId, State>,
    predecessors: BTreeMap<InstructionId, Vec<(InstructionId, bool)>>,
    captured_read_edges: BTreeSet<(InstructionId, usize)>,
}

impl BlockSchedule {
    pub fn new(graph: &PlanGraph) -> Result<Self, ScheduleError> {
        let order: Vec<_> = graph.nodes.iter().map(|node| node.instruction).collect();
        if graph.blocks.len() > 1 {
            return Err(ScheduleError(
                "a scheduler frontier cannot span basic blocks".into(),
            ));
        }
        if let Some(block) = graph.blocks.first()
            && block.instructions != order
        {
            return Err(ScheduleError(
                "basic block membership does not match its graph nodes".into(),
            ));
        }
        let mut schedule = Self::from_parts(
            order,
            graph.dependencies.iter().map(|edge| {
                let admitted = matches!(edge.wait, Wait::Admitted | Wait::ProcessStarted(_));
                (edge.before, edge.after, admitted)
            }),
        )?;
        let mut offsets = BTreeMap::<InstructionId, usize>::new();
        for edge in &graph.dependencies {
            let offset = offsets.entry(edge.after).or_default();
            if !edge.reasons.is_empty() && edge.reasons.iter().all(captured_read_reason) {
                schedule.captured_read_edges.insert((edge.after, *offset));
            }
            *offset += 1;
        }
        Ok(schedule)
    }

    fn from_parts(
        order: Vec<InstructionId>,
        edges: impl IntoIterator<Item = (InstructionId, InstructionId, bool)>,
    ) -> Result<Self, ScheduleError> {
        let states: BTreeMap<_, _> = order.iter().map(|id| (*id, State::Pending)).collect();
        if states.len() != order.len() {
            return Err(ScheduleError(
                "basic block contains duplicate instruction IDs".into(),
            ));
        }
        let mut predecessors = BTreeMap::<_, Vec<_>>::new();
        for (before, after, admitted) in edges {
            if before == after || !states.contains_key(&before) || !states.contains_key(&after) {
                return Err(ScheduleError(
                    "dependency is not between distinct nodes of this block".into(),
                ));
            }
            predecessors
                .entry(after)
                .or_default()
                .push((before, admitted));
        }
        let schedule = Self {
            order,
            states,
            predecessors,
            captured_read_edges: BTreeSet::new(),
        };
        schedule.check_acyclic()?;
        Ok(schedule)
    }

    fn check_acyclic(&self) -> Result<(), ScheduleError> {
        let mut counts = BTreeMap::new();
        let mut successors = BTreeMap::<_, Vec<_>>::new();
        let mut ready = VecDeque::new();
        for id in &self.order {
            let predecessors = self.predecessors.get(id).map(Vec::as_slice).unwrap_or(&[]);
            counts.insert(*id, predecessors.len());
            if predecessors.is_empty() {
                ready.push_back(*id);
            }
            for (before, _) in predecessors {
                successors.entry(*before).or_default().push(*id);
            }
        }
        let mut visited = 0;
        while let Some(id) = ready.pop_front() {
            visited += 1;
            for successor in successors.get(&id).into_iter().flatten() {
                let count = counts.get_mut(successor).expect("validated successor");
                *count -= 1;
                if *count == 0 {
                    ready.push_back(*successor);
                }
            }
        }
        if visited == self.order.len() {
            Ok(())
        } else {
            Err(ScheduleError(
                "basic block dependency graph contains a cycle".into(),
            ))
        }
    }

    fn is_ready(&self, id: InstructionId) -> bool {
        self.states.get(&id) == Some(&State::Pending)
            && self
                .predecessors
                .get(&id)
                .into_iter()
                .flatten()
                .all(|(before, admitted)| match self.states[before] {
                    State::Complete => true,
                    State::Running => *admitted,
                    State::Pending => false,
                })
    }

    pub fn ready(&self) -> Vec<InstructionId> {
        self.order
            .iter()
            .copied()
            .filter(|id| self.is_ready(*id))
            .collect()
    }

    pub fn admit(&mut self, id: InstructionId) -> Result<(), ScheduleError> {
        if !self.is_ready(id) {
            return Err(ScheduleError(format!(
                "instruction {} is not ready for admission",
                id.0
            )));
        }
        self.states.insert(id, State::Running);
        Ok(())
    }

    pub fn complete(&mut self, id: InstructionId) -> Result<(), ScheduleError> {
        if self.states.get(&id) != Some(&State::Running) {
            return Err(ScheduleError(format!(
                "instruction {} was not admitted",
                id.0
            )));
        }
        self.states.insert(id, State::Complete);
        Ok(())
    }

    /// Called only after runtime acquisition owns an immutable source snapshot.
    pub fn captured_input(&mut self, id: InstructionId) -> Result<(), ScheduleError> {
        if self.states.get(&id) != Some(&State::Running) {
            return Err(ScheduleError(
                "captured input belongs to an unadmitted instruction".into(),
            ));
        }
        for (after, incoming) in &mut self.predecessors {
            for (index, (before, admitted)) in incoming.iter_mut().enumerate() {
                if *before == id && self.captured_read_edges.contains(&(*after, index)) {
                    *admitted = true;
                }
            }
        }
        Ok(())
    }

    /// The caller proves the rewrite; this frontier checks its external gates.
    pub fn group_ready(&self, ids: &[InstructionId]) -> bool {
        let members: BTreeSet<_> = ids.iter().copied().collect();
        self.coadmission_ready(ids)
            && !self.predecessors.iter().any(|(after, incoming)| {
                !members.contains(after)
                    && incoming
                        .iter()
                        .any(|(before, admitted)| *admitted && members.contains(before))
            })
    }

    /// Connected physical work must reserve together, but only after every
    /// prerequisite outside the cohort can run without its reserved resources.
    pub fn coadmission_ready(&self, ids: &[InstructionId]) -> bool {
        let members: BTreeSet<_> = ids.iter().copied().collect();
        !members.is_empty()
            && members.len() == ids.len()
            && ids.iter().all(|id| {
                self.states.get(id) == Some(&State::Pending)
                    && self
                        .predecessors
                        .get(id)
                        .into_iter()
                        .flatten()
                        .all(|(before, admitted)| {
                            members.contains(before)
                                || match self.states[before] {
                                    State::Complete => true,
                                    State::Running => *admitted,
                                    State::Pending => false,
                                }
                        })
            })
    }

    pub fn admit_group(&mut self, ids: &[InstructionId]) -> Result<(), ScheduleError> {
        if !self.group_ready(ids) {
            return Err(ScheduleError(
                "instruction group is not ready for admission".into(),
            ));
        }
        for id in ids {
            self.states.insert(*id, State::Running);
        }
        Ok(())
    }

    pub fn complete_group(&mut self, ids: &[InstructionId]) -> Result<(), ScheduleError> {
        self.transition_group(ids, State::Complete)
    }

    /// Only a failed speculative guard with no observable effects may retry.
    pub fn retry_group(&mut self, ids: &[InstructionId]) -> Result<(), ScheduleError> {
        let members: BTreeSet<_> = ids.iter().copied().collect();
        if self.predecessors.iter().any(|(after, incoming)| {
            !members.contains(after)
                && self.states[after] != State::Pending
                && incoming
                    .iter()
                    .any(|(before, admitted)| *admitted && members.contains(before))
        }) {
            return Err(ScheduleError(
                "cannot retry a group after a dependent was admitted".into(),
            ));
        }
        self.transition_group(ids, State::Pending)
    }

    fn transition_group(
        &mut self,
        ids: &[InstructionId],
        state: State,
    ) -> Result<(), ScheduleError> {
        let members: BTreeSet<_> = ids.iter().copied().collect();
        if members.is_empty()
            || members.len() != ids.len()
            || ids
                .iter()
                .any(|id| self.states.get(id) != Some(&State::Running))
        {
            return Err(ScheduleError(
                "instruction group was not completely admitted".into(),
            ));
        }
        for id in ids {
            self.states.insert(*id, state);
        }
        Ok(())
    }

    pub fn is_finished(&self) -> bool {
        self.states.values().all(|state| *state == State::Complete)
    }
    pub fn all_admitted(&self) -> bool {
        self.states.values().all(|state| *state != State::Pending)
    }
    pub fn is_running(&self, id: InstructionId) -> bool {
        self.states.get(&id) == Some(&State::Running)
    }

    #[cfg(test)]
    pub fn running(&self) -> usize {
        self.states
            .values()
            .filter(|state| **state == State::Running)
            .count()
    }
}

fn captured_read_reason(reason: &crate::hir::graph::Reason) -> bool {
    use crate::hir::effects::{AccessKind, Resource};
    use crate::hir::graph::Reason;
    matches!(reason,
        Reason::Hazard(conflict)
            if conflict.before.kind == AccessKind::Read
                && matches!(&conflict.before.resource, Resource::File(_))
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use proptest::prelude::*;

    fn id(value: usize) -> InstructionId {
        InstructionId(value)
    }

    #[test]
    fn captured_source_releases_only_its_read_hazards() {
        use crate::hir::effects::{
            Access, AccessKind, Conflict, Effects, FileResource, Hazard, Resource, Scope,
        };
        use crate::hir::graph::{Dependency, Node, Reason};
        let file = Resource::File(FileResource::new(
            crate::endpoint::Location {
                backend: crate::endpoint::Backend::Local,
                path: "/captured".into(),
            },
            Scope::Exact,
        ));
        let hazard = |kind| {
            Reason::Hazard(Box::new(Conflict {
                kind: Hazard::WriteAfterRead,
                before: Access {
                    resource: file.clone(),
                    kind,
                },
                after: Access {
                    resource: file.clone(),
                    kind: AccessKind::Write,
                },
            }))
        };
        let graph = PlanGraph {
            nodes: (0..4)
                .map(|n| Node {
                    instruction: id(n),
                    effects: Effects::default(),
                    children: None,
                })
                .collect(),
            dependencies: vec![
                Dependency {
                    before: id(0),
                    after: id(1),
                    wait: Wait::Completion,
                    reasons: vec![hazard(AccessKind::Read)],
                },
                Dependency {
                    before: id(0),
                    after: id(2),
                    wait: Wait::Completion,
                    reasons: vec![hazard(AccessKind::Write)],
                },
                Dependency {
                    before: id(0),
                    after: id(3),
                    wait: Wait::Completion,
                    reasons: vec![hazard(AccessKind::Read), hazard(AccessKind::Write)],
                },
            ],
            ..Default::default()
        };
        let mut schedule = BlockSchedule::new(&graph).unwrap();
        assert!(schedule.captured_input(id(0)).is_err());
        schedule.admit(id(0)).unwrap();
        assert!(schedule.ready().is_empty());
        schedule.captured_input(id(0)).unwrap();
        assert_eq!(schedule.ready(), vec![id(1)]);
        schedule.complete(id(0)).unwrap();
        assert_eq!(schedule.ready(), vec![id(1), id(2), id(3)]);
    }

    #[test]
    fn admission_edges_do_not_wait_for_eof() {
        let mut schedule = BlockSchedule::from_parts(
            vec![id(0), id(1), id(2)],
            [(id(0), id(1), true), (id(0), id(2), false)],
        )
        .unwrap();
        assert_eq!(schedule.ready(), vec![id(0)]);
        schedule.admit(id(0)).unwrap();
        assert_eq!(schedule.ready(), vec![id(1)]);
        schedule.complete(id(0)).unwrap();
        assert_eq!(schedule.ready(), vec![id(1), id(2)]);
    }

    #[test]
    fn physical_cohort_waits_for_every_external_completion_before_reserving() {
        let mut schedule = BlockSchedule::from_parts(
            (0..3).map(id).collect(),
            [(id(0), id(2), true), (id(1), id(2), false)],
        )
        .unwrap();
        let cohort = [id(0), id(2)];
        assert_eq!(schedule.ready(), vec![id(0), id(1)]);
        assert!(!schedule.coadmission_ready(&cohort));
        schedule.admit(id(1)).unwrap();
        assert!(!schedule.coadmission_ready(&cohort));
        schedule.complete(id(1)).unwrap();
        assert!(schedule.coadmission_ready(&cohort));
        schedule.admit(id(0)).unwrap();
        assert_eq!(schedule.ready(), vec![id(2)]);
        schedule.admit(id(2)).unwrap();
    }

    #[test]
    fn physical_coadmission_preserves_instead_of_contracting_outgoing_edges() {
        let mut schedule = BlockSchedule::from_parts(
            (0..4).map(id).collect(),
            [
                (id(0), id(2), true),
                (id(1), id(2), true),
                (id(2), id(3), true),
            ],
        )
        .unwrap();
        let cohort = [id(0), id(2)];
        assert!(!schedule.coadmission_ready(&cohort));
        schedule.admit(id(1)).unwrap();
        assert!(schedule.coadmission_ready(&cohort));
        assert!(!schedule.group_ready(&cohort));
        assert!(!schedule.coadmission_ready(&[]));
        assert!(!schedule.coadmission_ready(&[id(0), id(0)]));
        assert!(!schedule.coadmission_ready(&[id(8)]));
        schedule.admit(id(0)).unwrap();
        schedule.admit(id(2)).unwrap();
        assert_eq!(schedule.ready(), vec![id(3)]);
    }

    #[test]
    fn contraction_waits_for_every_external_predecessor() {
        let mut schedule = BlockSchedule::from_parts(
            (0..4).map(id).collect(),
            [(id(0), id(2), false), (id(1), id(2), false)],
        )
        .unwrap();
        assert!(!schedule.group_ready(&[id(1), id(2)]));
        assert!(schedule.admit_group(&[id(1), id(2)]).is_err());
        assert!(schedule.group_ready(&[id(1)]));
        schedule.admit(id(0)).unwrap();
        assert!(!schedule.group_ready(&[id(1), id(2)]));
        schedule.complete(id(0)).unwrap();
        assert!(schedule.group_ready(&[id(1), id(2)]));
        schedule.admit_group(&[id(1), id(2)]).unwrap();
        assert!(schedule.admit_group(&[id(1), id(2)]).is_err());
        assert!(schedule.complete_group(&[id(1), id(3)]).is_err());
        schedule.complete_group(&[id(1), id(2)]).unwrap();
        assert!(schedule.complete_group(&[id(1), id(2)]).is_err());
        assert_eq!(schedule.ready(), vec![id(3)]);
    }

    #[test]
    fn independent_prefix_does_not_block_a_contraction() {
        let mut schedule =
            BlockSchedule::from_parts((0..3).map(id).collect(), [(id(1), id(2), false)]).unwrap();
        assert!(schedule.group_ready(&[id(1), id(2)]));
        assert!(!schedule.group_ready(&[]));
        assert!(!schedule.group_ready(&[id(1), id(1)]));
        assert!(!schedule.group_ready(&[id(10)]));
        schedule.admit_group(&[id(1), id(2)]).unwrap();
        assert_eq!(schedule.ready(), vec![id(0)]);
        schedule.complete_group(&[id(1), id(2)]).unwrap();
        schedule.admit(id(0)).unwrap();
        schedule.complete(id(0)).unwrap();
        assert!(schedule.is_finished());
    }

    #[test]
    fn deoptimization_restores_the_original_dependency_order() {
        let mut schedule = BlockSchedule::from_parts(
            (0..3).map(id).collect(),
            [(id(0), id(1), false), (id(1), id(2), false)],
        )
        .unwrap();
        schedule.admit_group(&[id(0), id(1)]).unwrap();
        assert!(schedule.retry_group(&[id(0), id(2)]).is_err());
        assert!(schedule.ready().is_empty());
        schedule.retry_group(&[id(0), id(1)]).unwrap();
        assert_eq!(schedule.ready(), vec![id(0)]);
        for next in 0..3 {
            assert_eq!(schedule.ready(), vec![id(next)]);
            schedule.admit(id(next)).unwrap();
            schedule.complete(id(next)).unwrap();
        }
        assert!(schedule.retry_group(&[id(0), id(1)]).is_err());
        assert!(schedule.is_finished());
    }

    #[test]
    fn speculation_cannot_release_an_early_external_consumer() {
        let mut schedule = BlockSchedule::from_parts(
            (0..3).map(id).collect(),
            [(id(0), id(1), false), (id(1), id(2), true)],
        )
        .unwrap();
        assert!(!schedule.group_ready(&[id(0), id(1)]));
        schedule.admit(id(0)).unwrap();
        schedule.complete(id(0)).unwrap();
        schedule.admit(id(1)).unwrap();
        schedule.admit(id(2)).unwrap();
        assert!(schedule.retry_group(&[id(1)]).is_err());
        assert_eq!(schedule.running(), 2);
    }

    #[test]
    fn malformed_graph_is_rejected_before_admission() {
        assert!(BlockSchedule::from_parts(vec![id(0), id(0)], []).is_err());
        assert!(BlockSchedule::from_parts(vec![id(0)], [(id(0), id(1), false)]).is_err());
        assert!(
            BlockSchedule::from_parts(
                vec![id(0), id(1)],
                [(id(0), id(1), false), (id(1), id(0), true),]
            )
            .is_err()
        );
    }

    #[test]
    fn a_frontier_cannot_admit_a_future_block() {
        use crate::hir::BlockId;
        use crate::hir::graph::BlockPlan;
        let mut graph = PlanGraph {
            blocks: vec![
                BlockPlan {
                    id: BlockId(0),
                    instructions: vec![],
                },
                BlockPlan {
                    id: BlockId(1),
                    instructions: vec![],
                },
            ],
            ..Default::default()
        };
        assert!(BlockSchedule::new(&graph).is_err());
        graph.blocks.truncate(1);
        graph.blocks[0].instructions.push(id(0));
        assert!(BlockSchedule::new(&graph).is_err());
    }

    proptest! {
        #[test]
        fn completion_frontier_respects_every_dependency(
            count in 1usize..40,
            candidates in prop::collection::vec((0usize..40, 0usize..40), 0..180),
        ) {
            let edges: Vec<_> = candidates.into_iter()
                .filter(|(a,b)| a < b && *b < count)
                .map(|(a,b)| (id(a),id(b),false)).collect();
            let mut schedule = BlockSchedule::from_parts((0..count).map(id).collect(), edges.clone()).unwrap();
            let mut completed = BTreeSet::new();
            while !schedule.is_finished() {
                let ready = schedule.ready();
                prop_assert!(!ready.is_empty());
                prop_assert!(ready.windows(2).all(|pair| pair[0] < pair[1]));
                for next in ready {
                    for (before,after,_) in &edges {
                        if *after == next { prop_assert!(completed.contains(before)); }
                    }
                    schedule.admit(next).unwrap();
                    prop_assert!(schedule.admit(next).is_err());
                    schedule.complete(next).unwrap();
                    prop_assert!(completed.insert(next));
                }
            }
            prop_assert_eq!(completed.len(), count);
        }
    }
}
