//! Compiler-input source expansion; no runtime work or scheduling boundary.

use super::*;
use crate::object::{SourceModule, SourceModulePath};
use std::fs::{self, File, OpenOptions};
use std::io::Read;

const FILE_BYTES: u64 = 8 * 1024 * 1024;
const TOTAL_BYTES: usize = 32 * 1024 * 1024;
const FILE_COUNT: usize = 1024;
const DEPTH: usize = 64;

#[derive(Default)]
pub(super) struct State {
    pub(super) modules: Vec<SourceModule>,
    module_sites: BTreeMap<String, SourceSpan>,
    entry: Option<PathBuf>,
    started: bool,
    root: Option<same_file::Handle>,
    active: Vec<Active>,
    bytes: usize,
    files: usize,
}

impl State {
    pub(super) fn new(entry: Option<PathBuf>) -> Self {
        Self {
            entry,
            ..Self::default()
        }
    }

    fn register_module(
        &mut self,
        module: SourceModule,
        site: &SourceSpan,
    ) -> Result<(), LocatedReport> {
        let Some(existing) = self
            .modules
            .iter_mut()
            .find(|prior| prior.name == module.name)
        else {
            self.module_sites.insert(module.name.clone(), site.clone());
            self.modules.push(module);
            return Ok(());
        };
        if existing.path == module.path {
            return Ok(());
        }
        match (&existing.path, &module.path) {
            // One logical source module has one link identity. A directive-relative
            // anchor preserves relocation; an additional absolute spelling does not.
            (SourceModulePath::Absolute { .. }, SourceModulePath::Contextual { .. }) => {
                existing.path = module.path;
                self.module_sites.insert(module.name, site.clone());
                Ok(())
            }
            (SourceModulePath::Contextual { .. }, SourceModulePath::Absolute { .. }) => Ok(()),
            _ => {
                let mut error = script_error(
                    site,
                    format!("conflicting source module bindings for '{}'", module.name),
                );
                if let Some(prior) = self.module_sites.get(&module.name) {
                    error = error.label(
                        prior.clone(),
                        format!("previous source anchor: {:?}", existing.path),
                    );
                }
                error = error.label(
                    site.clone(),
                    format!("conflicting source anchor: {:?}", module.path),
                );
                error.report.help.push("Use one contextual path for this source module; different contextual anchors may refer to different files after object relocation.".into());
                Err(error)
            }
        }
    }
}

struct Active {
    identity: same_file::Handle,
    site: SourceSpan,
}

