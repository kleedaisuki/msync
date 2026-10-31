//! Convention-first user configuration with explicit provenance and source spans.

use std::collections::BTreeMap;
use std::env;
use std::fmt;
use std::fs;
use std::io::{self, Read};
use std::path::{Path, PathBuf};
use std::time::Duration;

use serde::Deserialize;
use toml::Spanned;

use crate::scheduler::ResourceLimits;
use crate::syntax::{Report, Span};

const MAX_CONFIG_BYTES: u64 = 1024 * 1024;

fn open_config(path: &Path) -> io::Result<fs::File> {
    let metadata = fs::metadata(path)?;
    if !metadata.is_file() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "configuration must be an ordinary file",
        ));
    }
    let mut options = fs::OpenOptions::new();
    options.read(true);
    // A replacement between stat and open must not turn configuration loading
    // into a wait for a FIFO peer. The opened handle is checked again below.
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(libc::O_NONBLOCK);
    }
    options.open(path)
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Color {
    #[default]
    Auto,
    Always,
    Never,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TelemetryBackend {
    #[default]
    Sqlite,
    Jsonl,
}

#[derive(Clone, Debug)]
pub struct ExecutionSettings {
    pub limits: ResourceLimits,
    pub spool_limit: Option<u64>,
    pub state_dir: Option<PathBuf>,
}

#[derive(Clone, Debug)]
pub struct ProgressSettings {
    pub delay_ms: u64,
    pub interval_ms: u64,
}

impl Default for ProgressSettings {
    fn default() -> Self {
        Self {
            delay_ms: 200,
            interval_ms: 250,
        }
    }
}

impl ProgressSettings {
    pub fn delay(&self) -> Duration {
        Duration::from_millis(self.delay_ms)
    }
    pub fn interval(&self) -> Duration {
        Duration::from_millis(self.interval_ms)
    }
}

#[derive(Clone, Debug)]
pub struct TelemetrySettings {
    pub enabled: bool,
    pub backend: TelemetryBackend,
    pub path: Option<PathBuf>,
    pub queue_capacity: usize,
    pub batch_size: usize,
    pub flush_interval_ms: u64,
    pub busy_timeout_ms: u64,
    pub max_sessions: usize,
    pub max_records_per_session: usize,
    pub max_database_bytes: u64,
    pub synchronous_full: bool,
    pub sample_per_million: u32,
}

impl Default for TelemetrySettings {
    fn default() -> Self {
        let defaults = crate::telemetry::SqliteOptions::default();
        Self {
            enabled: false,
            backend: TelemetryBackend::Sqlite,
            path: None,
            queue_capacity: defaults.queue_capacity,
            batch_size: defaults.batch_size,
            flush_interval_ms: defaults.flush_interval_ms,
            busy_timeout_ms: defaults.busy_timeout_ms,
            max_sessions: defaults.max_sessions,
            max_records_per_session: defaults.max_records_per_session,
            max_database_bytes: defaults.max_database_bytes,
            synchronous_full: defaults.synchronous_full,
            sample_per_million: defaults.sample_per_million,
        }
    }
}

impl TelemetrySettings {
    pub fn sqlite_options(&self) -> crate::telemetry::SqliteOptions {
        crate::telemetry::SqliteOptions {
            queue_capacity: self.queue_capacity,
            batch_size: self.batch_size,
            flush_interval_ms: self.flush_interval_ms,
            busy_timeout_ms: self.busy_timeout_ms,
            max_sessions: self.max_sessions,
            max_records_per_session: self.max_records_per_session,
            max_database_bytes: self.max_database_bytes,
            synchronous_full: self.synchronous_full,
            sample_per_million: self.sample_per_million,
        }
    }
}

#[derive(Clone, Debug)]
pub struct Settings {
    pub execution: ExecutionSettings,
    pub color: Color,
    pub syntax: String,
    pub progress: ProgressSettings,
    pub telemetry: TelemetrySettings,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            execution: ExecutionSettings {
                limits: ResourceLimits::default(),
                spool_limit: None,
                state_dir: home().map(|path| path.join(".msync/state")),
            },
            color: Color::Auto,
            syntax: "0.6".into(),
            progress: ProgressSettings::default(),
            telemetry: TelemetrySettings::default(),
        }
    }
}

