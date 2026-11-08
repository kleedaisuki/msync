//! A deterministic, edge-driven ready frontier for one interpreted basic block.

use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::fmt;
use std::ops::Bound::{Excluded, Unbounded};

use crate::hir::InstructionId;
use crate::hir::graph::{PlanGraph, Wait};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum State {
    Pending,
    Running,
    Complete,
}

struct Node {
    state: State,
    unresolved: usize,
    incoming: Vec<usize>,
    outgoing: Vec<usize>,
}

struct Edge {
    before: usize,
    after: usize,
    admitted: bool,
    captured_read: bool,
    satisfied: bool,
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
    positions: BTreeMap<InstructionId, usize>,
    nodes: Vec<Node>,
    edges: Vec<Edge>,
    ready: BTreeSet<usize>,
    pending: usize,
    complete: usize,
}

impl BlockSchedule {
    pub fn ready_count(&self) -> usize {
        self.ready.len()
    }

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
        for (index, edge) in graph.dependencies.iter().enumerate() {
            schedule.edges[index].captured_read =
                !edge.reasons.is_empty() && edge.reasons.iter().all(captured_read_reason);
        }
        Ok(schedule)
    }

    fn from_parts(
        order: Vec<InstructionId>,
        edges: impl IntoIterator<Item = (InstructionId, InstructionId, bool)>,
    ) -> Result<Self, ScheduleError> {
        let positions: BTreeMap<_, _> = order
            .iter()
            .enumerate()
            .map(|(index, id)| (*id, index))
            .collect();
        if positions.len() != order.len() {
            return Err(ScheduleError(
                "basic block contains duplicate instruction IDs".into(),
            ));
        }
        let mut nodes: Vec<_> = order
            .iter()
            .map(|_| Node {
                state: State::Pending,
                unresolved: 0,
                incoming: Vec::new(),
                outgoing: Vec::new(),
            })
            .collect();
        let mut indexed = Vec::new();
        for (before, after, admitted) in edges {
            let (Some(&before), Some(&after)) = (positions.get(&before), positions.get(&after))
            else {
                return Err(ScheduleError(
                    "dependency is not between distinct nodes of this block".into(),
                ));
            };
            if before == after {
                return Err(ScheduleError(
                    "dependency is not between distinct nodes of this block".into(),
                ));
            }
            let index = indexed.len();
            nodes[before].outgoing.push(index);
            nodes[after].incoming.push(index);
            nodes[after].unresolved += 1;
            indexed.push(Edge {
                before,
                after,
                admitted,
                captured_read: false,
                satisfied: false,
            });
        }
        let ready = nodes
            .iter()
            .enumerate()
            .filter_map(|(index, node)| (node.unresolved == 0).then_some(index))
            .collect();
        let schedule = Self {
            pending: order.len(),
            complete: 0,
            order,
            positions,
            nodes,
            edges: indexed,
            ready,
        };
        schedule.check_acyclic()?;
        Ok(schedule)
    }

    fn check_acyclic(&self) -> Result<(), ScheduleError> {
        let mut counts: Vec<_> = self.nodes.iter().map(|node| node.unresolved).collect();
        let mut ready: VecDeque<_> = self.ready.iter().copied().collect();
        let mut visited = 0;
        while let Some(index) = ready.pop_front() {
            visited += 1;
            for &edge in &self.nodes[index].outgoing {
                let after = self.edges[edge].after;
                counts[after] -= 1;
                if counts[after] == 0 {
                    ready.push_back(after);
                }
            }
        }
        if visited != self.order.len() {
            return Err(ScheduleError(
                "basic block dependency graph contains a cycle".into(),
            ));
        }
        Ok(())
    }

    pub fn ready_position(&self, id: InstructionId) -> Option<usize> {
        self.positions.get(&id).copied().filter(|&index| {
            self.nodes[index].state == State::Pending && self.nodes[index].unresolved == 0
        })
    }

    pub fn next_ready(&self, after: Option<usize>) -> Option<(usize, InstructionId)> {
        let position = match after {
            Some(index) => self.ready.range((Excluded(index), Unbounded)).next(),
            None => self.ready.first(),
        }?;
        Some((*position, self.order[*position]))
    }

    #[cfg(test)]
    pub fn ready(&self) -> Vec<InstructionId> {
        self.ready.iter().map(|&index| self.order[index]).collect()
    }

    fn change_state(&mut self, index: usize, state: State) {
        match self.nodes[index].state {
            State::Pending => self.pending -= 1,
            State::Complete => self.complete -= 1,
            State::Running => {}
        }
        self.ready.remove(&index);
        self.nodes[index].state = state;
        match state {
            State::Pending => {
                self.pending += 1;
                if self.nodes[index].unresolved == 0 {
                    self.ready.insert(index);
                }
            }
            State::Complete => self.complete += 1,
            State::Running => {}
        }
    }

    fn satisfy(&mut self, index: usize, satisfied: bool) {
        let edge = &mut self.edges[index];
        if edge.satisfied == satisfied {
            return;
        }
        edge.satisfied = satisfied;
        let after = edge.after;
        if satisfied {
            self.nodes[after].unresolved -= 1;
        } else {
            self.nodes[after].unresolved += 1;
        }
        if self.nodes[after].state != State::Pending {
            return;
        }
        if self.nodes[after].unresolved == 0 {
            self.ready.insert(after);
        } else {
            self.ready.remove(&after);
        }
    }

    fn release(&mut self, index: usize) {
        for offset in 0..self.nodes[index].outgoing.len() {
            let edge = self.nodes[index].outgoing[offset];
            let satisfied = match self.nodes[index].state {
                State::Complete => true,
                State::Running => self.edges[edge].admitted,
                State::Pending => false,
            };
            self.satisfy(edge, satisfied);
        }
    }

    pub fn admit(&mut self, id: InstructionId) -> Result<(), ScheduleError> {
        let Some(index) = self.ready_position(id) else {
            return Err(ScheduleError(format!(
                "instruction {} is not ready for admission",
                id.0
            )));
        };
        self.change_state(index, State::Running);
        self.release(index);
        Ok(())
    }

    pub fn complete(&mut self, id: InstructionId) -> Result<(), ScheduleError> {
        let Some(&index) = self
            .positions
            .get(&id)
            .filter(|&&index| self.nodes[index].state == State::Running)
        else {
            return Err(ScheduleError(format!(
                "instruction {} was not admitted",
                id.0
            )));
        };
        self.change_state(index, State::Complete);
        self.release(index);
        Ok(())
    }

    /// Called only after runtime acquisition owns an immutable source snapshot.
    pub fn captured_input(&mut self, id: InstructionId) -> Result<(), ScheduleError> {
        let Some(&index) = self
            .positions
            .get(&id)
            .filter(|&&index| self.nodes[index].state == State::Running)
        else {
            return Err(ScheduleError(
                "captured input belongs to an unadmitted instruction".into(),
            ));
        };
        for offset in 0..self.nodes[index].outgoing.len() {
            let edge = self.nodes[index].outgoing[offset];
            if self.edges[edge].captured_read {
                self.edges[edge].admitted = true;
                self.satisfy(edge, true);
            }
        }
        Ok(())
    }

    fn members(&self, ids: &[InstructionId], state: State) -> Option<BTreeSet<usize>> {
        if ids.is_empty() {
            return None;
        }
        let mut members = BTreeSet::new();
        for id in ids {
            let &index = self.positions.get(id)?;
            if self.nodes[index].state != state || !members.insert(index) {
                return None;
            }
        }
        Some(members)
    }

    fn coadmission(&self, members: &BTreeSet<usize>) -> bool {
        members.iter().all(|&index| {
            self.nodes[index].incoming.iter().all(|&edge| {
                self.edges[edge].satisfied || members.contains(&self.edges[edge].before)
            })
        })
    }

    /// The caller proves the rewrite; this frontier checks its external gates.
    pub fn group_ready(&self, ids: &[InstructionId]) -> bool {
        let Some(members) = self.members(ids, State::Pending) else {
            return false;
        };
        self.coadmission(&members)
            && !members.iter().any(|&index| {
                self.nodes[index].outgoing.iter().any(|&edge| {
                    self.edges[edge].admitted && !members.contains(&self.edges[edge].after)
                })
            })
    }

    /// Connected physical work must reserve together after external prerequisites.
    pub fn coadmission_ready(&self, ids: &[InstructionId]) -> bool {
        self.members(ids, State::Pending)
            .is_some_and(|members| self.coadmission(&members))
    }

    pub fn admit_group(&mut self, ids: &[InstructionId]) -> Result<(), ScheduleError> {
        if !self.group_ready(ids) {
            return Err(ScheduleError(
                "instruction group is not ready for admission".into(),
            ));
        }
        let members = self.members(ids, State::Pending).expect("validated group");
        for &index in &members {
            self.change_state(index, State::Running);
        }
        for index in members {
            self.release(index);
        }
        Ok(())
    }

    pub fn complete_group(&mut self, ids: &[InstructionId]) -> Result<(), ScheduleError> {
        self.transition_group(ids, State::Complete)
    }

    /// Only a failed speculative guard with no observable effects may retry.
    pub fn retry_group(&mut self, ids: &[InstructionId]) -> Result<(), ScheduleError> {
        let Some(members) = self.members(ids, State::Running) else {
            return Err(ScheduleError(
                "instruction group was not completely admitted".into(),
            ));
        };
        if members.iter().any(|&index| {
            self.nodes[index].outgoing.iter().any(|&edge| {
                let edge = &self.edges[edge];
                edge.admitted
                    && !members.contains(&edge.after)
                    && self.nodes[edge.after].state != State::Pending
            })
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
        let Some(members) = self.members(ids, State::Running) else {
            return Err(ScheduleError(
                "instruction group was not completely admitted".into(),
            ));
        };
        for &index in &members {
            self.change_state(index, state);
        }
        for index in members {
            self.release(index);
        }
        Ok(())
    }

    pub fn is_finished(&self) -> bool {
        self.complete == self.nodes.len()
    }
    pub fn all_admitted(&self) -> bool {
        self.pending == 0
    }
    pub fn is_running(&self, id: InstructionId) -> bool {
        self.positions
            .get(&id)
            .is_some_and(|&index| self.nodes[index].state == State::Running)
    }
    #[cfg(test)]
    pub fn running(&self) -> usize {
        self.nodes.len() - self.pending - self.complete
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
    struct Reference {
        order: Vec<InstructionId>,
        states: BTreeMap<InstructionId, State>,
        incoming: BTreeMap<InstructionId, Vec<(InstructionId, bool, bool)>>,
    }

    impl Reference {
        fn new(
            order: Vec<InstructionId>,
            edges: &[(InstructionId, InstructionId, bool, bool)],
        ) -> Self {
            let mut incoming = BTreeMap::<_, Vec<_>>::new();
            for &(before, after, admitted, captured) in edges {
                incoming
                    .entry(after)
                    .or_default()
                    .push((before, admitted, captured));
            }
            let states = order.iter().map(|id| (*id, State::Pending)).collect();
            Self {
                order,
                states,
                incoming,
            }
        }
        fn satisfied(&self, before: InstructionId, admitted: bool) -> bool {
            self.states[&before] == State::Complete
                || admitted && self.states[&before] == State::Running
        }
        fn ready(&self) -> Vec<InstructionId> {
            self.order
                .iter()
                .copied()
                .filter(|id| {
                    self.states[id] == State::Pending
                        && self
                            .incoming
                            .get(id)
                            .into_iter()
                            .flatten()
                            .all(|&(before, admitted, _)| self.satisfied(before, admitted))
                })
                .collect()
        }
        fn members(&self, ids: &[InstructionId], state: State) -> Option<BTreeSet<InstructionId>> {
            let members: BTreeSet<_> = ids.iter().copied().collect();
            (!members.is_empty()
                && members.len() == ids.len()
                && members.iter().all(|id| self.states.get(id) == Some(&state)))
            .then_some(members)
        }
        fn coadmission(&self, ids: &[InstructionId]) -> bool {
            let Some(members) = self.members(ids, State::Pending) else {
                return false;
            };
            members.iter().all(|id| {
                self.incoming
                    .get(id)
                    .into_iter()
                    .flatten()
                    .all(|&(before, admitted, _)| {
                        members.contains(&before) || self.satisfied(before, admitted)
                    })
            })
        }
        fn group_ready(&self, ids: &[InstructionId]) -> bool {
            let members: BTreeSet<_> = ids.iter().copied().collect();
            self.coadmission(ids)
                && !self.incoming.iter().any(|(after, incoming)| {
                    !members.contains(after)
                        && incoming
                            .iter()
                            .any(|&(before, admitted, _)| admitted && members.contains(&before))
                })
        }
        fn apply(&mut self, op: u8, ids: &[InstructionId]) -> bool {
            let first = ids[0];
            match op {
                0 if self.ready().contains(&first) => {
                    self.states.insert(first, State::Running);
                }
                1 if self.states.get(&first) == Some(&State::Running) => {
                    self.states.insert(first, State::Complete);
                }
                2 if self.states.get(&first) == Some(&State::Running) => {
                    for incoming in self.incoming.values_mut() {
                        for (before, admitted, captured) in incoming {
                            if *before == first && *captured {
                                *admitted = true;
                            }
                        }
                    }
                }
                3 if self.group_ready(ids) => {
                    for id in ids {
                        self.states.insert(*id, State::Running);
                    }
                }
                4 if self.members(ids, State::Running).is_some() => {
                    for id in ids {
                        self.states.insert(*id, State::Complete);
                    }
                }
                5 if self.members(ids, State::Running).is_some() => {
                    let members: BTreeSet<_> = ids.iter().copied().collect();
                    if self.incoming.iter().any(|(after, incoming)| {
                        !members.contains(after)
                            && self.states[after] != State::Pending
                            && incoming
                                .iter()
                                .any(|&(before, admitted, _)| admitted && members.contains(&before))
                    }) {
                        return false;
                    }
                    for id in ids {
                        self.states.insert(*id, State::Pending);
                    }
                }
                _ => return false,
            }
            true
        }
    }

    fn apply(schedule: &mut BlockSchedule, op: u8, ids: &[InstructionId]) -> bool {
        match op {
            0 => schedule.admit(ids[0]),
            1 => schedule.complete(ids[0]),
            2 => schedule.captured_input(ids[0]),
            3 => schedule.admit_group(ids),
            4 => schedule.complete_group(ids),
            5 => schedule.retry_group(ids),
            _ => unreachable!(),
        }
        .is_ok()
    }

    fn assert_frontier(schedule: &BlockSchedule, reference: &Reference) {
        assert_eq!(schedule.ready(), reference.ready());
        assert_eq!(
            schedule.is_finished(),
            reference
                .states
                .values()
                .all(|state| *state == State::Complete)
        );
        assert_eq!(
            schedule.all_admitted(),
            reference
                .states
                .values()
                .all(|state| *state != State::Pending)
        );
        assert_eq!(
            schedule.running(),
            reference
                .states
                .values()
                .filter(|state| **state == State::Running)
                .count()
        );
        for (index, node) in schedule.nodes.iter().enumerate() {
            assert_eq!(
                node.unresolved,
                node.incoming
                    .iter()
                    .filter(|&&edge| !schedule.edges[edge].satisfied)
                    .count()
            );
            assert_eq!(
                schedule.ready.contains(&index),
                node.state == State::Pending && node.unresolved == 0
            );
        }
        for edge in &schedule.edges {
            let state = schedule.nodes[edge.before].state;
            assert_eq!(
                edge.satisfied,
                state == State::Complete || state == State::Running && edge.admitted
            );
        }
        let mut cursor = None;
        let mut ready = Vec::new();
        while let Some((position, instruction)) = schedule.next_ready(cursor) {
            ready.push(instruction);
            cursor = Some(position);
        }
        assert_eq!(ready, reference.ready());
    }

    #[test]
    fn retry_revokes_an_unconsumed_admission_gate() {
        let mut schedule =
            BlockSchedule::from_parts(vec![id(9), id(2)], [(id(9), id(2), true)]).unwrap();
        schedule.admit(id(9)).unwrap();
        assert_eq!(schedule.ready(), vec![id(2)]);
        schedule.retry_group(&[id(9)]).unwrap();
        assert_eq!(schedule.ready(), vec![id(9)]);
        schedule.admit(id(9)).unwrap();
        assert_eq!(schedule.ready(), vec![id(2)]);
        schedule.complete(id(9)).unwrap();
        assert_eq!(schedule.nodes[1].unresolved, 0);
    }

    proptest! {
        #[test]
        fn indexed_frontier_matches_scan_reference(
            count in 1usize..25,
            reverse in any::<bool>(),
            candidates in prop::collection::vec((0usize..25, 0usize..25, any::<bool>(), any::<bool>()), 0..100),
            actions in prop::collection::vec((0u8..6, 0usize..27, 0usize..27, any::<bool>()), 0..250),
        ) {
            let mut order: Vec<_> = (0..count).map(|n| id(7 * n + 3)).collect();
            if reverse { order.reverse(); }
            let edges: Vec<_> = candidates.into_iter().filter(|(before, after, _, _)| before < after && *after < count).map(|(before, after, admitted, captured)| (order[before], order[after], admitted, captured)).collect();
            let mut schedule = BlockSchedule::from_parts(order.clone(), edges.iter().map(|&(before, after, admitted, _)| (before, after, admitted))).unwrap();
            for (index, &(_, _, _, captured)) in edges.iter().enumerate() { schedule.edges[index].captured_read = captured; }
            let mut reference = Reference::new(order.clone(), &edges);
            for (op, first, second, singleton) in actions {
                let lookup = |index| order.get(index).copied().unwrap_or(id(1000 + index));
                let mut ids = vec![lookup(first), lookup(second)];
                if singleton { ids.truncate(1); }
                prop_assert_eq!(schedule.coadmission_ready(&ids), reference.coadmission(&ids));
                prop_assert_eq!(schedule.group_ready(&ids), reference.group_ready(&ids));
                prop_assert_eq!(apply(&mut schedule, op, &ids), reference.apply(op, &ids));
                assert_frontier(&schedule, &reference);
            }
            while !schedule.is_finished() {
                let running = order.iter().copied().find(|id| schedule.is_running(*id));
                let (op, next) = running.map(|id| (1, id)).unwrap_or_else(|| (0, schedule.ready()[0]));
                prop_assert!(apply(&mut schedule, op, &[next]));
                prop_assert!(reference.apply(op, &[next]));
                assert_frontier(&schedule, &reference);
            }
        }
    }
}