impl Lower<'_> {
    pub(super) fn script(
        &mut self,
        expression: &Expr,
        shared: bool,
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        let site = self.source.at(expression.span);
        let value =
            resolve_value(expression, scope, context).map_err(|failure| self.located(failure))?;
        let path = script_path(value, context)
            .map_err(|message| self.located(Report::new(message, expression.span)))?;
        let paths = script_matches(path)
            .map_err(|message| self.located(Report::new(message, expression.span)))?;
        if !self.scripts.started {
            self.scripts.started = true;
            self.scripts.root = self.scripts.entry.as_ref().and_then(|path| {
                open_source(path)
                    .and_then(same_file::Handle::from_file)
                    .ok()
            });
        }
        for file in paths {
            self.script_file(file, &site, shared, scope, context, region)?;
        }
        Ok(())
    }

    fn script_file(
        &mut self,
        file: (PathBuf, SourceModulePath),
        site: &SourceSpan,
        shared: bool,
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        let (path, binding) = file;
        let path = path.as_path();
        if self.scripts.active.len() >= DEPTH || self.scripts.files >= FILE_COUNT {
            return Err(script_error(
                site,
                format!("source expansion exceeds the {DEPTH}-level/{FILE_COUNT}-file bound"),
            ));
        }
        let (text, identity) = read_script(path).map_err(|cause| {
            script_error(
                site,
                format!("cannot read msync source {}: {cause}", path.display()),
            )
        })?;
        if self.scripts.root.as_ref() == Some(&identity)
            || self
                .scripts
                .active
                .iter()
                .any(|active| active.identity == identity)
        {
            let mut error = script_error(
                site,
                format!("cyclic source expansion through {}", path.display()),
            );
            for active in self.scripts.active.iter().rev().take(8) {
                error = error.label(active.site.clone(), "source expansion entered here");
            }
            return Err(error);
        }
        self.scripts.bytes = self
            .scripts
            .bytes
            .checked_add(text.len())
            .ok_or_else(|| script_error(site, "source expansion byte count overflow"))?;
        if self.scripts.bytes > TOTAL_BYTES {
            return Err(script_error(
                site,
                "source expansion exceeds the 32MiB aggregate input bound",
            ));
        }
        self.scripts.files += 1;
        let name = path.to_string_lossy().into_owned();
        let self_anchor = matches!(&binding, SourceModulePath::Contextual { path: RelocPath { anchor: Anchor::ModuleFile { module } | Anchor::ModuleDirectory { module }, .. } } if module == &name);
        let binding = if self_anchor {
            SourceModulePath::Absolute { path: name.clone() }
        } else {
            binding
        };
        let module = SourceModule {
            name,
            path: binding,
        };
        self.scripts.register_module(module, site)?;
        let source = Source::new(path.to_string_lossy().into_owned(), text);
        language::select_version(&source.text, None, language::Version::Structured)
            .map_err(|reports| source_errors(&source, reports, site, shared))?;
        let document = language::parse(&source.text)
            .map_err(|reports| source_errors(&source, reports, site, shared))?;
        self.scripts.active.push(Active {
            identity,
            site: site.clone(),
        });
        let previous = std::mem::replace(&mut self.source, source.clone());
        let child_context = CompileContext {
            pwd: context.pwd.clone(),
            source_file: Some(path.to_owned()),
        };
        let errors = if shared {
            let prior_source = scope.source.replace(source);
            let errors = self.block(&document.statements, scope, &child_context, region);
            scope.source = prior_source;
            errors
        } else {
            let mut isolated = Scope {
                slots: scope.slots.clone(),
                current_unit: scope.current_unit,
                symbol_budget: scope.symbol_budget.clone(),
                source: Some(source),
                ..Scope::default()
            };
            self.block(&document.statements, &mut isolated, &child_context, region)
        };
        self.source = previous;
        self.scripts.active.pop();
        if let Some(error) = errors.into_iter().next() {
            let action = if shared { "including" } else { "executing" };
            return Err(error
                .in_context(site.clone())
                .label(site.clone(), format!("while {action} {}", path.display())));
        }
        Ok(())
    }
}

fn script_error(site: &SourceSpan, message: impl Into<String>) -> LocatedReport {
    LocatedReport::new(site.source.clone(), Report::new(message, site.span).help("Source files are expanded during compilation, before runtime effects. Check the file path and active include/execute chain."))
}

fn source_errors(
    source: &Arc<Source>,
    reports: Vec<Report>,
    site: &SourceSpan,
    shared: bool,
) -> LocatedReport {
    let action = if shared { "including" } else { "executing" };
    let mut reports = reports.into_iter();
    let report = reports
        .next()
        .unwrap_or_else(|| Report::new("invalid msync source", Span::default()));
    LocatedReport::new(source.clone(), report)
        .in_context(site.clone())
        .label(site.clone(), format!("while {action} this source"))
}

struct SourcePath {
    file: PathBuf,
    pattern: Option<String>,
    binding: SourceModulePath,
}