#[derive(Clone, Debug)]
pub enum Origin {
    Default,
    Configuration(PathBuf),
    Environment(&'static str),
    CommandLine,
}

impl fmt::Display for Origin {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Default => f.write_str("default"),
            Self::Configuration(path) => write!(f, "configuration {}", path.display()),
            Self::Environment(name) => write!(f, "environment {name}"),
            Self::CommandLine => f.write_str("command line"),
        }
    }
}

#[derive(Debug)]
pub struct Error {
    pub path: PathBuf,
    pub key: Option<String>,
    pub span: Option<Span>,
    details: Box<ErrorDetails>,
}

#[derive(Debug)]
struct ErrorDetails {
    input: Option<String>,
    message: String,
    cause: Option<Box<dyn std::error::Error + Send + Sync>>,
}

const _: () = assert!(std::mem::size_of::<Error>() < 128);

impl Error {
    fn io(path: &Path, cause: io::Error) -> Self {
        Self {
            path: path.to_owned(),
            key: None,
            span: None,
            details: Box::new(ErrorDetails {
                input: None,
                message: format!("cannot read configuration: {cause}"),
                cause: Some(Box::new(cause)),
            }),
        }
    }

    pub fn render(&self, color: bool) -> String {
        if let (Some(input), Some(span)) = (&self.details.input, self.span) {
            let report = Report::new(
                format!("configuration error: {}", self.details.message),
                span,
            )
            .help("Correct this setting, choose another --config file, or use --no-config.");
            return crate::syntax::render_report_named(
                input,
                &report,
                &self.path.to_string_lossy(),
                color,
            );
        }
        format!(
            "configuration error: {}\n  path: {}\n  help: Correct the file or use --no-config.",
            self.details.message,
            self.path.display()
        )
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.path.display(), self.details.message)
    }
}
impl std::error::Error for Error {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        self.details
            .cause
            .as_deref()
            .map(|error| error as &(dyn std::error::Error + 'static))
    }
}

#[derive(Clone, Debug)]
pub struct Loaded {
    pub settings: Settings,
    pub path: Option<PathBuf>,
    pub origins: BTreeMap<&'static str, Origin>,
    spans: BTreeMap<&'static str, Span>,
    input: Option<String>,
}

impl Loaded {
    pub fn load(explicit: Option<&Path>, disabled: bool) -> Result<Self, Error> {
        let mut loaded = Self {
            settings: Settings::default(),
            path: None,
            origins: BTreeMap::new(),
            spans: BTreeMap::new(),
            input: None,
        };
        if !disabled {
            let path = explicit.map(Path::to_owned).or_else(|| {
                env::var_os("MSYNC_CONFIG")
                    .filter(|value| !value.is_empty())
                    .map(PathBuf::from)
            });
            if let Some(path) = path {
                loaded.read(&path)?;
            } else {
                for path in conventional_paths() {
                    match loaded.read(&path) {
                        Ok(()) => break,
                        Err(error)
                            if error
                                .details
                                .cause
                                .as_ref()
                                .and_then(|cause| cause.downcast_ref::<io::Error>())
                                .is_some_and(|cause| cause.kind() == io::ErrorKind::NotFound) => {}
                        Err(error) => return Err(error),
                    }
                }
            }
        }
        if let Some(path) = env::var_os("MSYNC_STATE_DIR") {
            loaded.settings.execution.state_dir = Some(
                absolute(&PathBuf::from(path))
                    .map_err(|cause| Error::io(Path::new("MSYNC_STATE_DIR"), cause))?,
            );
            loaded.origins.insert(
                "execution.state_dir",
                Origin::Environment("MSYNC_STATE_DIR"),
            );
        }
        Ok(loaded)
    }

    fn read(&mut self, path: &Path) -> Result<(), Error> {
        let path = absolute(path).map_err(|cause| Error::io(path, cause))?;
        let file = open_config(&path).map_err(|cause| Error::io(&path, cause))?;
        let metadata = file.metadata().map_err(|cause| Error::io(&path, cause))?;
        if !metadata.is_file() {
            return Err(Error::io(
                &path,
                io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "configuration must be an ordinary file",
                ),
            ));
        }
        let mut bytes = Vec::new();
        file.take(MAX_CONFIG_BYTES + 1)
            .read_to_end(&mut bytes)
            .map_err(|cause| Error::io(&path, cause))?;
        if bytes.len() as u64 > MAX_CONFIG_BYTES {
            return Err(Error::io(
                &path,
                io::Error::new(io::ErrorKind::InvalidData, "configuration exceeds 1 MiB"),
            ));
        }
        let input = String::from_utf8(bytes)
            .map_err(|cause| Error::io(&path, io::Error::new(io::ErrorKind::InvalidData, cause)))?;
        let document = toml::from_str::<Document>(&input).map_err(|cause| Error {
            path: path.clone(),
            key: None,
            span: cause.span().map(|span| Span {
                start: span.start,
                end: span.end,
            }),
            details: Box::new(ErrorDetails {
                input: Some(input.clone()),
                message: cause.message().to_owned(),
                cause: Some(Box::new(cause)),
            }),
        })?;
        self.path = Some(path);
        self.input = Some(input);
        self.apply(document)?;
        Ok(())
    }

