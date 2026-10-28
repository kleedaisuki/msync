use super::state::State;
use super::{Diagnostic, Operand, absolute, endpoint_error, parents};
use crate::endpoint::{Backend, Location};
use crate::hir::effects::{EndpointKey, EndpointRole};
use crate::hir::graph::PlanningContext;
use crate::hir::{self, Input, InstructionId, InstructionKind, Output, Region};
use crate::plan::FileClass;
use crate::runtime;
use crate::syntax::{Report, Span};
use std::collections::{BTreeSet, HashMap, HashSet};
#[derive(Clone, Copy)]
struct Site {
    instruction: InstructionId,
    operand: Operand,
    role: Option<EndpointRole>,
    span: Span,
}
impl Site {
    fn report(self, report: Report) -> Diagnostic {
        Diagnostic {
            instruction: Some(self.instruction),
            operand: self.operand,
            report: Box::new(report),
        }
    }
}

#[derive(Default)]
pub(super) struct Observations {
    host: State,
    native: runtime::Inspection,
    resolved: HashMap<(Location, bool), Location>,
    pub(super) classes: HashMap<Location, FileClass>,
    pub(super) verified: HashSet<Location>,
    pub(super) host_unstable: BTreeSet<EndpointKey>,
    pub(super) independent_endpoints: HashSet<(Location, Location)>,
    pub(super) canonical_remote: HashMap<Location, Location>,
    live: bool,
    wsl_files: HashMap<Location, Option<(Location, FileClass)>>,
    native_filesystems: HashMap<Location, bool>,
    remote: HashMap<Backend, Vec<Location>>,
    sites: HashMap<Location, Site>,
}

impl Observations {
    pub(super) fn new(initial: State, host_unstable: BTreeSet<EndpointKey>, live: bool) -> Self {
        Self {
            host: initial,
            host_unstable,
            live,
            ..Self::default()
        }
    }

    fn resolve(
        &mut self,
        location: &mut Location,
        source: bool,
        site: Site,
    ) -> Result<(), Diagnostic> {
        let key = (location.clone(), source);
        if location.path.contains('\0') {
            return Err(site.report(endpoint_error(
                "file path contains a NUL character",
                location,
                site.span,
            )));
        }
        let captured = if let Some(captured) = self.resolved.get(&key) {
            captured.clone()
        } else {
            let captured = if let Some(path) = runtime::local_path(location, source)
                .map_err(|message| site.report(endpoint_error(message, location, site.span)))?
            {
                let path = if path.is_absolute() {
                    path
                } else {
                    std::env::current_dir()
                        .map_err(|error| {
                            site.report(endpoint_error(error.to_string(), location, site.span))
                        })?
                        .join(path)
                };
                Location {
                    backend: Backend::Local,
                    path: native_spelling(&path),
                }
            } else {
                location.clone()
            };
            self.resolved.insert(key, captured.clone());
            captured
        };
        if site.role.is_none() {
            // A path argument is a value, not a required filesystem input.
            *location = captured;
            return Ok(());
        }
        let endpoint = site.role.map(|role| EndpointKey {
            instruction: site.instruction,
            role,
        });
        let filesystem = matches!(
            captured.backend,
            Backend::Local | Backend::Ssh { .. } | Backend::Wsl
        );
        if source
            && captured.backend == Backend::Wsl
            && self.live
            && !self.host.host_is_stable(&captured)
        {
            self.refine_wsl(&captured);
        }
        let deferred = filesystem
            && (endpoint.is_some_and(|key| self.host_unstable.contains(&key))
                || !self.host.host_is_stable(&captured));
        if deferred && let Some(key) = endpoint {
            self.host_unstable.insert(key);
        }
        *location = captured;
        let local_class = if location.backend == Backend::Local {
            let class = if deferred {
                FileClass::Unknown
            } else if self.verified.contains(location)
                && let Some(class) = self.classes.get(location)
            {
                *class
            } else {
                self.native
                    .inspect(location, source)
                    .map_err(|message| site.report(endpoint_error(message, location, site.span)))?
            };
            if !deferred {
                location.path = absolute(std::path::Path::new(&location.path))
                    .map_err(|error| {
                        site.report(endpoint_error(error.to_string(), location, site.span))
                    })?
                    .to_string_lossy()
                    .into_owned();
            }
            Some(class)
        } else {
            None
        };
        if !deferred {
            self.verified.insert(location.clone());
        }
        self.sites.entry(location.clone()).or_insert(site);
        if !self.classes.contains_key(location)
            && !self
                .remote
                .values()
                .flatten()
                .any(|queued| queued == location)
        {
            if matches!(location.backend, Backend::Ssh { .. } | Backend::Wsl) && deferred {
                self.classes.insert(location.clone(), FileClass::Unknown);
            } else if matches!(location.backend, Backend::Ssh { .. } | Backend::Wsl) {
                self.remote
                    .entry(location.backend.clone())
                    .or_default()
                    .push(location.clone());
            } else {
                let class = match local_class {
                    Some(class) => class,
                    None => self.native.inspect(location, source).map_err(|message| {
                        site.report(endpoint_error(message, location, site.span))
                    })?,
                };
                self.classes.insert(location.clone(), class);
            }
        }
        if location.backend == Backend::Local && !deferred {
            for parent in parents(location) {
                let class = if let Some(class) = self.classes.get(&parent) {
                    *class
                } else {
                    let class = self.native.inspect(&parent, false).map_err(|message| {
                        site.report(endpoint_error(message, &parent, site.span))
                    })?;
                    self.classes.insert(parent, class);
                    class
                };
                if class == FileClass::Directory {
                    break;
                }
            }
        }
        Ok(())
    }

