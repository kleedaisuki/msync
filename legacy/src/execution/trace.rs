//! Causal tracing attaches context only while a future is actually polled.

use std::future::{Future, poll_fn};
use std::pin::Pin;

use crate::hir::{Instruction, InstructionKind};
use crate::telemetry::LinkedSpan;

use super::error::Result;

pub(super) type Step<'a, T> = Pin<Box<dyn Future<Output = Result<T>> + Send + 'a>>;

pub(super) fn run<'a, T: 'a>(
    mut span: LinkedSpan,
    phase: &'static str,
    future: impl Future<Output = Result<T>> + Send + 'a,
) -> Step<'a, T> {
    if !span.is_recording() {
        return Box::pin(future);
    }
    Box::pin(async move {
        span.expect_completion();
        let context = span.context();
        let mut future = std::pin::pin!(future);
        let result = poll_fn(|cx| {
            crate::telemetry::with_context(context.as_ref(), || future.as_mut().poll(cx))
        })
        .await;
        match &result {
            Err(error) if error.is_cancelled() => span.cancelled(),
            _ => {
                if let Err(error) = &result {
                    span.record_error(phase, error);
                }
                span.finish_result(&result);
            }
        }
        result
    })
}

pub(super) fn instruction(node: &Instruction) -> LinkedSpan {
    let mut span = crate::telemetry::linked_span(
        "interpreter.instruction",
        crate::telemetry::context().as_ref(),
    );
    span.metric("instruction.id", node.id.0 as u64);
    let kind = match &node.kind {
        InstructionKind::Transfer { .. } => "transfer",
        InstructionKind::Spawn { .. } => "spawn",
        InstructionKind::Await { .. } => "acquire",
        InstructionKind::Barrier { .. } => "barrier",
        InstructionKind::Control(control) => control.name(),
    };
    span.attribute("instruction.kind", kind);
    span
}
