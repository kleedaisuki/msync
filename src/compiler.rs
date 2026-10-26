use std::collections::{BTreeMap, HashSet};
use std::fmt;
use std::sync::Arc;

use regex::Regex;
use url::Url;

use crate::syntax::{
    Diagnostic, OperandSource, Operands, Report, Script, Span, Statement, StatementKind,
};

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Backend {
    Local,
    Wsl,
    Ssh {
        host: String,
        user: Option<String>,
        port: Option<u16>,
    },
    Http {
        base: String,
    },
    Path,
    Env,
    Tcp {
        host: String,
        port: u16,
    },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Location {
    pub backend: Backend,
    pub path: String,
}

#[derive(Clone, Debug)]
pub struct CopyOp {
    pub source: Location,
    pub destination: Location,
    pub pattern: Option<String>,
    pub span: Span,
}

#[derive(Clone, Debug, Default)]
pub struct Program {
    pub operations: Vec<CopyOp>,
}

#[derive(Clone, Debug)]
struct Alias {
    backend: Option<Backend>,
    path: String,
}

#[derive(Clone, Debug, Default)]
pub struct Compiler {
    endpoints: Arc<BTreeMap<String, Backend>>,
    aliases: Arc<BTreeMap<String, Alias>>,
}

impl Compiler {
    pub fn new() -> Self {
        Self::default()
    }

    /// Bindings are committed only if the entire submission compiles.
    pub fn compile(&mut self, script: &Script) -> Result<Program, Vec<Diagnostic>> {
        self.compile_rich(script).map_err(|reports| {
            reports
                .into_iter()
                .map(|report| report.diagnostic)
                .collect()
        })
    }

    pub fn compile_rich(&mut self, script: &Script) -> Result<Program, Vec<Report>> {
        let mut trace = crate::telemetry::span("compile");
        let mut candidate = self.clone();
        let mut program = Program::default();
        let mut errors = Vec::new();
        let mut validated_patterns = HashSet::new();
        for statement in &script.statements {
            if let Err(report) = candidate.lower(statement, &mut program, &mut validated_patterns) {
                errors.push(report);
            }
        }
        trace.metric("operations", program.operations.len() as u64);
        trace.metric("errors", errors.len() as u64);
        if !errors.is_empty() {
            trace.error();
            return Err(errors);
        }
        *self = candidate;
        Ok(program)
    }

    pub fn bindings(&self) -> Vec<String> {
        self.endpoints
            .iter()
            .map(|(name, backend)| format!("{name} => {backend}"))
            .chain(self.aliases.iter().map(|(name, alias)| {
                let prefix = alias
                    .backend
                    .as_ref()
                    .map(|backend| format!("{backend}::"))
                    .unwrap_or_default();
                format!("{name} := {prefix}{}", alias.path)
            }))
            .collect()
    }

    fn lower(
        &mut self,
        statement: &Statement,
        program: &mut Program,
        validated_patterns: &mut HashSet<String>,
    ) -> Result<(), Report> {
        let fallback = Operands {
            left: OperandSource::unmapped(statement.span),
            operator: statement.span,
            right: OperandSource::unmapped(statement.span),
        };
        let operands = statement.operands.as_ref().unwrap_or(&fallback);
        let left = &operands.left;
        let right = &operands.right;
        match &statement.kind {
            StatementKind::Comment(_) => Ok(()),
            StatementKind::Endpoint { name, value } => {
                validate_name(name).map_err(|message| Report::new(message, left.content).help("Use letters, digits, '.', '_' or '-' for a binding name; built-ins begin with '$' and cannot be rebound."))?;
                let backend = self.endpoint(value, right.content)?;
                Arc::make_mut(&mut self.endpoints).insert(name.clone(), backend);
                Ok(())
            }
            StatementKind::Alias { name, value } => {
                validate_name(name).map_err(|message| {
                    Report::new(message, left.content).help(
                        "Use a single path component made of letters, digits, '.', '_' or '-'.",
                    )
                })?;
                let alias = self.expand(value, right)?;
                if alias.path.contains('<') || alias.path.contains('>') {
                    return Err(Report::new("path aliases cannot contain a selector", selector_span(value, right)).help("Define an alias for the directory, then add '/<regex>' when copying from it."));
                }
                Arc::make_mut(&mut self.aliases).insert(name.clone(), alias);
                Ok(())
            }
            StatementKind::Copy {
                source,
                destination,
            } => {
                let source_text = source;
                let destination_text = destination;
                let mut source = self.location(source, left)?;
                let destination = self.location(destination, right)?;
                if matches!(destination.backend, Backend::Http { .. } | Backend::Path) {
                    return Err(Report::new("HTTP(S) and PATH endpoints are read-only", qualifier_span(destination_text, right)).label(right.content, "this is the destination").help("Use this endpoint on the left of '->', or choose a writable destination such as '$pwd::output/'."));
                }
                if destination.path.contains('<') || destination.path.contains('>') {
                    return Err(Report::new("selectors are only allowed in the source", selector_span(destination_text, right)).label(operands.operator, "copies flow from left to right").help("Move the selector to the source and use a destination directory ending in '/'."));
                }
                let pattern = take_selector(&mut source.path, validated_patterns).map_err(|message| Report::new(message, regex_span(source_text, left)).help("Use one final '/<regex>' source component; selectors use Rust regex syntax and match complete entry names."))?;
                if pattern.is_some()
                    && matches!(source.backend, Backend::Http { .. } | Backend::Path)
                {
                    return Err(Report::new("selectors require a directory source, not HTTP(S) or PATH", selector_span(source_text, left)).label(qualifier_span(source_text, left), "this endpoint does not enumerate directories").help("Download or resolve a single file without a selector, or use a local/WSL/SSH directory."));
                }
                if pattern.is_some() && !destination.path.ends_with('/') {
                    let end = right.interval(destination_text.len(), destination_text.len());
                    return Err(Report::new(
                        "a selector requires a destination ending in '/'",
                        end,
                    )
                    .label(
                        selector_span(source_text, left),
                        "this selector may produce multiple entries",
                    )
                    .help(
                        "Append '/' to the destination to preserve each selected entry's name.",
                    ));
                }
                program.operations.push(CopyOp {
                    source,
                    destination,
                    pattern,
                    span: statement.span,
                });
                Ok(())
            }
        }
    }

    fn endpoint(&self, value: &str, span: Span) -> Result<Backend, Report> {
        match value {
            "$local" | "$pwd" => return Ok(Backend::Local),
            "$wsl" => return Ok(Backend::Wsl),
            "$path" => return Ok(Backend::Path),
            "$env" => return Ok(Backend::Env),
            _ => {}
        }
        if let Some(backend) = self.endpoints.get(value) {
            return Ok(backend.clone());
        }
        let url = Url::parse(value).map_err(|_| {
            let mut report = Report::new(format!("unknown endpoint '{value}'"), span).help("Declare an endpoint first with 'name => $local', or use a built-in: $pwd, $local, $wsl, $path, $env.");
            if let Some(name) = self.endpoints.keys().find(|name| edit_distance(name, value) <= 2) {
                report = report.help(format!("A similarly named endpoint is '{name}'."));
            }
            report
        })?;
        if url.host_str().is_none() {
            return Err(Report::new("an endpoint URL must have a host", span)
                .help("Use 'ssh://user@host', 'https://host', or a built-in endpoint."));
        }
        if url.password().is_some() || url.query().is_some() || url.fragment().is_some() {
            return Err(Report::new(
                "endpoint URLs cannot include passwords, queries or fragments",
                span,
            )
            .help("Keep credentials in SSH/curl configuration and put paths after '::'."));
        }
        match url.scheme() {
            "tcp" => {
                if !url.username().is_empty() || !matches!(url.path(), "" | "/") {
                    return Err(Report::new("TCP endpoint URLs cannot include credentials or paths", span).help("Use 'tcp://host:port'; stream framing and direction belong to execution, not the URL path."));
                }
                let port = url.port().ok_or_else(|| {
                    Report::new("TCP endpoints require an explicit port", span)
                        .help("Write a complete endpoint, for example 'tcp://127.0.0.1:9000'.")
                })?;
                Ok(Backend::Tcp {
                    host: match url.host().unwrap() {
                        url::Host::Ipv6(address) => address.to_string(),
                        host => host.to_string(),
                    },
                    port,
                })
            }
            "http" | "https" => {
                if !url.username().is_empty() {
                    return Err(Report::new("HTTP endpoint credentials belong in the transport configuration", span).help("Remove the username from the URL and configure authentication outside the DSL."));
                }
                Ok(Backend::Http {
                    base: url.to_string(),
                })
            }
            "ssh" => {
                if !matches!(url.path(), "" | "/") {
                    return Err(Report::new(
                        "SSH endpoint paths belong after '::', not in the URL",
                        span,
                    )
                    .help("Bind 'server => ssh://user@host', then use 'server::path/to/file'."));
                }
                let host = url.host_str().unwrap().to_string();
                let user = (!url.username().is_empty()).then(|| url.username().to_string());
                if host.starts_with('-') || user.as_ref().is_some_and(|user| user.starts_with('-'))
                {
                    return Err(Report::new("SSH host and user must not begin with '-'", span).help("Use a hostname or SSH configuration alias that does not begin with '-'."));
                }
                Ok(Backend::Ssh {
                    host,
                    user,
                    port: url.port(),
                })
            }
            scheme => Err(
                Report::new(format!("unsupported endpoint scheme '{scheme}'"), span)
                    .help("Supported URL schemes are ssh, http, https and tcp."),
            ),
        }
    }

    fn location(&self, value: &str, site: &OperandSource) -> Result<Location, Report> {
        let alias = self.expand(value, site)?;
        Ok(Location {
            backend: alias.backend.unwrap_or(Backend::Local),
            path: alias.path,
        })
    }

    fn expand(&self, value: &str, site: &OperandSource) -> Result<Alias, Report> {
        let alias = self.expand_inner(value, site)?;
        if matches!(alias.backend, Some(Backend::Tcp { .. })) && alias.path != "/" {
            let start = value.find("::").map_or(0, |index| index + 2);
            return Err(tcp_path_report(site.interval(start, value.len())));
        }
        Ok(alias)
    }

    fn expand_inner(&self, value: &str, site: &OperandSource) -> Result<Alias, Report> {
        if value.is_empty() || value.contains('\0') {
            return Err(Report::new(
                "paths must be nonempty and cannot contain NUL",
                site.content,
            )
            .help("Provide a non-empty filesystem path without NUL characters."));
        }
        let (mut backend, path) = match value.split_once("::") {
            Some((name, path)) => (
                Some(self.endpoint(name, site.interval(0, name.len()))?),
                path,
            ),
            None => (None, value),
        };
        if path.is_empty() {
            if matches!(backend, Some(Backend::Tcp { .. })) {
                return Err(tcp_path_report(site.interval(value.len(), value.len())));
            }
            return Err(Report::new(
                "a qualified path must not be empty",
                site.interval(value.len(), value.len()),
            )
            .help("Add a path after '::'; use '$pwd::.' to refer to the current directory."));
        }
        if path.contains("::") {
            let start = value.len() - path.len() + path.find("::").unwrap();
            return Err(Report::new(
                "a path may only have one endpoint qualifier",
                site.interval(start, start + 2),
            )
            .help("Choose one endpoint before '::' and put only a filesystem path after it."));
        }
        if path == "$pwd" || path.starts_with("$pwd/") || path.starts_with("$pwd\\") {
            if backend
                .as_ref()
                .is_some_and(|backend| !matches!(backend, Backend::Local))
            {
                return Err(Report::new("$pwd denotes the local working directory", qualifier_span(value, site)).label(site.interval(value.len() - path.len(), value.len() - path.len() + 4), "this path is explicitly local").help("Remove the remote qualifier, or use a relative path within that remote endpoint."));
            }
            return Ok(Alias {
                backend: Some(Backend::Local),
                path: format!(".{}", &path[4..]),
            });
        }
        // Binding names are single components; unbound scripts need no path scan.
        let found = if self.aliases.is_empty() {
            None
        } else {
            let name = path.split(['/', '\\']).next().unwrap_or(path);
            self.aliases.get_key_value(name)
        };
        let Some((name, alias)) = found else {
            return Ok(Alias {
                backend,
                path: path.to_string(),
            });
        };
        if let Some(alias_backend) = &alias.backend {
            if backend
                .as_ref()
                .is_some_and(|backend| backend != alias_backend)
            {
                let start = value.len() - path.len();
                return Err(Report::new(format!("path alias '{name}' has a conflicting endpoint"), qualifier_span(value, site)).label(site.interval(start, start + name.len()), format!("this alias is bound to {alias_backend}")).help(format!("Remove the explicit qualifier to use the alias's {alias_backend} endpoint, or define a path-only alias.")));
            }
            backend = Some(alias_backend.clone());
        }
        let suffix = &path[name.len()..];
        let path = if suffix.is_empty() {
            alias.path.clone()
        } else {
            format!("{}{}", alias.path.trim_end_matches(['/', '\\']), suffix)
        };
        Ok(Alias { backend, path })
    }
}

fn tcp_path_report(span: Span) -> Report {
    Report::new("TCP sockets do not have filesystem paths", span)
        .help("Use endpoint::/ for a socket; TCP has no filesystem path.")
}

fn qualifier_span(value: &str, site: &OperandSource) -> Span {
    site.interval(0, value.find("::").unwrap_or(value.len()))
}

fn selector_span(value: &str, site: &OperandSource) -> Span {
    value
        .find('<')
        .map_or(site.content, |start| site.interval(start, value.len()))
}

fn regex_span(value: &str, site: &OperandSource) -> Span {
    value.find('<').map_or(site.content, |start| {
        site.interval(
            start + 1,
            value
                .len()
                .saturating_sub(usize::from(value.ends_with('>'))),
        )
    })
}

fn edit_distance(left: &str, right: &str) -> usize {
    let right = right.chars().collect::<Vec<_>>();
    let mut row = (0..=right.len()).collect::<Vec<_>>();
    for (index, left) in left.chars().enumerate() {
        let mut diagonal = row[0];
        row[0] = index + 1;
        for (column, right) in right.iter().enumerate() {
            let old = row[column + 1];
            row[column + 1] = (row[column] + 1)
                .min(old + 1)
                .min(diagonal + usize::from(left != *right));
            diagonal = old;
        }
    }
    row[right.len()]
}

fn validate_name(name: &str) -> Result<(), String> {
    if name.is_empty()
        || name == "."
        || name == ".."
        || !name
            .chars()
            .all(|ch| ch.is_alphanumeric() || "_.-".contains(ch))
    {
        return Err(format!("invalid binding name '{name}'"));
    }
    Ok(())
}

fn take_selector(
    path: &mut String,
    validated_patterns: &mut HashSet<String>,
) -> Result<Option<String>, String> {
    let Some(start) = path.find('<') else {
        if path.contains('>') {
            return Err("unmatched '>' in source path".into());
        }
        return Ok(None);
    };
    if !path.ends_with('>') || (start != 0 && !path[..start].ends_with('/')) {
        return Err("a selector must be the final source path component '<regex>'".into());
    }
    let pattern = path[start + 1..path.len() - 1].to_string();
    if !validated_patterns.contains(&pattern) {
        Regex::new(&format!("\\A(?:{pattern})\\z"))
            .map_err(|error| format!("invalid selector regex: {error}"))?;
        validated_patterns.insert(pattern.clone());
    }
    path.truncate(start);
    if path.is_empty() {
        path.push('.');
    }
    Ok(Some(pattern))
}

impl fmt::Display for Backend {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Local => f.write_str("$local"),
            Self::Wsl => f.write_str("$wsl"),
            Self::Path => f.write_str("$path"),
            Self::Env => f.write_str("$env"),
            Self::Tcp { host, port } if host.contains(':') && !host.starts_with('[') => {
                write!(f, "tcp://[{host}]:{port}")
            }
            Self::Tcp { host, port } => write!(f, "tcp://{host}:{port}"),
            Self::Http { base } => f.write_str(base),
            Self::Ssh { host, user, port } => {
                f.write_str("ssh://")?;
                if let Some(user) = user {
                    write!(f, "{user}@")?;
                }
                f.write_str(host)?;
                if let Some(port) = port {
                    write!(f, ":{port}")?;
                }
                Ok(())
            }
        }
    }
}

