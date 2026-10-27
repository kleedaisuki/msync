#[cfg(windows)]
pub(super) fn validate_case_policy(
    path: &std::path::Path,
    span: crate::syntax::Span,
) -> super::error::Result<()> {
    crate::io::windows::require_case_insensitive_ancestors(path).map_err(|cause|
        super::error::ExecutionError::caused("cannot establish native resource case policy", span, cause)
            .help("The default Windows manager will not merge names that the filesystem treats as distinct. No files have been published."))
}

#[cfg(not(windows))]
pub(super) fn validate_case_policy(
    _: &std::path::Path,
    _: crate::syntax::Span,
) -> super::error::Result<()> {
    Ok(())
}
