//! Compatibility handles for the retired persistent recovery API.
//!
//! File transfers now use ordinary staging and replacement. These handles retain
//! source compatibility without creating records, scanning old directories or
//! blocking unrelated execution on abandoned transfers.

use std::io;
use std::path::{Path, PathBuf};

#[derive(Clone, Debug)]
pub struct Registry {
    directory: PathBuf,
}

#[derive(Debug)]
pub struct RegistryLease;

#[derive(Debug)]
pub struct Registration {
    path: PathBuf,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RecoveryReport {
    pub parent: PathBuf,
    pub journal: PathBuf,
    pub recovered: usize,
}

impl Registry {
    pub fn open_existing(_scratch_root: &Path) -> io::Result<Option<Self>> {
        Ok(None)
    }

    pub fn new(scratch_root: &Path) -> io::Result<Self> {
        Ok(Self {
            directory: scratch_root.join(".msync-recovery-v1"),
        })
    }

    pub fn directory(&self) -> &Path {
        &self.directory
    }

    pub fn enter(&self) -> io::Result<RegistryLease> {
        Ok(RegistryLease)
    }

    pub fn register(&self, parent: &Path, journal: &Path) -> io::Result<Registration> {
        self.enter()?.register(parent, journal)
    }
}

impl RegistryLease {
    pub fn register(self, _parent: &Path, journal: &Path) -> io::Result<Registration> {
        Ok(Registration {
            path: journal.to_owned(),
        })
    }

    pub fn register_stage(&self, _parent: &Path, workspace: &Path) -> io::Result<Registration> {
        Ok(Registration {
            path: workspace.to_owned(),
        })
    }
}

impl Registration {
    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn finish(self) -> io::Result<()> {
        Ok(())
    }
}

pub fn recover(_scratch_root: &Path) -> io::Result<Vec<RecoveryReport>> {
    Ok(Vec::new())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compatibility_handles_do_not_create_or_inspect_disk_state() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        std::fs::create_dir_all(&root).unwrap();
        let work = tempfile::tempdir_in(root).unwrap();
        let scratch = work.path().join("absent-scratch");
        let registry = Registry::new(&scratch).unwrap();
        registry
            .enter()
            .unwrap()
            .register_stage(work.path(), &work.path().join("stage"))
            .unwrap()
            .finish()
            .unwrap();
        assert!(!scratch.exists());
        assert!(Registry::open_existing(&scratch).unwrap().is_none());
        assert!(recover(&scratch).unwrap().is_empty());
    }
}
