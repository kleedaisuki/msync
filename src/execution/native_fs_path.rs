//! Endpoint-aware, purely lexical filesystem path operations.

use crate::endpoint::{Backend, Location};
use crate::syntax::Span;

use super::super::error::{ExecutionError, Result};

#[derive(Debug, PartialEq, Eq)]
pub(super) enum PathValue {
    Path(Location),
    Paths(Vec<Location>),
    Bool(bool),
    String(String),
}

pub(super) fn call(name: &str, args: &[Location], span: Span) -> Result<Option<PathValue>> {
    let binary = matches!(
        name,
        "lexically_relative"
            | "lexically_proximate"
            | "replace_filename"
            | "replace_extension"
            | "append"
            | "concat"
    );
    let unary = matches!(
        name,
        "root_name"
            | "root_directory"
            | "root_path"
            | "relative_path"
            | "parent_path"
            | "filename"
            | "stem"
            | "extension"
            | "lexically_normal"
            | "remove_filename"
            | "make_preferred"
            | "string"
            | "generic_string"
            | "native"
            | "empty"
            | "path_empty"
            | "components"
            | "is_absolute"
            | "is_relative"
            | "has_root_name"
            | "has_root_directory"
            | "has_root_path"
            | "has_relative_path"
            | "has_parent_path"
            | "has_filename"
            | "has_stem"
            | "has_extension"
    );
    if !unary && !binary {
        return Ok(None);
    }
    let arity = if binary { 2 } else { 1 };
    if args.len() != arity {
        return Err(ExecutionError::new(
            format!("fs::{name} requires {arity} path arguments"),
            span,
        ));
    }
    let location = &args[0];
    let path = LexicalPath::new(&location.path, windows(&location.backend));
    if name == "components" {
        let mut components = Vec::new();
        if !path.root_name.is_empty() {
            components.push(path.root_name.to_owned());
        }
        if path.root_directory {
            components.push(if path.relative.is_empty() {
                location.path[path.root_name.len()..].to_owned()
            } else {
                path.separator().to_string()
            });
        }
        components.extend(path.parts().map(str::to_owned));
        if !path.relative.is_empty()
            && path
                .source
                .ends_with(|character| path.is_separator(character))
        {
            components.push(String::new());
        }
        return Ok(Some(PathValue::Paths(
            components
                .into_iter()
                .map(|path| Location {
                    backend: location.backend.clone(),
                    path,
                })
                .collect(),
        )));
    }
    if matches!(name, "string" | "native" | "generic_string") {
        let text = if name == "generic_string" && path.windows {
            location.path.replace('\\', "/")
        } else {
            location.path.clone()
        };
        return Ok(Some(PathValue::String(text)));
    }
    let boolean = match name {
        "empty" | "path_empty" | "components" => Some(location.path.is_empty()),
        "is_absolute" => Some(path.absolute()),
        "is_relative" => Some(!path.absolute()),
        "has_root_name" => Some(!path.root_name.is_empty()),
        "has_root_directory" => Some(path.root_directory),
        "has_root_path" => Some(!path.root_name.is_empty() || path.root_directory),
        "has_relative_path" => Some(!path.relative.is_empty()),
        "has_parent_path" => Some(!path.parent().is_empty()),
        "has_filename" => Some(!path.filename().is_empty()),
        "has_stem" => Some(!stem_extension(path.filename()).0.is_empty()),
        "has_extension" => Some(!stem_extension(path.filename()).1.is_empty()),
        _ => None,
    };
    if let Some(value) = boolean {
        return Ok(Some(PathValue::Bool(value)));
    }
    let value = match name {
        "root_name" => path.root_name.to_owned(),
        "root_directory" => {
            if path.root_directory {
                path.separator().to_string()
            } else {
                String::new()
            }
        }
        "root_path" => path.root(),
        "relative_path" => path.relative.to_owned(),
        "parent_path" => path.parent(),
        "remove_filename" => path.remove_filename(),
        "filename" => path.filename().to_owned(),
        "stem" => stem_extension(path.filename()).0.to_owned(),
        "extension" => stem_extension(path.filename()).1.to_owned(),
        "lexically_normal" => path.normal(),
        "make_preferred" => path.preferred(),
        _ => binary_path(name, &path, location, &args[1], span)?,
    };
    Ok(Some(PathValue::Path(Location {
        backend: location.backend.clone(),
        path: value,
    })))
}

fn windows(backend: &Backend) -> bool {
    cfg!(windows) && matches!(backend, Backend::Local | Backend::Env | Backend::Path)
}

struct LexicalPath<'a> {
    source: &'a str,
    windows: bool,
    root_name: &'a str,
    root_directory: bool,
    relative: &'a str,
}

impl<'a> LexicalPath<'a> {
    fn new(source: &'a str, windows: bool) -> Self {
        let separator = |character: char| character == '/' || windows && character == '\\';
        let root_end = root_end(source, windows);
        let tail = &source[root_end..];
        Self {
            source,
            windows,
            root_name: &source[..root_end],
            root_directory: tail.starts_with(separator),
            relative: tail.trim_start_matches(separator),
        }
    }

