use std::collections::{BTreeMap, HashSet};
use std::fmt;
use std::sync::Arc;

use regex::Regex;
use url::Url;

use crate::syntax::{Diagnostic, Script, Span, StatementKind};

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
        let mut trace = crate::telemetry::span("compile");
        let mut candidate = self.clone();
        let mut program = Program::default();
        let mut errors = Vec::new();
        let mut validated_patterns = HashSet::new();
        for statement in &script.statements {
            if let Err(message) = candidate.lower(
                &statement.kind,
                statement.span,
                &mut program,
                &mut validated_patterns,
            ) {
                errors.push(Diagnostic {
                    message,
                    span: statement.span,
                });
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
        statement: &StatementKind,
        span: Span,
        program: &mut Program,
        validated_patterns: &mut HashSet<String>,
    ) -> Result<(), String> {
        match statement {
            StatementKind::Comment(_) => Ok(()),
            StatementKind::Endpoint { name, value } => {
                validate_name(name)?;
                let backend = self.endpoint(value)?;
                Arc::make_mut(&mut self.endpoints).insert(name.clone(), backend);
                Ok(())
            }
            StatementKind::Alias { name, value } => {
                validate_name(name)?;
                let alias = self.expand(value)?;
                if alias.path.contains('<') || alias.path.contains('>') {
                    return Err("path aliases cannot contain a selector".into());
                }
                Arc::make_mut(&mut self.aliases).insert(name.clone(), alias);
                Ok(())
            }
            StatementKind::Copy {
                source,
                destination,
            } => {
                let mut source = self.location(source)?;
                let destination = self.location(destination)?;
                if matches!(destination.backend, Backend::Http { .. } | Backend::Path) {
                    return Err("HTTP(S) and PATH endpoints are read-only".into());
                }
                if destination.path.contains('<') || destination.path.contains('>') {
                    return Err("selectors are only allowed in the source".into());
                }
                let pattern = take_selector(&mut source.path, validated_patterns)?;
                if pattern.is_some()
                    && matches!(source.backend, Backend::Http { .. } | Backend::Path)
                {
                    return Err("selectors require a directory source, not HTTP(S) or PATH".into());
                }
                if pattern.is_some() && !destination.path.ends_with('/') {
                    return Err("a selector requires a destination ending in '/'".into());
                }
                program.operations.push(CopyOp {
                    source,
                    destination,
                    pattern,
                    span,
                });
                Ok(())
            }
        }
    }

    fn endpoint(&self, value: &str) -> Result<Backend, String> {
        match value {
            "$local" => return Ok(Backend::Local),
            "$wsl" => return Ok(Backend::Wsl),
            "$path" => return Ok(Backend::Path),
            "$env" => return Ok(Backend::Env),
            _ => {}
        }
        if let Some(backend) = self.endpoints.get(value) {
            return Ok(backend.clone());
        }
        let url = Url::parse(value).map_err(|_| format!("unknown endpoint '{value}'"))?;
        if url.host_str().is_none() {
            return Err("an endpoint URL must have a host".into());
        }
        if url.password().is_some() || url.query().is_some() || url.fragment().is_some() {
            return Err("endpoint URLs cannot include passwords, queries or fragments".into());
        }
        match url.scheme() {
            "http" | "https" => {
                if !url.username().is_empty() {
                    return Err(
                        "HTTP endpoint credentials belong in the transport configuration".into(),
                    );
                }
                Ok(Backend::Http {
                    base: url.to_string(),
                })
            }
            "ssh" => {
                if !matches!(url.path(), "" | "/") {
                    return Err("SSH endpoint paths belong after '::', not in the URL".into());
                }
                let host = url.host_str().unwrap().to_string();
                let user = (!url.username().is_empty()).then(|| url.username().to_string());
                if host.starts_with('-') || user.as_ref().is_some_and(|user| user.starts_with('-'))
                {
                    return Err("SSH host and user must not begin with '-'".into());
                }
                Ok(Backend::Ssh {
                    host,
                    user,
                    port: url.port(),
                })
            }
            scheme => Err(format!("unsupported endpoint scheme '{scheme}'")),
        }
    }

    fn location(&self, value: &str) -> Result<Location, String> {
        let alias = self.expand(value)?;
        Ok(Location {
            backend: alias.backend.unwrap_or(Backend::Local),
            path: alias.path,
        })
    }

    fn expand(&self, value: &str) -> Result<Alias, String> {
        if value.is_empty() || value.contains('\0') {
            return Err("paths must be nonempty and cannot contain NUL".into());
        }
        let (mut backend, path) = match value.split_once("::") {
            Some((name, path)) => (Some(self.endpoint(name)?), path),
            None => (None, value),
        };
        if path.is_empty() {
            return Err("a qualified path must not be empty".into());
        }
        if path.contains("::") {
            return Err("a path may only have one endpoint qualifier".into());
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
                return Err(format!("path alias '{name}' has a conflicting endpoint"));
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
