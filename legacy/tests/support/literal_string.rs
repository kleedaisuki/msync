//! Canonical quoted msync source; deliberately not JSON or shell encoding.

pub fn quoted(value: &str) -> String {
    let mut source = String::new();
    msync::language::literal::write_string(&mut source, value);
    source
}