    fn record<T>(&mut self, key: &'static str, value: Spanned<T>) -> T {
        let span = value.span();
        self.spans.insert(
            key,
            Span {
                start: span.start,
                end: span.end,
            },
        );
        self.origins.insert(
            key,
            Origin::Configuration(self.path.clone().expect("loaded configuration path")),
        );
        value.into_inner()
    }

    fn apply(&mut self, doc: Document) -> Result<(), Error> {
        macro_rules! set {
            ($field:expr, $value:expr, $key:literal) => {
                if let Some(value) = $value {
                    $field = self.record($key, value);
                }
            };
        }
        let execution = doc.execution.unwrap_or_default();
        set!(
            self.settings.execution.limits.jobs,
            execution.jobs,
            "execution.jobs"
        );
        set!(
            self.settings.execution.limits.max_streams,
            execution.max_streams,
            "execution.max_streams"
        );
        if let Some(value) = execution.buffer_size {
            let size = self.bytes("execution.buffer_size", value, false)?;
            self.settings.execution.limits.buffer_bytes = usize::try_from(size)
                .map_err(|_| self.invalid("execution.buffer_size", "byte budget is too large"))?;
        }
        if let Some(value) = execution.memory_limit {
            let size = self.bytes("execution.memory_limit", value, false)?;
            self.settings.execution.limits.memory_bytes = usize::try_from(size)
                .map_err(|_| self.invalid("execution.memory_limit", "byte budget is too large"))?;
        }
        if let Some(value) = execution.spool_limit {
            if matches!(value.get_ref(), Bytes::Text(text) if text == "unlimited") {
                self.record("execution.spool_limit", value);
                self.settings.execution.spool_limit = None;
            } else {
                self.settings.execution.spool_limit =
                    Some(self.bytes("execution.spool_limit", value, true)?);
            }
        }
        if let Some(value) = execution.state_dir {
            let raw = self.record("execution.state_dir", value);
            self.settings.execution.state_dir = Some(self.path_value("execution.state_dir", &raw)?);
        }
        let ui = doc.ui.unwrap_or_default();
        set!(self.settings.color, ui.color, "ui.color");
        let language = doc.language.unwrap_or_default();
        set!(self.settings.syntax, language.syntax, "language.syntax");
        let progress = doc.progress.unwrap_or_default();
        set!(
            self.settings.progress.delay_ms,
            progress.delay_ms,
            "progress.delay_ms"
        );
        set!(
            self.settings.progress.interval_ms,
            progress.interval_ms,
            "progress.interval_ms"
        );
        let telemetry = doc.telemetry.unwrap_or_default();
        set!(
            self.settings.telemetry.enabled,
            telemetry.enabled,
            "telemetry.enabled"
        );
        set!(
            self.settings.telemetry.backend,
            telemetry.backend,
            "telemetry.backend"
        );
        if let Some(value) = telemetry.path {
            let raw = self.record("telemetry.path", value);
            self.settings.telemetry.path = Some(self.path_value("telemetry.path", &raw)?);
        }
        set!(
            self.settings.telemetry.queue_capacity,
            telemetry.queue_capacity,
            "telemetry.queue_capacity"
        );
        set!(
            self.settings.telemetry.batch_size,
            telemetry.batch_size,
            "telemetry.batch_size"
        );
        set!(
            self.settings.telemetry.flush_interval_ms,
            telemetry.flush_interval_ms,
            "telemetry.flush_interval_ms"
        );
        set!(
            self.settings.telemetry.busy_timeout_ms,
            telemetry.busy_timeout_ms,
            "telemetry.busy_timeout_ms"
        );
        set!(
            self.settings.telemetry.max_sessions,
            telemetry.max_sessions,
            "telemetry.max_sessions"
        );
        set!(
            self.settings.telemetry.max_records_per_session,
            telemetry.max_records_per_session,
            "telemetry.max_records_per_session"
        );
        if let Some(value) = telemetry.max_database_bytes {
            self.settings.telemetry.max_database_bytes =
                self.bytes("telemetry.max_database_bytes", value, false)?;
        }
        set!(
            self.settings.telemetry.synchronous_full,
            telemetry.synchronous_full,
            "telemetry.synchronous_full"
        );
        set!(
            self.settings.telemetry.sample_per_million,
            telemetry.sample_per_million,
            "telemetry.sample_per_million"
        );
        self.validate_values()
    }

