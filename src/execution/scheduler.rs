//! Admission for co-started program pipes. A pipe must never wait behind itself.

use std::future::{Future, poll_fn};
use std::sync::Arc;

use tokio::sync::{OwnedSemaphorePermit, Semaphore};

use crate::scheduler::ResourceLimits;
use crate::syntax::Span;

use super::error::{ExecutionError, Result};

pub(super) struct Admission {
    programs: Arc<Semaphore>,
    streams: Arc<Semaphore>,
    buffers: Arc<Semaphore>,
    buffer_bytes: usize,
}

pub(super) struct Lease {
    _slot: OwnedSemaphorePermit,
    _memory: OwnedSemaphorePermit,
    context: Option<crate::telemetry::TraceContext>,
}

impl Lease {
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
        let cancelled = result
            .as_ref()
            .err()
            .is_some_and(ExecutionError::is_cancelled);
        if let Err(cause) = &result
            && !cancelled
            && let Some(context) = &context
        {
            context.record_error("execution.task", cause);
        }
        if cancelled {
            self.span.cancelled();
        } else {
            self.span.finish_result(&result);
        }
        result
    }
}

impl Admission {
    pub fn new(limits: &ResourceLimits) -> Result<Self> {
        let units = limits.memory_bytes / limits.buffer_bytes;
        if [limits.jobs, limits.max_streams, units]
            .into_iter()
            .any(|value| value > Semaphore::MAX_PERMITS)
        {
            return Err(ExecutionError::new("configured concurrency exceeds the platform admission range", Span::default())
                .help("Use bounded jobs/streams and payload capacity instead of the maximum integer value."));
        }
        Ok(Self {
            programs: Arc::new(Semaphore::new(limits.jobs)),
            streams: Arc::new(Semaphore::new(limits.max_streams)),
            buffers: Arc::new(Semaphore::new(units)),
            buffer_bytes: limits.buffer_bytes,
        })
    }

    pub fn program(&self, span: Span) -> Result<Lease> {
        self.acquire(&self.programs, 6, span, "program")
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
            ExecutionError::new(format!("the ready {role} cohort exceeds its concurrent resource limit"), span)
                .help("Increase --jobs/--max-streams for this co-start cohort, or split it at a completed program boundary; serially waiting on a connected pipe would deadlock.")
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
