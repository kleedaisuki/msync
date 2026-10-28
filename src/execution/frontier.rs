//! Cooperative named-region continuations over one authoritative runtime owner.

use super::*;

struct Frame<'a> {
    pending: Vec<&'a Instruction>,
    boundary: Option<(&'a Instruction, WorkNode)>,
}

impl<'a> Frame<'a> {
    fn root(region: &'a Region) -> Self {
        Self {
            pending: region.instructions.iter().collect(),
            boundary: None,
        }
    }

    fn owner(&self) -> Option<TransactionId> {
        self.boundary.as_ref().map(|(_, work)| work.transaction)
    }

    fn span(&self) -> Span {
        self.pending
            .first()
            .map(|node| node.span)
            .or_else(|| self.boundary.as_ref().map(|(node, _)| node.span))
            .unwrap_or_default()
    }
}

struct Selected {
    frame: usize,
    node: usize,
    work: WorkNode,
    candidate: Option<program::SharedView>,
}

#[derive(Default)]
struct Blocked {
    capacity: Option<(Span, Option<crate::source::InstructionOrigin>)>,
    owner: Option<(Span, Option<crate::source::InstructionOrigin>)>,
}

impl RuntimeSession {
    pub(super) async fn region(
        &mut self,
        region: &Region,
        current: Option<TransactionId>,
        program: &hir::Program,
    ) -> Result<()> {
        debug_assert!(
            current.is_none(),
            "named regions are frontier frames, not recursive calls"
        );
        let prior = self.engine_mut()?.manager.active_transaction_ids();
        match self.run_frontier(region, program).await {
            Ok(()) => Ok(()),
            Err(mut failure) => {
                let engine = self.engine.as_ref().expect("frontier has its manager");
                let named = engine.named.values().copied().collect::<BTreeSet<_>>();
                let active = engine.manager.active_transaction_ids();
                let abort = active
                    .difference(&prior)
                    .filter(|owner| named.contains(owner))
                    .copied()
                    .collect::<Vec<_>>();
                for owner in abort {
                    self.cancel_transaction(owner, &mut failure).await;
                    self.engine_mut()?.abort(owner, &mut failure);
                }
                Err(failure)
            }
        }
    }

    async fn run_frontier(&mut self, region: &Region, program: &hir::Program) -> Result<()> {
        let mut frames = vec![Frame::root(region)];
        let mut cursor = 0;
        loop {
            if self.cancel.is_cancelled() {
                return Err(ExecutionError::cancelled(
                    "execution cancelled",
                    frames[0].span(),
                ));
            }
            if self.finish_named_frame(&mut frames).await? {
                continue;
            }
            let pending = frames.iter().any(|frame| !frame.pending.is_empty());
            let named = frames.len() > 1;
            let finite = self.has_materializations();
            if !pending && !named && !finite {
                return Ok(());
            }
            let available = self.admission.available();
            let mut blocked = Blocked::default();
            if let Some(selected) = self.select_frontier(&frames, cursor, &mut blocked).await? {
                let node = frames[selected.frame].pending.remove(selected.node);
                let owner = frames[selected.frame].owner();
                cursor = (selected.frame + 1) % frames.len();
                let work = self.activate_selected(selected);
                if let InstructionKind::Transaction { body, .. } = &node.kind {
                    let work = self.begin_named(node, program)?;
                    frames.push(Frame {
                        pending: body.instructions.iter().collect(),
                        boundary: Some((node, work)),
                    });
                    continue;
                }
                let origin = work.plan.origin(node.id).cloned();
                self.observe_instruction(node);
                self.instruction(node, owner, program)
                    .await
                    .map_err(|error| error.with_origin(origin.as_ref()))?;
                self.complete_immediate(node, owner, &work)?;
                continue;
            }
            let before = self.progress;
            self.reap_physical().await?;
            if before != self.progress || available != self.admission.available() {
                continue;
            }
            if !self.complete_submission && !named && pending && !finite {
                return self.interactive_blocked(&frames, blocked);
            }
            self.close_unconnected_inputs(&frames);
            if let Some((span, origin)) = blocked.owner {
                return Err(ExecutionError::new("a live program owner retains the capacity needed by its pending input", span)
                    .help("Connect program input before opening a dependent output drain, or increase --max-streams/--memory-limit. Waiting cannot admit this input while its own owner retains those resources.").with_origin(origin.as_ref()));
            }
            if self.flows.is_empty() && self.live_processes().is_empty() {
                return Err(ExecutionError::new(
                    "the executable event graph has no producer for its remaining prerequisites",
                    frames
                        .iter()
                        .find(|frame| !frame.pending.is_empty())
                        .map_or(Span::default(), Frame::span),
                ));
            }
            self.wait_event().await?;
        }
    }