    fn path_value(&self, key: &'static str, raw: &str) -> Result<PathBuf, Error> {
        if raw.is_empty() || raw.contains('\0') {
            return Err(self.invalid(key, "path must be nonempty and NUL-free"));
        }
        let path = Path::new(raw);
        if path.is_absolute() {
            return Ok(path.to_owned());
        }
        Ok(self
            .path
            .as_ref()
            .expect("loaded path")
            .parent()
            .expect("absolute configuration path")
            .join(path))
    }

    fn bytes(
        &mut self,
        key: &'static str,
        value: Spanned<Bytes>,
        allow_zero: bool,
    ) -> Result<u64, Error> {
        let value = self.record(key, value);
        let bytes = match value {
            Bytes::Number(value) => Ok(value),
            Bytes::Text(value) => parse_bytes(&value),
        }
        .map_err(|cause| self.invalid(key, cause))?;
        if !allow_zero && bytes == 0 {
            return Err(self.invalid(key, "byte budget must be positive"));
        }
        Ok(bytes)
    }

    fn validate_values(&self) -> Result<(), Error> {
        for (key, valid) in [
            ("execution.jobs", self.settings.execution.limits.jobs > 0),
            (
                "execution.max_streams",
                self.settings.execution.limits.max_streams > 0,
            ),
            (
                "progress.interval_ms",
                self.settings.progress.interval_ms > 0,
            ),
        ] {
            if !valid {
                return Err(self.invalid(key, "value must be positive"));
            }
        }
        if let Err((key, message)) = self.settings.telemetry.sqlite_options().validate_keyed() {
            let key = match key {
                "queue_capacity" => "telemetry.queue_capacity",
                "batch_size" => "telemetry.batch_size",
                "flush_interval_ms" => "telemetry.flush_interval_ms",
                "busy_timeout_ms" => "telemetry.busy_timeout_ms",
                "max_sessions" => "telemetry.max_sessions",
                "max_records_per_session" => "telemetry.max_records_per_session",
                "max_database_bytes" => "telemetry.max_database_bytes",
                "sample_per_million" => "telemetry.sample_per_million",
                _ => "telemetry",
            };
            return Err(self.invalid(key, message));
        }
        for (key, value) in [
            ("progress.delay_ms", self.settings.progress.delay_ms),
            ("progress.interval_ms", self.settings.progress.interval_ms),
        ] {
            if value > 86_400_000 {
                return Err(self.invalid(
                    key,
                    "duration must not exceed 86400000 milliseconds (24 hours)",
                ));
            }
        }
        if self
            .settings
            .syntax
            .parse::<crate::language::Version>()
            .is_err()
        {
            return Err(self.invalid(
                "language.syntax",
                "choose syntax 0.6 or its 0.5 compatibility alias",
            ));
        }
        Ok(())
    }

    pub fn validate(&self) -> Result<(), Error> {
        self.validate_values()?;
        if let Err((key, message)) = self.settings.execution.limits.validate_keyed() {
            let key = match key {
                "jobs" => "execution.jobs",
                "max_streams" => "execution.max_streams",
                "buffer_size" => "execution.buffer_size",
                "memory_limit" => "execution.memory_limit",
                _ => "execution",
            };
            return Err(self.invalid(key, message));
        }
        Ok(())
    }

    fn invalid(&self, key: &'static str, message: impl Into<String>) -> Error {
        Error {
            path: self
                .path
                .clone()
                .unwrap_or_else(|| PathBuf::from("<command line>")),
            key: Some(key.into()),
            span: self.spans.get(key).copied(),
            details: Box::new(ErrorDetails {
                input: self.input.clone(),
                message: format!("{key}: {}", message.into()),
                cause: None,
            }),
        }
    }