fn script_path(value: Value, context: &CompileContext) -> Result<SourcePath, String> {
    let (mut location, mut relocation) = match value {
        Value::Literal(text) => (
            Location {
                backend: Backend::Local,
                path: text.to_string(),
            },
            None,
        ),
        Value::Path(path) => (
            Location {
                backend: path.backend.unwrap_or(Backend::Local),
                path: path.path,
            },
            path.relocation,
        ),
        _ => {
            return Err(
                "execute/include expects a source path item or a filename string literal".into(),
            );
        }
    };
    if location.path.is_empty() || location.path.contains('\0') {
        return Err("source filenames must be nonempty and NUL-free".into());
    }
    if !matches!(
        location.backend,
        Backend::Local | Backend::Env | Backend::Path
    ) {
        return Err(format!(
            "source loading requires a locally readable file, not {}; make a local source copy before compiling this submission",
            location.backend
        ));
    }
    let before = location.path.len();
    let pattern = super::super::take_selector(&mut location.path, &mut HashSet::new())?;
    if let Some(relocation) = &mut relocation {
        relocation.suffix.truncate(
            relocation
                .suffix
                .len()
                .saturating_sub(before - location.path.len()),
        );
    }
    let relocation = path_relocation(&location.backend, &location.path, relocation);
    let relative = !absolute_fragment(&location.path) && !rooted_fragment(&location.path);
    // Explicit environment items keep their cwd anchor in contextual objects.
    let base = if location.backend == Backend::Env {
        &context.pwd
    } else {
        context
            .source_file
            .as_deref()
            .and_then(Path::parent)
            .unwrap_or(&context.pwd)
    };
    let file = crate::runtime::local_path_at(&location, true, Some(&context.pwd))?
        .ok_or("source path did not resolve to a local filename")?;
    let file = std::path::absolute(if file.is_absolute() {
        file
    } else {
        base.join(file)
    })
    .map_err(|cause| cause.to_string())?;
    let binding = match relocation {
        Some(path) if !matches!(path.anchor, Anchor::SearchPath { .. }) => {
            SourceModulePath::Contextual { path }
        }
        _ if relative && location.backend != Backend::Path => {
            let anchor = context
                .source_file
                .as_ref()
                .map_or(Anchor::WorkingDirectory, |file| Anchor::ModuleDirectory {
                    module: file.to_string_lossy().into_owned(),
                });
            SourceModulePath::Contextual {
                path: RelocPath::new(anchor, location.path),
            }
        }
        _ => SourceModulePath::Absolute {
            path: file
                .to_str()
                .ok_or("source paths must be UTF-8")?
                .to_owned(),
        },
    };
    Ok(SourcePath {
        file,
        pattern,
        binding,
    })
}

fn script_matches(source: SourcePath) -> Result<Vec<(PathBuf, SourceModulePath)>, String> {
    let Some(pattern) = source.pattern else {
        return Ok(vec![(source.file, source.binding)]);
    };
    let regex =
        regex::Regex::new(&format!("\\A(?:{pattern})\\z")).map_err(|cause| cause.to_string())?;
    let entries = fs::read_dir(&source.file).map_err(|cause| {
        format!(
            "cannot enumerate source directory {}: {cause}",
            source.file.display()
        )
    })?;
    let mut selected = Vec::new();
    for entry in entries {
        let entry = entry.map_err(|cause| cause.to_string())?;
        let Ok(name) = entry.file_name().into_string() else {
            continue;
        };
        if regex.is_match(&name) {
            selected.push((name, entry.path()));
            if selected.len() > FILE_COUNT {
                return Err(format!(
                    "source selector exceeds {FILE_COUNT} matching entries"
                ));
            }
        }
    }
    selected.sort_by(|left, right| left.0.cmp(&right.0));
    if selected.is_empty() {
        return Err(format!(
            "source selector <{pattern}> matched no files in {}",
            source.file.display()
        ));
    }
    selected
        .into_iter()
        .map(|(name, file)| {
            let binding = match &source.binding {
                SourceModulePath::Absolute { path } => SourceModulePath::Absolute {
                    path: Path::new(path)
                        .join(&name)
                        .to_str()
                        .ok_or("source paths must be UTF-8")?
                        .to_owned(),
                },
                SourceModulePath::Contextual { path } => {
                    let mut path = path.clone();
                    if !path.suffix.is_empty() && !path.suffix.ends_with(['/', '\\']) {
                        path.suffix.push('/');
                    }
                    path.append(&name);
                    SourceModulePath::Contextual { path }
                }
            };
            Ok((file, binding))
        })
        .collect()
}

fn open_source(path: &Path) -> std::io::Result<File> {
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(libc::O_NONBLOCK | libc::O_NOCTTY);
    }
    let file: File = options.open(path)?;
    let metadata = file.metadata()?;
    if !metadata.is_file() {
        return Err(std::io::Error::other(
            "source input must be a finite ordinary file",
        ));
    }
    Ok(file)
}

fn read_script(path: &Path) -> std::io::Result<(String, same_file::Handle)> {
    let file = open_source(path)?;
    let metadata = file.metadata()?;
    if metadata.len() > FILE_BYTES {
        return Err(std::io::Error::other(
            "source file exceeds the 8MiB input bound",
        ));
    }
    let identity = same_file::Handle::from_file(file.try_clone()?)?;
    let mut text = String::new();
    file.take(FILE_BYTES + 1).read_to_string(&mut text)?;
    if text.len() as u64 > FILE_BYTES {
        return Err(std::io::Error::other(
            "source file grew beyond the 8MiB input bound",
        ));
    }
    Ok((text, identity))
}
