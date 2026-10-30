//! Independent copy, program and stream lanes with one payload-buffer budget.

use std::future::{Future, poll_fn};
use std::sync::Arc;

use tokio::sync::{OwnedSemaphorePermit, Semaphore};

use crate::scheduler::ResourceLimits;
use crate::syntax::Span;

use super::error::{ExecutionError, Result};

pub(super) struct Admission {
    copies: Arc<Semaphore>,
    programs: Arc<Semaphore>,
    streams: Arc<Semaphore>,
    buffers: Arc<Semaphore>,
    buffer_bytes: usize,
    capacity: Demand,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(super) struct Demand {
    pub copies: usize,
    pub programs: usize,
    pub streams: usize,
    pub buffers: usize,
}

impl Demand {
    pub const COPY: Self = Self {
        copies: 1,
        programs: 0,
        streams: 0,
        buffers: 1,
    };
    #[cfg(test)]
    pub const PROGRAM: Self = Self {
        copies: 0,
        programs: 1,
        streams: 0,
        buffers: 6,
    };

    pub fn flows(count: usize) -> Self {
        Self {
            copies: 0,
            programs: 0,
            streams: count,
            buffers: count.saturating_mul(2),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Availability {
    Ready,
    Busy,
    Impossible,
}

pub(super) struct Lease {
    _slot: OwnedSemaphorePermit,
    _memory: OwnedSemaphorePermit,
    context: Option<crate::telemetry::TraceContext>,
}

impl Lease {
    pub fn context(&self) -> Option<&crate::telemetry::TraceContext> {
        self.context.as_ref()
    }
    pub fn task(&self, role: &'static str) -> TaskTrace {
        let mut span = crate::telemetry::linked_span("task", self.context.as_ref());
        span.expect_completion();
        span.attribute("operation", role);
        TaskTrace { span }
    }
}

pub(super) struct TaskTrace {
    span: crate::telemetry::LinkedSpan,
}

impl TaskTrace {
    pub async fn run<T>(mut self, future: impl Future<Output = Result<T>>) -> Result<T> {
        let context = self.span.context();
        let mut future = std::pin::pin!(future);
        // TLS attachments belong to one poll, never to the suspended task or
        // the worker thread which may poll a different task immediately after.
        let result = poll_fn(|cx| {
            crate::telemetry::with_context(context.as_ref(), || future.as_mut().poll(cx))
        })
        .await;
        finish_task(&mut self.span, &result, false);
        result
    }
}

pub(super) fn finish_task<T>(
    span: &mut crate::telemetry::LinkedSpan,
    result: &Result<T>,
    completed_cancelled: bool,
) {
    let cancelled = match result {
        Ok(_) => completed_cancelled,
        Err(cause) => cause.is_cancelled(),
    };
    if let Err(cause) = result
        && !cancelled
    {
        span.record_error("execution.task", cause);
    }
    if cancelled {
        span.cancelled();
    } else {
        span.finish_result(result);
    }
}

impl Admission {
    pub fn new(limits: &ResourceLimits) -> Result<Self> {
        limits
            .validate()
            .map_err(|message| ExecutionError::new(message, Span::default()))?;
        let units = limits.memory_bytes / limits.buffer_bytes;
        if [limits.jobs, limits.max_streams, units]
            .into_iter()
            .any(|value| value > Semaphore::MAX_PERMITS)
        {
            return Err(ExecutionError::new("configured concurrency exceeds the platform admission range", Span::default())
                .help("Use bounded jobs/streams and payload capacity instead of the maximum integer value."));
        }
        Ok(Self {
            copies: Arc::new(Semaphore::new(limits.jobs)),
            programs: Arc::new(Semaphore::new(limits.jobs)),
            streams: Arc::new(Semaphore::new(limits.max_streams)),
            buffers: Arc::new(Semaphore::new(units)),
            buffer_bytes: limits.buffer_bytes,
            capacity: Demand {
                copies: limits.jobs,
                programs: limits.jobs,
                streams: limits.max_streams,
                buffers: units,
            },
        })
    }

    pub fn availability(&self, demand: Demand) -> Availability {
        if !fits(demand, self.capacity) {
            return Availability::Impossible;
        }
        if fits(demand, self.available()) {
            Availability::Ready
        } else {
            Availability::Busy
        }
    }

    pub fn available(&self) -> Demand {
        Demand {
            copies: self.copies.available_permits(),
            programs: self.programs.available_permits(),
            streams: self.streams.available_permits(),
            buffers: self.buffers.available_permits(),
        }
    }

    pub fn feasible(&self, demand: Demand, span: Span) -> Result<()> {
        if fits(demand, self.capacity) {
            return Ok(());
        }
        Err(ExecutionError::new(format!(
            "this admission requires {} copy slots, {} program slots, {} stream slots and {} payload buffers; configured capacity is {}, {}, {} and {}",
            demand.copies, demand.programs, demand.streams, demand.buffers,
            self.capacity.copies, self.capacity.programs, self.capacity.streams, self.capacity.buffers,
        ), span).help("Increase --jobs/--max-streams/--memory-limit or reduce --buffer-size. Connected work must fit together; a running async program is never implicitly awaited to free capacity."))
    }

    pub fn program(&self, span: Span) -> Result<Lease> {
        self.acquire(&self.programs, 6, span, "program")
    }

    pub fn copy(&self, span: Span) -> Result<Lease> {
        self.acquire(&self.copies, 1, span, "finite materialization")
    }

    pub fn copy_availability(&self) -> Availability {
        self.availability(Demand::COPY)
    }

    pub fn flow(&self, span: Span) -> Result<Lease> {
        self.acquire(&self.streams, 2, span, "program stream")
    }

    fn acquire(&self, slots: &Arc<Semaphore>, units: u32, span: Span, role: &str) -> Result<Lease> {
        let parent = crate::telemetry::context();
        let mut trace = crate::telemetry::linked_span("schedule", parent.as_ref());
        trace.expect_completion();
        trace.attribute("resource", role);
        trace.metric("available_slots", slots.available_permits() as u64);
        trace.metric(
            "available_buffer_bytes",
            self.buffers
                .available_permits()
                .saturating_mul(self.buffer_bytes) as u64,
        );
        let result = (|| {
            let slot = slots.clone().try_acquire_owned().map_err(|_| {
            ExecutionError::new(format!("the ready {role} exceeds its concurrent resource limit"), span)
                .help("Increase --jobs/--max-streams or add an explicit await after the program's inputs have been supplied. Automatically waiting on a connected pipe could deadlock.")
        })?;
            let memory = self.buffers.clone().try_acquire_many_owned(units).map_err(|_| {
            ExecutionError::new(format!("the ready {role} cohort exceeds the payload-buffer budget"), span)
                .help("Increase --memory-limit, reduce --buffer-size, or run fewer independent program spaces together.")
        })?;
            Ok(Lease {
                _slot: slot,
                _memory: memory,
                context: trace.context(),
            })
        })();
        if result.is_ok() {
            trace.metric("granted_slots", 1);
            trace.metric(
                "reserved_buffer_bytes",
                (units as usize).saturating_mul(self.buffer_bytes) as u64,
            );
        }
        trace.finish_result(&result);
        result
    }
}

fn fits(demand: Demand, capacity: Demand) -> bool {
    demand.copies <= capacity.copies
        && demand.programs <= capacity.programs
        && demand.streams <= capacity.streams
        && demand.buffers <= capacity.buffers
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn running_program_does_not_occupy_copy_lane() {
        let limits = ResourceLimits {
            jobs: 1,
            max_streams: 1,
            buffer_bytes: 1,
            memory_bytes: 9,
        };
        let admission = Admission::new(&limits).unwrap();
        let program = admission.program(Span::default()).unwrap();
        assert_eq!(admission.availability(Demand::PROGRAM), Availability::Busy);
        assert_eq!(admission.copy_availability(), Availability::Ready);
        let copy = admission.copy(Span::default()).unwrap();
        assert_eq!(admission.copy_availability(), Availability::Busy);
        assert_eq!(
            admission.availability(Demand::flows(1)),
            Availability::Ready
        );
        let flow = admission.flow(Span::default()).unwrap();
        assert_eq!(admission.available().buffers, 0);
        drop((copy, flow, program));
        assert_eq!(admission.available().buffers, 9);
    }

    #[test]
    fn separate_lanes_still_share_payload_budget() {
        let limits = ResourceLimits {
            jobs: 1,
            max_streams: 1,
            buffer_bytes: 1,
            memory_bytes: 6,
        };
        let admission = Admission::new(&limits).unwrap();
        let program = admission.program(Span::default()).unwrap();
        assert_eq!(admission.copy_availability(), Availability::Busy);
        assert_eq!(admission.available().copies, 1);
        assert!(admission.copy(Span::default()).is_err());
        assert_eq!(admission.available().copies, 1);
        drop(program);
        assert_eq!(admission.copy_availability(), Availability::Ready);
    }

    #[test]
    fn occupied_capacity_is_busy_not_an_impossible_request() {
        let limits = ResourceLimits {
            jobs: 1,
            max_streams: 1,
            buffer_bytes: 1,
            memory_bytes: 2,
        };
        let admission = Admission::new(&limits).unwrap();
        assert_eq!(
            admission.availability(Demand::flows(1)),
            Availability::Ready
        );
        let lease = admission.flow(Span::default()).unwrap();
        let occupied = admission.available();
        assert_eq!(admission.availability(Demand::flows(1)), Availability::Busy);
        assert_eq!(
            admission.availability(Demand::flows(2)),
            Availability::Impossible
        );
        drop(lease);
        assert_ne!(admission.available(), occupied);
        assert_eq!(
            admission.availability(Demand::flows(1)),
            Availability::Ready
        );
    }
}
