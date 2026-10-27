use super::{Diagnostic, Operand, absolute, endpoint_error, parents};
use crate::endpoint::{Backend, Location};
use crate::hir::{self, Input, InstructionId, InstructionKind, Output, Region};
use crate::plan::FileClass;
use crate::runtime;
use crate::syntax::{Report, Span};
use std::collections::HashMap;
#[derive(Clone, Copy)]
struct Site {
    instruction: InstructionId,
    operand: Operand,
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
    native: runtime::Inspection,
    resolved: HashMap<(Location, bool), Location>,
    pub(super) classes: HashMap<Location, FileClass>,
    remote: HashMap<Backend, Vec<Location>>,
    sites: HashMap<Location, Site>,
}

impl Observations {
    fn resolve(
        &mut self,
        location: &mut Location,
        source: bool,
        site: Site,
    ) -> Result<(), Diagnostic> {
        let key = (location.clone(), source);
        if let Some(snapshot) = self.resolved.get(&key) {
            *location = snapshot.clone();
            return Ok(());
        }
        let local_class = if let Some(path) = runtime::local_path(location, source)
            .map_err(|message| site.report(endpoint_error(message, location, site.span)))?
        {
            let native = Location {
                backend: Backend::Local,
                path: path.to_string_lossy().into_owned(),
            };
            let class = self
                .native
                .inspect(&native, source)
                .map_err(|message| site.report(endpoint_error(message, location, site.span)))?;
            let path = absolute(&path).map_err(|error| {
                site.report(endpoint_error(error.to_string(), location, site.span))
            })?;
            location.backend = Backend::Local;
            location.path = path.to_string_lossy().into_owned();
            Some(class)
        } else {
            None
        };
        self.resolved.insert(key, location.clone());
        self.sites.entry(location.clone()).or_insert(site);
        if !self.classes.contains_key(location)
            && !self
                .remote
                .values()
                .flatten()
                .any(|queued| queued == location)
        {
            if matches!(location.backend, Backend::Ssh { .. } | Backend::Wsl) {
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
        if location.backend == Backend::Local {
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
}

pub(super) fn resolve_region(
    region: &mut Region,
    observations: &mut Observations,
) -> Result<(), Vec<Diagnostic>> {
    let mut errors = Vec::new();
    for node in &mut region.instructions {
        let site = |operand| Site {
            instruction: node.id,
            operand,
            span: node.span,
        };
        let result = match &mut node.kind {
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                let result = match source {
                    Input::File { location, .. } => {
                        observations.resolve(location, true, site(Operand::Source))
                    }
                    _ => Ok(()),
                };
                result.and_then(|()| match destination {
                    Output::File { location, .. } => {
                        observations.resolve(location, false, site(Operand::Destination))
                    }
                    _ => Ok(()),
                })
            }
            InstructionKind::Spawn {
                executable,
                arguments,
                ..
            } => {
                let result = observations.resolve(executable, true, site(Operand::Operation));
                result.and_then(|()| {
                    for argument in arguments {
                        if let hir::Argument::Path(location) = argument {
                            observations.resolve(location, true, site(Operand::Source))?;
                        }
                    }
                    Ok(())
                })
            }
            InstructionKind::Transaction { body, .. } => {
                if let Err(child) = resolve_region(body, observations) {
                    errors.extend(child);
                }
                Ok(())
            }
            _ => Ok(()),
        };
        if let Err(error) = result {
            errors.push(error);
        }
    }
    if errors.is_empty() {
        Ok(())
    } else {
        Err(errors)
    }
}