    pub(super) fn complete(&mut self, live: bool) -> Result<(), Vec<Diagnostic>> {
        let native = self
            .sites
            .keys()
            .filter(|location| location.backend == Backend::Local)
            .cloned()
            .collect::<Vec<_>>();
        let wsl = self.canonical_remote.keys().cloned().collect::<Vec<_>>();
        for remote in wsl {
            for local in &native {
                self.prove_pair(local, &remote);
            }
        }
        let mut groups = std::mem::take(&mut self.remote)
            .into_iter()
            .collect::<Vec<_>>();
        groups.sort_by_key(|(backend, _)| backend.to_string());
        for (backend, locations) in groups {
            if !live {
                self.classes.extend(
                    locations
                        .into_iter()
                        .map(|location| (location, FileClass::Unknown)),
                );
                continue;
            }
            let paths = locations
                .iter()
                .map(|location| location.path.clone())
                .collect::<Vec<_>>();
            let rows = runtime::inspect_locations_live(&backend, &paths).map_err(|message| {
                vec![
                    self.sites[&locations[0]]
                        .report(Report::new(message, self.sites[&locations[0]].span)),
                ]
            })?;
            if rows.len() != locations.len() {
                return Err(vec![self.sites[&locations[0]].report(Report::new(
                    "remote metadata response has the wrong number of entries",
                    self.sites[&locations[0]].span,
                ))]);
            }
            for (location, row) in locations.into_iter().zip(rows) {
                let class = row.map_err(|message| {
                    vec![self.sites[&location].report(endpoint_error(
                        message,
                        &location,
                        self.sites[&location].span,
                    ))]
                })?;
                self.classes.insert(location, class);
            }
        }
        Ok(())
    }

    fn refine_wsl(&mut self, location: &Location) {
        let Some(writes) = self.host.separation_candidates(location) else {
            return;
        };
        if !writes.iter().all(|write| {
            *self
                .native_filesystems
                .entry(write.clone())
                .or_insert_with(|| runtime::native_windows_filesystem(write))
        }) {
            return;
        }
        let observation = self
            .wsl_files
            .entry(location.clone())
            .or_insert_with(|| runtime::wsl_native_file(location))
            .clone();
        let Some((canonical, kind)) = observation else {
            return;
        };
        self.canonical_remote.insert(location.clone(), canonical);
        for write in writes {
            self.prove_pair(&write, location);
        }
        self.host.extend_independent(&self.independent_endpoints);
        if self.host.host_is_stable(location) {
            self.classes.insert(location.clone(), kind);
        }
    }