impl fmt::Display for Location {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}::{:?}", self.backend, self.path)
    }
}

impl fmt::Display for Program {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        for (index, operation) in self.operations.iter().enumerate() {
            let mode = if operation.destination.path.ends_with('/') {
                "insert"
            } else {
                "replace"
            };
            write!(f, "{index:04} {mode} {}", operation.source)?;
            if let Some(pattern) = &operation.pattern {
                write!(f, " match {pattern:?}")?;
            }
            writeln!(f, " -> {}", operation.destination)?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cloned_compiler_is_a_snapshot_even_after_mutating_both_binding_maps() {
        let mut original = Compiler::new();
        original
            .compile(&crate::syntax::parse("root := old\nremote => $wsl\n").unwrap())
            .unwrap();
        let expected = original.bindings();
        let mut fork = original.clone();
        fork.compile(&crate::syntax::parse("root := new\nremote => $local\n").unwrap())
            .unwrap();
        assert_eq!(original.bindings(), expected);
        let plan = original
            .compile(&crate::syntax::parse("remote::root/x -> output\n").unwrap())
            .unwrap();
        assert_eq!(plan.operations[0].source.backend, Backend::Wsl);
        assert_eq!(plan.operations[0].source.path, "old/x");
    }

    #[test]
    fn selector_validation_is_deduplicated_without_caching_failures() {
        let mut validated = HashSet::new();
        for _ in 0..3 {
            let mut path = "configs/<.+\\.toml>".to_string();
            assert_eq!(
                take_selector(&mut path, &mut validated).unwrap().as_deref(),
                Some(".+\\.toml")
            );
            assert_eq!(path, "configs/");
        }
        assert_eq!(validated.len(), 1);
        for _ in 0..2 {
            assert!(take_selector(&mut "configs/<[>".to_string(), &mut validated).is_err());
        }
        assert_eq!(validated.len(), 1);
    }

    #[test]
    fn alias_lookup_obeys_component_boundaries_for_both_separators() {
        let mut compiler = Compiler::new();
        let program = compiler.compile(&crate::syntax::parse("a := base\na-long := other\na/file -> out\na\\file -> out\na-long/file -> out\na-longer/file -> out\n").unwrap()).unwrap();
        let paths = program
            .operations
            .iter()
            .map(|operation| operation.source.path.as_str())
            .collect::<Vec<_>>();
        assert_eq!(
            paths,
            ["base/file", "base\\file", "other/file", "a-longer/file"]
        );
    }
}