    fn separator(&self) -> char {
        if self.windows { '\\' } else { '/' }
    }
    fn is_separator(&self, character: char) -> bool {
        character == '/' || self.windows && character == '\\'
    }
    fn absolute(&self) -> bool {
        self.root_directory && (!self.windows || !self.root_name.is_empty())
    }
    fn root(&self) -> String {
        let mut root = self.root_name.to_owned();
        if self.root_directory {
            root.push(self.separator());
        }
        root
    }
    fn filename(&self) -> &str {
        self.relative
            .rsplit(|character| self.is_separator(character))
            .next()
            .unwrap_or_default()
    }
    fn parent(&self) -> String {
        if self.relative.is_empty() {
            return self.source.to_owned();
        }
        let parent = self.source[..self.source.len() - self.filename().len()]
            .trim_end_matches(|character| self.is_separator(character));
        if parent.len() < self.root_name.len() || parent.is_empty() && self.root_directory {
            self.root()
        } else {
            parent.to_owned()
        }
    }
    fn remove_filename(&self) -> String {
        self.source[..self.source.len() - self.filename().len()].to_owned()
    }
    fn preferred(&self) -> String {
        if self.windows {
            self.source.replace('/', "\\")
        } else {
            self.source.to_owned()
        }
    }
    fn parts(&self) -> impl DoubleEndedIterator<Item = &str> {
        self.relative
            .split(|character| self.is_separator(character))
            .filter(|part| !part.is_empty())
    }
    fn normal(&self) -> String {
        if self.source.is_empty() || self.relative.is_empty() {
            return self.preferred();
        }
        let mut parts = Vec::new();
        for part in self.parts() {
            match part {
                "." => {}
                ".." if parts.last().is_some_and(|last| *last != "..") => {
                    parts.pop();
                }
                ".." if self.root_directory => {}
                _ => parts.push(part),
            }
        }
        let mut result = self.root();
        result.push_str(&parts.join(&self.separator().to_string()));
        if result.is_empty() {
            return ".".to_owned();
        }
        let trailing = self
            .source
            .ends_with(|character| self.is_separator(character))
            || self
                .parts()
                .next_back()
                .is_some_and(|part| matches!(part, "." | ".."));
        if trailing && parts.last().is_some_and(|part| *part != "..") {
            result.push(self.separator());
        }
        result
    }
}

fn root_end(source: &str, windows: bool) -> usize {
    if !windows {
        return 0;
    }
    let bytes = source.as_bytes();
    if bytes.len() >= 2 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':' {
        return 2;
    }
    let separator = |byte: u8| matches!(byte, b'/' | b'\\');
    if bytes.len() < 3 || !separator(bytes[0]) || !separator(bytes[1]) || separator(bytes[2]) {
        return 0;
    }
    bytes[2..]
        .iter()
        .position(|byte| separator(*byte))
        .map_or(source.len(), |index| index + 2)
}

fn stem_extension(filename: &str) -> (&str, &str) {
    if matches!(filename, "." | "..") {
        return (filename, "");
    }
    match filename.rfind('.') {
        Some(index) if index != 0 => (&filename[..index], &filename[index..]),
        _ => (filename, ""),
    }
}

fn binary_path(
    name: &str,
    left: &LexicalPath<'_>,
    location: &Location,
    right: &Location,
    span: Span,
) -> Result<String> {
    if matches!(name, "lexically_relative" | "lexically_proximate") {
        let result = if location.backend == right.backend {
            relative(left, &LexicalPath::new(&right.path, left.windows))
        } else {
            String::new()
        };
        return Ok(if name == "lexically_proximate" && result.is_empty() {
            location.path.clone()
        } else {
            result
        });
    }
    if matches!(name, "concat") {
        return Ok(format!("{}{}", left.source, right.path));
    }
    let value = match name {
        "replace_extension" => {
            let (_, extension) = stem_extension(left.filename());
            let mut result = left.source[..left.source.len() - extension.len()].to_owned();
            if !right.path.is_empty() && !right.path.starts_with('.') {
                result.push('.');
            }
            result.push_str(&right.path);
            result
        }
        "replace_filename" => append(
            &LexicalPath::new(&left.remove_filename(), left.windows),
            &right.path,
        ),
        "append" => append(left, &right.path),
        _ => {
            return Err(ExecutionError::new(
                format!("unknown lexical filesystem operation {name}"),
                span,
            ));
        }
    };
    Ok(value)
}

fn append(left: &LexicalPath<'_>, right: &str) -> String {
    let right_path = LexicalPath::new(right, left.windows);
    if right_path.absolute()
        || !right_path.root_name.is_empty() && right_path.root_name != left.root_name
    {
        return right.to_owned();
    }
    if right_path.root_directory {
        return format!("{}{}", left.root_name, &right[right_path.root_name.len()..]);
    }
    let mut result = left.source.to_owned();
    if !result.is_empty()
        && !result.ends_with(|character| left.is_separator(character))
        && !left.relative.is_empty()
    {
        result.push(left.separator());
    }
    result.push_str(&right[right_path.root_name.len()..]);
    result
}

