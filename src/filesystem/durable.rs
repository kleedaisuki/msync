//! Compatibility adapter for the retired multi-root durable publication API.
//!
//! A batch now publishes ordinary staged replacements in order. A later failure
//! does not undo completed files; the caller retries its transfer.

use std::ffi::OsStr;
use std::io;
use std::path::{Path, PathBuf};

use super::{Publication, PublishEvent, Result};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Event {
    Prepared,
    AfterBackup(usize),
    AfterInstall(usize),
    BeforeCommit,
    Committed,
}

pub struct Batch {
    parent: PathBuf,
    stage: PathBuf,
    publications: Vec<Publication>,
    committed: bool,
}

impl Batch {
    pub fn prepare(publications: Vec<Publication>) -> Result<Self> {
        let first = publications.first().ok_or_else(|| {
            super::invalid(
                "prepare replacements",
                Path::new(""),
                "a batch needs a replacement",
            )
        })?;
        Ok(Self {
            parent: first
                .target
                .parent()
                .expect("replacement target has parent")
                .to_owned(),
            stage: first.workspace.clone(),
            publications,
            committed: false,
        })
    }

    pub fn prepare_registered(
        publications: Vec<Publication>,
        lease: &super::recovery::RegistryLease,
    ) -> Result<(Self, super::recovery::Registration)> {
        let batch = Self::prepare(publications)?;
        let registration = lease
            .register_stage(&batch.parent, &batch.stage)
            .map_err(|cause| super::error("create compatibility receipt", &batch.stage, cause))?;
        Ok((batch, registration))
    }

    pub fn parent(&self) -> &Path {
        &self.parent
    }
    pub fn journal_path(&self) -> &Path {
        &self.stage
    }
    pub fn is_committed(&self) -> bool {
        self.committed
    }
    pub fn publish(&mut self) -> Result<()> {
        self.publish_with(|_| Ok(()))
    }

    pub fn publish_with(&mut self, mut observe: impl FnMut(Event) -> io::Result<()>) -> Result<()> {
        observe(Event::Prepared)
            .map_err(|cause| super::error("prepare replacements", &self.parent, cause))?;
        for (index, publication) in self.publications.iter_mut().enumerate() {
            publication.publish_with(|event| {
                observe(match event {
                    PublishEvent::AfterBackup => Event::AfterBackup(index),
                    PublishEvent::AfterInstall => Event::AfterInstall(index),
                })
            })?;
        }
        observe(Event::BeforeCommit)
            .map_err(|cause| super::error("finish replacements", &self.parent, cause))?;
        self.committed = true;
        observe(Event::Committed)
            .map_err(|cause| super::error("finish replacements", &self.parent, cause))
    }

    /// Release uninstalled staging; already replaced files remain replaced.
    pub fn abort(&mut self) -> Result<()> {
        self.finalize()
    }
    pub fn finalize(&mut self) -> Result<()> {
        for publication in &mut self.publications {
            publication.finalize()?;
        }
        Ok(())
    }
}

pub fn recover_parent(_parent: &Path) -> Result<usize> {
    Ok(0)
}
pub fn recover_journal(_parent: &Path, _name: &OsStr) -> Result<usize> {
    Ok(0)
}