    pub fn mark_cli(&mut self, key: &'static str) {
        self.origins.insert(key, Origin::CommandLine);
        self.spans.remove(key);
    }
    pub fn origin(&self, key: &str) -> Origin {
        self.origins.get(key).cloned().unwrap_or(Origin::Default)
    }

    pub fn render(&self) -> String {
        let mut out = String::from(
            "# Effective msync configuration; explicit CLI options override configuration.\n",
        );
        if let Some(path) = &self.path {
            out.push_str(&format!("# Configuration file: {path:?}\n"));
        } else {
            out.push_str("# Configuration file: none (defaults and explicit overrides).\n");
        }
        let e = &self.settings.execution;
        let mut execution = vec![
            ("jobs", e.limits.jobs.to_string()),
            ("max_streams", e.limits.max_streams.to_string()),
            ("buffer_size", byte_budget(e.limits.buffer_bytes as u64)),
            ("memory_limit", byte_budget(e.limits.memory_bytes as u64)),
            (
                "spool_limit",
                e.spool_limit
                    .map_or_else(|| quoted("unlimited"), byte_budget),
            ),
        ];
        if let Some(path) = &e.state_dir {
            execution.push(("state_dir", quoted(&path.to_string_lossy())));
        }
        self.table(&mut out, "execution", &execution);
        if e.state_dir.is_none() {
            out.push_str(
                "# state_dir is unset (source: default); no user home directory is available.\n",
            );
        }
        self.table(
            &mut out,
            "ui",
            &[(
                "color",
                quoted(match self.settings.color {
                    Color::Auto => "auto",
                    Color::Always => "always",
                    Color::Never => "never",
                }),
            )],
        );
        self.table(
            &mut out,
            "language",
            &[("syntax", quoted(&self.settings.syntax))],
        );
        self.table(
            &mut out,
            "progress",
            &[
                ("delay_ms", self.settings.progress.delay_ms.to_string()),
                (
                    "interval_ms",
                    self.settings.progress.interval_ms.to_string(),
                ),
            ],
        );
        let t = &self.settings.telemetry;
        let mut telemetry = vec![
            ("enabled", t.enabled.to_string()),
            (
                "backend",
                quoted(match t.backend {
                    TelemetryBackend::Sqlite => "sqlite",
                    TelemetryBackend::Jsonl => "jsonl",
                }),
            ),
            ("queue_capacity", t.queue_capacity.to_string()),
            ("batch_size", t.batch_size.to_string()),
            ("flush_interval_ms", t.flush_interval_ms.to_string()),
            ("busy_timeout_ms", t.busy_timeout_ms.to_string()),
            ("max_sessions", t.max_sessions.to_string()),
            (
                "max_records_per_session",
                t.max_records_per_session.to_string(),
            ),
            ("max_database_bytes", t.max_database_bytes.to_string()),
            ("synchronous_full", t.synchronous_full.to_string()),
            ("sample_per_million", t.sample_per_million.to_string()),
        ];
        if let Some(path) = &t.path {
            telemetry.push(("path", quoted(&path.to_string_lossy())));
        }
        self.table(&mut out, "telemetry", &telemetry);
        if t.path.is_none() {
            out.push_str("# path is unset (source: default); enabling telemetry selects the conventional state directory.\n");
        }
        out
    }

    fn table(&self, out: &mut String, table: &str, rows: &[(&str, String)]) {
        out.push_str(&format!("\n[{table}]\n"));
        for (key, value) in rows {
            let full = format!("{table}.{key}");
            let origin = self.origin(&full).to_string().replace(['\n', '\r'], " ");
            out.push_str(&format!("# source: {origin}\n{key} = {value}\n"));
        }
    }
}

fn quoted(value: &str) -> String {
    toml::Value::String(value.into()).to_string()
}

fn byte_budget(value: u64) -> String {
    if i64::try_from(value).is_ok() {
        value.to_string()
    } else {
        quoted(&value.to_string())
    }
}
fn home() -> Option<PathBuf> {
    env::var_os(if cfg!(windows) { "USERPROFILE" } else { "HOME" })
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
}
fn absolute(path: &Path) -> io::Result<PathBuf> {
    std::path::absolute(path)
}

pub fn conventional_paths() -> Vec<PathBuf> {
    let home = home();
    let variable = if cfg!(windows) {
        "APPDATA"
    } else {
        "XDG_CONFIG_HOME"
    };
    let canonical = env::var_os(variable)
        .map(PathBuf::from)
        .filter(|path| path.is_absolute())
        .map(|path| path.join("msync/config.toml"))
        .or_else(|| {
            home.as_ref().map(|path| {
                path.join(if cfg!(windows) {
                    "AppData/Roaming/msync/config.toml"
                } else {
                    ".config/msync/config.toml"
                })
            })
        });
    canonical
        .into_iter()
        .chain(home.map(|path| path.join(".msync/config.toml")))
        .collect()
}