fn relative(left: &LexicalPath<'_>, base: &LexicalPath<'_>) -> String {
    if left.root_name != base.root_name
        || left.absolute() != base.absolute()
        || !left.root_directory && base.root_directory
    {
        return String::new();
    }
    let parts: Vec<_> = left.parts().collect();
    let base_parts: Vec<_> = base.parts().collect();
    let common = parts
        .iter()
        .zip(&base_parts)
        .take_while(|(left, right)| left == right)
        .count();
    let mut count: isize = 0;
    for part in &base_parts[common..] {
        count += match *part {
            "." => 0,
            ".." => -1,
            _ => 1,
        };
    }
    if count < 0 {
        return String::new();
    }
    let mut result = vec![".."; count as usize];
    result.extend_from_slice(&parts[common..]);
    if result.is_empty() {
        ".".to_owned()
    } else {
        let mut result = result.join(&left.separator().to_string());
        if left
            .source
            .ends_with(|character| left.is_separator(character))
        {
            result.push(left.separator());
        }
        result
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn posix(path: &str) -> LexicalPath<'_> {
        LexicalPath::new(path, false)
    }

    #[test]
    fn cpp_lexical_examples() {
        for (input, normal) in [
            ("a/./b/..", "a/"),
            ("a/./b/../", "a/"),
            ("/../../a", "/a"),
            ("a/..", "."),
            ("../a/..", ".."),
            ("a/.", "a/"),
            ("", ""),
            ("/", "/"),
        ] {
            assert_eq!(posix(input).normal(), normal, "{input}");
        }
        assert_eq!(posix("a/b/").filename(), "");
        assert_eq!(posix("a/b/").parent(), "a/b");
        assert_eq!(posix("/").parent(), "/");
        assert_eq!(stem_extension(".profile"), (".profile", ""));
        assert_eq!(stem_extension("foo."), ("foo", "."));
        assert_eq!(relative(&posix("/a/d"), &posix("/a/b/c")), "../../d");
        assert_eq!(relative(&posix("a"), &posix("../b")), "a");
        assert_eq!(relative(&posix("a"), &posix("../..")), "");
        assert_eq!(relative(&posix("a/b/"), &posix("a")), "b/");
        assert_eq!(posix("a/b").remove_filename(), "a/");
        assert_eq!(posix("//a//b").parent(), "//a");
        let path = Location {
            backend: Backend::Wsl,
            path: "/a//b/".to_owned(),
        };
        let parts = ["/", "a", "b", ""]
            .map(|path| Location {
                backend: Backend::Wsl,
                path: path.to_owned(),
            })
            .to_vec();
        assert_eq!(
            call("components", &[path], Span::default()).unwrap(),
            Some(PathValue::Paths(parts))
        );
    }

    #[test]
    fn dispatch_retains_endpoint_and_preserves_string_data() {
        let backend = Backend::Ssh {
            host: "forge".to_owned(),
            user: None,
            port: None,
        };
        let path = Location {
            backend: backend.clone(),
            path: "/a/β.tar.gz".to_owned(),
        };
        assert_eq!(
            call("stem", std::slice::from_ref(&path), Span::default()).unwrap(),
            Some(PathValue::Path(Location {
                backend: backend.clone(),
                path: "β.tar".to_owned()
            }))
        );
        assert_eq!(
            call("string", std::slice::from_ref(&path), Span::default()).unwrap(),
            Some(PathValue::String(path.path.clone()))
        );
        let base = Location {
            backend: Backend::Local,
            path: "/a".to_owned(),
        };
        assert_eq!(
            call("lexically_relative", &[path, base], Span::default()).unwrap(),
            Some(PathValue::Path(Location {
                backend,
                path: String::new()
            }))
        );
        assert!(call("filename", &[], Span::default()).is_err());
        assert_eq!(
            call("not_a_path_operation", &[], Span::default()).unwrap(),
            None
        );
    }

    #[test]
    fn windows_and_remote_posix_have_distinct_grammars() {
        let drive = LexicalPath::new("C:\\work\\..\\file.txt", true);
        assert_eq!(drive.root(), "C:\\");
        assert!(drive.absolute());
        assert_eq!(drive.normal(), "C:\\file.txt");
        assert_eq!(LexicalPath::new("C:file.txt", true).root_name, "C:");
        assert!(!LexicalPath::new("C:file.txt", true).absolute());
        assert_eq!(
            LexicalPath::new("\\\\server\\share\\file", true).root_name,
            "\\\\server"
        );
        assert_eq!(posix("a\\b").filename(), "a\\b");
        assert_eq!(
            append(&LexicalPath::new("C:\\work", true), "\\other"),
            "C:\\other"
        );
    }

    proptest::proptest! {
        #[test]
        fn normalization_is_idempotent(path in "[a-z./]{0,160}") {
            let normal = posix(&path).normal();
            proptest::prop_assert_eq!(posix(&normal).normal(), normal);
        }
    }
}
