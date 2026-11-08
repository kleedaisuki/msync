//! Frontier-only paired measurements; compiled by bench-block-frontier.sh.
#![allow(dead_code)]

use std::collections::VecDeque;
use std::hint::black_box;
use std::time::{Duration, Instant};

mod hir {
    #[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
    pub struct InstructionId(pub usize);
    pub mod effects {
        #[derive(PartialEq)]
        pub enum AccessKind {
            Read,
            Write,
        }
        pub enum Resource {
            File(()),
        }
        pub struct Access {
            pub kind: AccessKind,
            pub resource: Resource,
        }
        pub struct Conflict {
            pub before: Access,
        }
    }
    pub mod graph {
        use super::{InstructionId, effects::Conflict};
        pub enum Wait {
            Admitted,
            ProcessStarted(usize),
            Completion,
        }
        pub enum Reason {
            Hazard(Box<Conflict>),
        }
        pub struct Dependency {
            pub before: InstructionId,
            pub after: InstructionId,
            pub wait: Wait,
            pub reasons: Vec<Reason>,
        }
        pub struct Node {
            pub instruction: InstructionId,
        }
        pub struct Block {
            pub instructions: Vec<InstructionId>,
        }
        pub struct PlanGraph {
            pub nodes: Vec<Node>,
            pub blocks: Vec<Block>,
            pub dependencies: Vec<Dependency>,
        }
    }
}

use hir::InstructionId;

type Edge = (InstructionId, InstructionId, bool);

trait Frontier: Sized {
    fn new(order: Vec<InstructionId>, edges: Vec<Edge>) -> Self;
    fn ready_batch(&self, limit: usize) -> Vec<InstructionId>;
    fn admit_one(&mut self, id: InstructionId);
    fn complete_one(&mut self, id: InstructionId);
    fn finished(&self) -> bool;
}

mod before {
    include!(env!("MSYNC_FRONTIER_BEFORE"));
    impl crate::Frontier for BlockSchedule {
        fn new(order: Vec<InstructionId>, edges: Vec<crate::Edge>) -> Self {
            Self::from_parts(order, edges).unwrap()
        }
        fn ready_batch(&self, limit: usize) -> Vec<InstructionId> {
            self.ready().into_iter().take(limit).collect()
        }
        fn admit_one(&mut self, id: InstructionId) {
            self.admit(id).unwrap();
        }
        fn complete_one(&mut self, id: InstructionId) {
            self.complete(id).unwrap();
        }
        fn finished(&self) -> bool {
            self.is_finished()
        }
    }
}

mod after {
    include!(env!("MSYNC_FRONTIER_AFTER"));
    impl crate::Frontier for BlockSchedule {
        fn new(order: Vec<InstructionId>, edges: Vec<crate::Edge>) -> Self {
            Self::from_parts(order, edges).unwrap()
        }
        fn ready_batch(&self, limit: usize) -> Vec<InstructionId> {
            let mut cursor = None;
            let mut ready = Vec::new();
            while ready.len() < limit {
                let Some((position, id)) = self.next_ready(cursor) else {
                    break;
                };
                cursor = Some(position);
                ready.push(id);
            }
            ready
        }
        fn admit_one(&mut self, id: InstructionId) {
            self.admit(id).unwrap();
        }
        fn complete_one(&mut self, id: InstructionId) {
            self.complete(id).unwrap();
        }
        fn finished(&self) -> bool {
            self.is_finished()
        }
    }
}

fn measure<S: Frontier>(count: usize, shape: &str) -> (Duration, Duration) {
    let order: Vec<_> = (0..count)
        .map(|index| InstructionId(7 * index + 3))
        .collect();
    let edges: Vec<_> = match shape {
        "independent" => Vec::new(),
        "chain" => (1..count)
            .map(|index| (order[index - 1], order[index], false))
            .collect(),
        "fanout" => (1..count)
            .map(|index| (order[0], order[index], false))
            .collect(),
        _ => unreachable!(),
    };
    let started = Instant::now();
    let mut schedule = S::new(order, edges);
    let construction = started.elapsed();
    let started = Instant::now();
    let mut running = VecDeque::new();
    let mut completed = 0usize;
    let mut checksum = 0usize;
    while !schedule.finished() {
        for id in schedule.ready_batch(4 - running.len()) {
            schedule.admit_one(id);
            running.push_back(id);
        }
        let next = running
            .pop_front()
            .expect("acyclic frontier makes progress");
        schedule.complete_one(next);
        completed += 1;
        checksum += next.0;
    }
    let scheduling = started.elapsed();
    assert_eq!(completed, count);
    assert_eq!(black_box(checksum), 7 * count * (count - 1) / 2 + 3 * count);
    (construction, scheduling)
}

fn median(samples: &mut [Duration]) -> f64 {
    samples.sort_unstable();
    samples[samples.len() / 2].as_secs_f64() * 1000.0
}

fn main() {
    let args: Vec<_> = std::env::args().skip(1).collect();
    let sizes = args.first().map(String::as_str).unwrap_or("100,1000,10000");
    let samples = args
        .get(1)
        .map(|value| value.parse::<usize>().unwrap())
        .unwrap_or(7);
    assert!(samples > 0);
    println!(
        "# actual before/after BlockSchedule source; optimized rustc; four simulated workers; two warmups each; alternating AB/BA samples; construction excluded from frontier timing; no filesystem/executor throughput claim"
    );
    println!(
        "shape,nodes,before_init_ms,after_init_ms,before_frontier_ms,after_frontier_ms,before_min_ms,before_max_ms,after_min_ms,after_max_ms"
    );
    for shape in ["independent", "chain", "fanout"] {
        for count in sizes
            .split(',')
            .map(|value| value.parse::<usize>().unwrap())
        {
            assert!(count > 0);
            for _ in 0..2 {
                measure::<before::BlockSchedule>(count, shape);
                measure::<after::BlockSchedule>(count, shape);
            }
            let mut baseline = Vec::new();
            let mut current = Vec::new();
            for sample in 0..samples {
                if sample % 2 == 0 {
                    baseline.push(measure::<before::BlockSchedule>(count, shape));
                    current.push(measure::<after::BlockSchedule>(count, shape));
                } else {
                    current.push(measure::<after::BlockSchedule>(count, shape));
                    baseline.push(measure::<before::BlockSchedule>(count, shape));
                }
            }
            let (mut before_init, mut before): (Vec<_>, Vec<_>) = baseline.into_iter().unzip();
            let (mut after_init, mut after): (Vec<_>, Vec<_>) = current.into_iter().unzip();
            let before_median = median(&mut before);
            let after_median = median(&mut after);
            println!(
                "{shape},{count},{:.6},{:.6},{before_median:.6},{after_median:.6},{:.6},{:.6},{:.6},{:.6}",
                median(&mut before_init),
                median(&mut after_init),
                before[0].as_secs_f64() * 1000.0,
                before.last().unwrap().as_secs_f64() * 1000.0,
                after[0].as_secs_f64() * 1000.0,
                after.last().unwrap().as_secs_f64() * 1000.0
            );
        }
    }
}
