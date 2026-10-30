//! Optional live execution observation, independent of tracing and scheduling.
//!
//! A handle owns only the latest event. It never starts a thread, performs IO,
//! retains file payloads or queues an unbounded history. Observers must not use
//! these events as publication receipts or scheduling proof.

use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use crate::hir::InstructionId;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Phase {
    Inspect,
    Launch,
    Acquire,
    Stage,
    Publish,
    Wait,
    #[default]
    Execute,
}

impl Phase {
    pub fn label(self) -> &'static str {
        match self {
            Self::Inspect => "inspect endpoints",
            Self::Launch => "launch program",
            Self::Acquire => "acquire source",
            Self::Stage => "stage assignment",
            Self::Publish => "publish assignment",
            Self::Wait => "wait for IO",
            Self::Execute => "execute",
        }
    }
}

/// Actual current work, not an estimated percentage or a future completion.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Event {
    pub phase: Phase,
    pub instruction: Option<InstructionId>,
    pub source: Option<String>,
    pub destination: Option<String>,
    pub files: usize,
    pub bytes: u64,
}

impl Event {
    pub fn new(phase: Phase) -> Self {
        Self {
            phase,
            ..Self::default()
        }
    }
}

#[derive(Clone, Debug)]
pub struct Snapshot {
    pub event: Event,
    pub elapsed: Duration,
    pub phase_elapsed: Duration,
    pub revision: u64,
    pub finished: bool,
}

struct State {
    event: Event,
    changed: Instant,
    revision: u64,
    finished: Option<Instant>,
}

struct Shared {
    started: Instant,
    state: Mutex<State>,
    changed: Condvar,
}

/// Clones share one bounded latest-event slot and an idempotent stop signal.
#[derive(Clone)]
pub struct Progress(Arc<Shared>);

impl Default for Progress {
    fn default() -> Self {
        Self::new()
    }
}

impl Progress {
    pub fn new() -> Self {
        let started = Instant::now();
        Self(Arc::new(Shared {
            started,
            state: Mutex::new(State {
                event: Event::default(),
                changed: started,
                revision: 0,
                finished: None,
            }),
            changed: Condvar::new(),
        }))
    }

    pub fn update(&self, event: Event) {
        let mut state = self
            .0
            .state
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        if state.finished.is_some() {
            return;
        }
        if state.event.phase != event.phase
            || state.event.instruction != event.instruction
            || state.event.source != event.source
            || state.event.destination != event.destination
        {
            state.changed = Instant::now();
        }
        state.event = event;
        state.revision = state.revision.saturating_add(1);
        self.0.changed.notify_all();
    }

    pub fn snapshot(&self) -> Snapshot {
        let state = self
            .0
            .state
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        self.read(&state)
    }

    /// Wait at most `timeout`; updates and finish wake observers early.
    pub fn wait(&self, timeout: Duration) -> Snapshot {
        let mut state = self
            .0
            .state
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        if state.finished.is_none() {
            (state, _) = self
                .0
                .changed
                .wait_timeout(state, timeout)
                .unwrap_or_else(|error| error.into_inner());
        }
        self.read(&state)
    }

    /// Stop observation without affecting the execution owner or its resources.
    pub fn finish(&self) {
        let mut state = self
            .0
            .state
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        state.finished.get_or_insert_with(Instant::now);
        self.0.changed.notify_all();
    }

    fn read(&self, state: &State) -> Snapshot {
        let now = state.finished.unwrap_or_else(Instant::now);
        Snapshot {
            event: state.event.clone(),
            elapsed: now.saturating_duration_since(self.0.started),
            phase_elapsed: now.saturating_duration_since(state.changed),
            revision: state.revision,
            finished: state.finished.is_some(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn updates_replace_the_latest_event_without_retaining_history() {
        let progress = Progress::new();
        let copy = progress.clone();
        for index in 0..1000 {
            copy.update(Event {
                phase: Phase::Stage,
                instruction: Some(InstructionId(index)),
                ..Event::default()
            });
        }
        let snapshot = progress.snapshot();
        assert_eq!(snapshot.revision, 1000);
        assert_eq!(snapshot.event.instruction, Some(InstructionId(999)));
        assert!(!snapshot.finished);
        assert!(snapshot.elapsed >= snapshot.phase_elapsed);
    }

    #[test]
    fn finish_wakes_a_blocked_observer_and_is_idempotent() {
        let progress = Progress::new();
        let observer = progress.clone();
        let waiting = std::thread::spawn(move || observer.wait(Duration::from_secs(30)));
        progress.finish();
        assert!(waiting.join().unwrap().finished);
        progress.finish();
        progress.update(Event::new(Phase::Publish));
        assert_eq!(progress.snapshot().revision, 0);
        assert!(progress.wait(Duration::from_secs(30)).finished);
    }

    #[test]
    fn counters_keep_the_phase_clock_and_finish_freezes_elapsed_time() {
        let progress = Progress::new();
        progress.update(Event::new(Phase::Stage));
        let before = progress.snapshot();
        progress.update(Event {
            phase: Phase::Stage,
            files: 1,
            bytes: 17,
            ..Event::default()
        });
        assert!(progress.snapshot().phase_elapsed >= before.phase_elapsed);
        progress.finish();
        let finished = progress.snapshot();
        progress.finish();
        assert_eq!(progress.snapshot().elapsed, finished.elapsed);
        assert_eq!(progress.snapshot().phase_elapsed, finished.phase_elapsed);
    }
}