/// Byte budgets share the established CLI spelling and overflow semantics.
pub fn parse_bytes(value: &str) -> Result<u64, String> {
    let end = value
        .find(|ch: char| !ch.is_ascii_digit())
        .unwrap_or(value.len());
    let number = value[..end]
        .parse::<u64>()
        .map_err(|_| format!("'{value}' is not a nonnegative byte budget"))?;
    let scale = match value[end..].to_ascii_lowercase().as_str() {
        "" | "b" => 1,
        "k" | "kb" | "kib" => 1024,
        "m" | "mb" | "mib" => 1024 * 1024,
        "g" | "gb" | "gib" => 1024 * 1024 * 1024,
        _ => {
            return Err(format!(
                "invalid byte unit in '{value}'; use bytes, KiB, MiB or GiB"
            ));
        }
    };
    number
        .checked_mul(scale)
        .ok_or_else(|| "byte budget is too large".into())
}

enum Bytes {
    Number(u64),
    Text(String),
}

impl<'de> Deserialize<'de> for Bytes {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct Budget;

        impl<'de> serde::de::Visitor<'de> for Budget {
            type Value = Bytes;

            fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                formatter.write_str(
                    "a nonnegative integer byte count or a quoted budget such as \"64KiB\"",
                )
            }

            fn visit_u64<E: serde::de::Error>(self, value: u64) -> Result<Self::Value, E> {
                Ok(Bytes::Number(value))
            }

            fn visit_i64<E: serde::de::Error>(self, value: i64) -> Result<Self::Value, E> {
                u64::try_from(value)
                    .map(Bytes::Number)
                    .map_err(|_| E::invalid_value(serde::de::Unexpected::Signed(value), &self))
            }

            fn visit_str<E: serde::de::Error>(self, value: &str) -> Result<Self::Value, E> {
                Ok(Bytes::Text(value.to_owned()))
            }

            fn visit_string<E: serde::de::Error>(self, value: String) -> Result<Self::Value, E> {
                Ok(Bytes::Text(value))
            }
        }

        deserializer.deserialize_any(Budget)
    }
}

#[derive(Default, Deserialize)]
#[serde(default, deny_unknown_fields)]
struct Document {
    execution: Option<Execution>,
    ui: Option<Ui>,
    language: Option<Language>,
    progress: Option<Progress>,
    telemetry: Option<Telemetry>,
}
#[derive(Default, Deserialize)]
#[serde(default, deny_unknown_fields)]
struct Execution {
    jobs: Option<Spanned<usize>>,
    max_streams: Option<Spanned<usize>>,
    buffer_size: Option<Spanned<Bytes>>,
    memory_limit: Option<Spanned<Bytes>>,
    spool_limit: Option<Spanned<Bytes>>,
    state_dir: Option<Spanned<String>>,
}
#[derive(Default, Deserialize)]
#[serde(default, deny_unknown_fields)]
struct Ui {
    color: Option<Spanned<Color>>,
}
#[derive(Default, Deserialize)]
#[serde(default, deny_unknown_fields)]
struct Language {
    syntax: Option<Spanned<String>>,
}
#[derive(Default, Deserialize)]
#[serde(default, deny_unknown_fields)]
struct Progress {
    delay_ms: Option<Spanned<u64>>,
    interval_ms: Option<Spanned<u64>>,
}
#[derive(Default, Deserialize)]
#[serde(default, deny_unknown_fields)]
struct Telemetry {
    enabled: Option<Spanned<bool>>,
    backend: Option<Spanned<TelemetryBackend>>,
    path: Option<Spanned<String>>,
    queue_capacity: Option<Spanned<usize>>,
    batch_size: Option<Spanned<usize>>,
    flush_interval_ms: Option<Spanned<u64>>,
    busy_timeout_ms: Option<Spanned<u64>>,
    max_sessions: Option<Spanned<usize>>,
    max_records_per_session: Option<Spanned<usize>>,
    synchronous_full: Option<Spanned<bool>>,
    sample_per_million: Option<Spanned<u32>>,
    max_database_bytes: Option<Spanned<Bytes>>,
}