    fn prove_pair(&mut self, native: &Location, wsl: &Location) {
        for local in std::iter::once(native.clone()).chain(parents(native)) {
            let native = *self
                .native_filesystems
                .entry(local.clone())
                .or_insert_with(|| runtime::native_windows_filesystem(&local));
            if native {
                self.independent_endpoints.insert((local, wsl.clone()));
            }
        }
    }
}

fn native_spelling(path: &std::path::Path) -> String {
    use std::path::{Component, MAIN_SEPARATOR};

    #[cfg(windows)]
    let native = std::path::PathBuf::from(path.to_string_lossy().replace('/', "\\"));
    #[cfg(windows)]
    let path = native.as_path();
    let mut spelling = String::new();
    for component in path.components() {
        match component {
            Component::Prefix(prefix) => spelling.push_str(&prefix.as_os_str().to_string_lossy()),
            Component::RootDir => spelling.push(MAIN_SEPARATOR),
            Component::CurDir => {}
            component => {
                if !spelling.is_empty() && !spelling.ends_with(MAIN_SEPARATOR) {
                    spelling.push(MAIN_SEPARATOR);
                }
                spelling.push_str(&component.as_os_str().to_string_lossy());
            }
        }
    }
    // PathBuf::push (including FromIterator<Component>) folds parent traversal
    // in Windows verbatim paths. Only separators may change before admission.
    spelling
}

pub(super) fn resolve_region(
    region: &mut Region,
    observations: &mut Observations,
    context: &PlanningContext,
) -> Result<(), Vec<Diagnostic>> {
    let mut errors = Vec::new();
    for node in &mut region.instructions {
        let site = |operand, role| Site {
            instruction: node.id,
            operand,
            role,
            span: node.span,
        };
        let result = match &mut node.kind {
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                let result = match source {
                    Input::File { location, .. } => observations.resolve(
                        location,
                        true,
                        site(Operand::Source, Some(EndpointRole::Source)),
                    ),
                    _ => Ok(()),
                };
                result.and_then(|()| match destination {
                    Output::File { location, .. } => observations.resolve(
                        location,
                        false,
                        site(Operand::Destination, Some(EndpointRole::Destination)),
                    ),
                    _ => Ok(()),
                })
            }
            InstructionKind::Spawn {
                executable,
                arguments,
                ..
            } => {
                let result = observations.resolve(
                    executable,
                    true,
                    site(Operand::Operation, Some(EndpointRole::Executable)),
                );
                result.and_then(|()| {
                    for argument in arguments {
                        if let hir::Argument::Path(location) = argument {
                            observations.resolve(location, true, site(Operand::Source, None))?;
                        }
                    }
                    Ok(())
                })
            }
            InstructionKind::Transaction { body, .. } => {
                if let Err(child) = resolve_region(body, observations, context) {
                    errors.extend(child);
                }
                Ok(())
            }
            _ => Ok(()),
        };
        if let Err(error) = result {
            errors.push(error);
            continue;
        }
        match &node.kind {
            InstructionKind::Transfer {
                destination: Output::File { location, .. },
                ..
            } => {
                let target = EndpointKey {
                    instruction: node.id,
                    role: EndpointRole::Destination,
                };
                // Writing a proved physical stream changes bytes, not its file
                // kind or namespace. A stale stream observation under a pending
                // native replacement is not such a proof.
                if observations.classes.get(location) != Some(&FileClass::Stream)
                    || observations.host_unstable.contains(&target)
                {
                    observations.host.defer_write(location.clone());
                }
            }
            InstructionKind::Spawn { process, .. } => {
                if let Some(effects) = context.process_effects.get(process) {
                    observations.host.invalidate_writes(effects.footprint());
                } else {
                    observations.host.unknown();
                }
            }
            _ => {}
        }
    }
    if errors.is_empty() {
        Ok(())
    } else {
        Err(errors)
    }
}