    async fn select_frontier(
        &mut self,
        frames: &[Frame<'_>],
        cursor: usize,
        blocked: &mut Blocked,
    ) -> Result<Option<Selected>> {
        for offset in 0..frames.len() {
            let index = (cursor + offset) % frames.len();
            let frame = &frames[index];
            for (position, node) in frame.pending.iter().enumerate() {
                if !self.ports_available(node) {
                    continue;
                }
                if matches!(node.kind, InstructionKind::Commit { .. })
                    && let Some(owner) = frame.owner()
                {
                    self.close_frame_inputs(frame, position);
                    if !self.is_ready(owner) {
                        continue;
                    }
                }
                let (mut work, candidate) = self.prepare_work(node, frame.owner())?;
                let start_ready = self.gate(&work, Phase::Start)?;
                let pin_input = self.may_pin_input(node, &work)?;
                if !start_ready && !pin_input {
                    self.discard_candidate(work.key, candidate, node.span)?;
                    continue;
                }
                let demand = self.demand(node, &work);
                match self.admission.availability(demand) {
                    Availability::Ready => {}
                    Availability::Impossible => self.admission.feasible(demand, node.span)?,
                    Availability::Busy => {
                        let site = (node.span, work.plan.origin(node.id).cloned());
                        blocked.capacity.get_or_insert_with(|| site.clone());
                        if self.owner_capacity_blocked(node, &work, demand) {
                            blocked.owner.get_or_insert(site);
                        }
                        self.discard_candidate(work.key, candidate, node.span)?;
                        continue;
                    }
                }
                let reserved = if demand.streams > 0 {
                    self.reserve_flows(node.id, node.span)?
                } else {
                    Vec::new()
                };
                if pin_input {
                    self.plan = Some(work.plan.clone());
                    self.active_work = Some(work.clone());
                    let InstructionKind::Transfer { source, .. } = &node.kind else {
                        unreachable!()
                    };
                    let reader = self
                        .reader(node.id, source, work.transaction, node.span)
                        .await?;
                    self.prepared_readers.insert(work.key, reader);
                    work.plan = self.refresh_plan(Bindings::default())?;
                }
                if self.gate(&work, Phase::Start)? {
                    return Ok(Some(Selected {
                        frame: index,
                        node: position,
                        work,
                        candidate,
                    }));
                }
                for member in reserved {
                    self.flow_reservations.remove(&member);
                }
                self.discard_candidate(work.key, candidate, node.span)?;
            }
        }
        Ok(None)
    }

    fn activate_selected(&mut self, selected: Selected) -> WorkNode {
        let Selected {
            work, candidate, ..
        } = selected;
        self.plan = Some(work.plan.clone());
        self.remember_plan(work.plan.clone());
        self.node_transactions.insert(work.key, work.transaction);
        self.candidate = candidate;
        self.active_work = Some(work.clone());
        self.active_origin = work.plan.origin(work.key.instruction).cloned();
        work
    }

    fn begin_named(&mut self, node: &Instruction, program: &hir::Program) -> Result<WorkNode> {
        let InstructionKind::Transaction { transaction, body } = &node.kind else {
            unreachable!()
        };
        let isolation = program
            .transactions
            .iter()
            .find(|info| info.id == *transaction)
            .ok_or_else(|| ExecutionError::new("transaction metadata is missing", node.span))?
            .isolation;
        let mut work = self.work()?;
        self.engine_mut()?
            .manager
            .begin(work.transaction, isolation)
            .map_err(|cause| {
                ExecutionError::caused("cannot begin transaction", node.span, cause)
            })?;
        self.engine_mut()?
            .named
            .insert(*transaction, work.transaction);
        let owner = self.owner_key(work.transaction)?;
        let mut bindings = Bindings::default();
        for child in &body.instructions {
            bindings.nodes.insert(child.id, owner.clone());
            if let InstructionKind::Spawn { process, .. } = child.kind {
                bindings.processes.insert(process, owner.clone());
            }
        }
        work.plan = self.refresh_plan(bindings)?;
        Ok(work)
    }

    fn complete_immediate(
        &mut self,
        node: &Instruction,
        owner: Option<TransactionId>,
        work: &WorkNode,
    ) -> Result<()> {
        let deferred = matches!(node.kind, InstructionKind::Spawn { .. })
            || self.flows.iter().any(|flow| flow.work.key == work.key)
            || self
                .deferred
                .iter()
                .any(|assignment| assignment.work.key == work.key);
        if deferred {
            return Ok(());
        }
        self.event(Event::Node(work.key, NodeEvent::ServiceReady))?;
        self.logical_complete(work)?;
        if owner.is_none() || matches!(node.kind, InstructionKind::Commit { .. }) {
            self.owner_committed(work.transaction)?;
        }
        Ok(())
    }

    async fn finish_named_frame(&mut self, frames: &mut Vec<Frame<'_>>) -> Result<bool> {
        for index in 1..frames.len() {
            if !frames[index].pending.is_empty() {
                continue;
            }
            let (node, work) = frames[index]
                .boundary
                .as_ref()
                .expect("named frame has its boundary");
            let work = work.clone();
            self.close_owner_stdin(work.transaction);
            if !self.is_ready(work.transaction) {
                continue;
            }
            // A retained boundary is an immutable gate witness, not the latest
            // binding state. Restoring its older plan would unbind a sibling's
            // already-started children and misclassify own pending reads.
            self.active_work = Some(work.clone());
            self.active_origin = work.plan.origin(work.key.instruction).cloned();
            self.quiesce(work.transaction).await?;
            if !self.gate(&work, Phase::RegionCommit)? {
                continue;
            }
            self.engine_mut()?.finish(work.transaction, node.span)?;
            self.event(Event::Node(work.key, NodeEvent::ServiceReady))?;
            self.logical_complete(&work)?;
            self.owner_committed(work.transaction)?;
            frames.remove(index);
            return Ok(true);
        }
        Ok(false)
    }

    fn close_owner_stdin(&mut self, owner: TransactionId) {
        for program in self
            .programs
            .values_mut()
            .filter(|program| program.transaction == owner)
        {
            program.close_unused_stdin();
        }
    }

    fn close_frame_inputs(&mut self, frame: &Frame<'_>, controller: usize) {
        let Some(owner) = frame.owner() else { return };
        for program in self
            .programs
            .values_mut()
            .filter(|program| program.transaction == owner)
        {
            let pending = frame.pending[..controller].iter().any(|node| matches!(node.kind,
                InstructionKind::Transfer { destination: Output::Stdin(process), .. } if process == program.id));
            if !pending {
                program.close_unused_stdin();
            }
        }
    }

    fn close_unconnected_inputs(&mut self, frames: &[Frame<'_>]) {
        for program in self
            .programs
            .values_mut()
            .filter(|program| program.is_running())
        {
            let pending = frames.iter().flat_map(|frame| &frame.pending).any(|node| matches!(node.kind,
                InstructionKind::Transfer { destination: Output::Stdin(process), .. } if process == program.id));
            if !pending {
                program.close_unused_stdin();
            }
        }
    }

    fn interactive_blocked(&mut self, frames: &[Frame<'_>], blocked: Blocked) -> Result<()> {
        self.rejected_before_start = true;
        if let Some((span, origin)) = blocked.capacity {
            return Err(ExecutionError::new("concurrent resource capacity is occupied; no blocked operation started", span)
                .help("Use /wait to finish an existing finite job, /stop to release an open producer, or increase the concurrent resource limits before retrying.").with_origin(origin.as_ref()));
        }
        let fifo = frames.iter().flat_map(|frame| &frame.pending).any(|node| {
            self.native_fifo_pins
                .keys()
                .any(|key| key.instruction == node.id)
        });
        let help = if fifo {
            "If the FIFO peer is another msync statement, /stop the pending half and submit both sides together with /load or one macro call. /wait alone cannot create its missing peer. Otherwise connect the external FIFO reader/writer before retrying."
        } else {
            "Connect the required stdin, finish its producer with /wait, or cancel the conflicting job with /stop before retrying."
        };
        Err(ExecutionError::new("this submission is waiting for an owned resource producer; no blocked operation started", frames[0].span()).help(help))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::compiler::{CompileContext, structured::Session};
    use crate::hir::effects::OperationFacts;

    #[tokio::test]
    async fn settling_an_old_boundary_preserves_new_sibling_owner_bindings() {
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let directory = tempfile::tempdir_in(temporary).unwrap();
        let source =
            "transaction A:\n    \"A\" -> a\ntransaction B:\n    \"B\" -> b\n    b -> copied\n";
        let program = Session::new()
            .compile(
                &crate::language::parse(source).unwrap(),
                &CompileContext {
                    pwd: directory.path().to_owned(),
                    source_file: None,
                },
            )
            .unwrap();
        let a = &program.body.instructions[0];
        let b = &program.body.instructions[1];
        let InstructionKind::Transaction { body: a_body, .. } = &a.kind else {
            unreachable!()
        };
        let InstructionKind::Transaction { body: b_body, .. } = &b.kind else {
            unreachable!()
        };
        let mut context = hir::graph::PlanningContext::default();
        for child in a_body.instructions.iter().chain(&b_body.instructions) {
            context.operation_facts.insert(
                child.id,
                OperationFacts {
                    source: crate::plan::FileClass::Regular,
                    destination: crate::plan::FileClass::Regular,
                },
            );
        }
        let graph = hir::graph::build_with_context(&program, &context);
        let mut runtime =
            RuntimeSession::new(&Options::new(directory.path().join("scratch"))).unwrap();
        runtime.enroll(&Domain::default()).unwrap();
        let a_owner = TransactionId(0);
        let b_owner = TransactionId(1);
        runtime
            .engine_mut()
            .unwrap()
            .manager
            .begin(a_owner, Isolation::Serializable)
            .unwrap();
        runtime
            .engine_mut()
            .unwrap()
            .manager
            .begin(b_owner, Isolation::Serializable)
            .unwrap();
        runtime.engine_mut().unwrap().named.insert(a_owner, a_owner);
        runtime.engine_mut().unwrap().named.insert(b_owner, b_owner);
        let submission = runtime.ledger.next_submission().unwrap();
        let owner = runtime.owner_key(a_owner).unwrap();
        let old = SubmissionPlan::bind(
            submission,
            Arc::new(program.clone()),
            crate::source::Origins::default(),
            context.clone(),
            graph.clone(),
            Bindings {
                nodes: BTreeMap::from([(a.id, owner.clone()), (a_body.instructions[0].id, owner)]),
                ..Bindings::default()
            },
        )
        .unwrap();
        let owner = runtime.owner_key(b_owner).unwrap();
        let latest = old
            .rebind(
                context,
                graph,
                Bindings {
                    nodes: std::iter::once((b.id, owner.clone()))
                        .chain(
                            b_body
                                .instructions
                                .iter()
                                .map(|node| (node.id, owner.clone())),
                        )
                        .collect(),
                    ..Bindings::default()
                },
            )
            .unwrap();
        runtime.plan = Some(latest.clone());
        let key = old.node_key(a.id).unwrap();
        let boundary = WorkNode {
            plan: old,
            key,
            order: PublicationOrder(0),
            transaction: a_owner,
            physical: None,
            materialization: None,
        };
        let child = a_body.instructions[0].id;
        runtime
            .event(Event::Node(
                NodeKey {
                    submission,
                    instruction: child,
                },
                NodeEvent::LogicalComplete,
            ))
            .unwrap();
        let mut frames = vec![
            Frame {
                pending: Vec::new(),
                boundary: None,
            },
            Frame {
                pending: Vec::new(),
                boundary: Some((a, boundary)),
            },
        ];
        assert!(runtime.finish_named_frame(&mut frames).await.unwrap());
        assert!(Arc::ptr_eq(runtime.plan.as_ref().unwrap(), &latest));
        let dependency = NodeKey {
            submission,
            instruction: b_body.instructions[0].id,
        };
        let gate = runtime
            .ledger
            .gate(
                runtime.plan.as_ref().unwrap(),
                b_body.instructions[1].id,
                Phase::Start,
            )
            .unwrap();
        let GateState::Pending(events) = gate else {
            panic!("B must wait for its own producer")
        };
        assert!(events.contains(&Event::Node(dependency, NodeEvent::LogicalComplete)));
        assert!(!events.contains(&Event::Node(dependency, NodeEvent::Committed)));
        runtime.shutdown().await.unwrap();
    }
}
