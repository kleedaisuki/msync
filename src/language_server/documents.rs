use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use line_index::{LineIndex, TextSize, WideEncoding, WideLineCol};
use lsp_types::{Position, Range, TextDocumentContentChangeEvent, Uri};

use crate::language;
use crate::syntax::{Report, Span};
use crate::tooling::SymbolIndex;

pub(super) const MAX_DOCUMENT_BYTES: usize = 8 * 1024 * 1024;
static NEXT_GENERATION: AtomicU64 = AtomicU64::new(0);

pub(super) struct Text {
    pub source: String,
    pub lines: LineIndex,
}

impl Text {
    pub fn new(source: String) -> Self {
        // LSP accepts lone CR line endings; replacing only the line-index copy
        // preserves all original source bytes and UTF-8/UTF-16 offsets.
        if !source.contains('\r') {
            return Self {
                lines: LineIndex::new(&source),
                source,
            };
        }
        let mut bytes = source.as_bytes().to_vec();
        for index in 0..bytes.len() {
            if bytes[index] == b'\r' && bytes.get(index + 1) != Some(&b'\n') {
                bytes[index] = b'\n';
            }
        }
        let indexed = String::from_utf8(bytes).expect("only ASCII line terminators changed");
        Self {
            lines: LineIndex::new(&indexed),
            source,
        }
    }

    pub fn position(&self, offset: usize) -> Position {
        let mut offset = offset.min(self.source.len());
        while !self.source.is_char_boundary(offset) {
            offset -= 1;
        }
        let native = self.lines.line_col(TextSize::from(offset as u32));
        let wide = self
            .lines
            .to_wide(WideEncoding::Utf16, native)
            .expect("valid source offset");
        Position::new(wide.line, wide.col)
    }

    pub fn offset(&self, position: Position) -> Option<usize> {
        let line = self.lines.line(position.line)?;
        let start = u32::from(line.start()) as usize;
        let end = u32::from(line.end()) as usize;
        let content = self.source.get(start..end)?.trim_end_matches(['\r', '\n']);
        let col = position
            .character
            .min(content.encode_utf16().count() as u32);
        let native = self.lines.to_utf8(
            WideEncoding::Utf16,
            WideLineCol {
                line: position.line,
                col,
            },
        )?;
        let offset = u32::from(self.lines.offset(native)?) as usize;
        (offset <= start + content.len() && self.source.is_char_boundary(offset)).then_some(offset)
    }

    pub fn range(&self, span: Span) -> Range {
        Range::new(
            self.position(span.start),
            self.position(span.end.max(span.start)),
        )
    }

    pub fn end(&self) -> Position {
        self.position(self.source.len())
    }
}

pub(super) struct OpenDocument {
    pub text: Text,
    pub version: i32,
    pub dirty: bool,
    pub generation: u64,
    syntax: Option<Syntax>,
}

pub(super) struct Syntax {
    pub index: SymbolIndex,
    pub document: language::Document,
    pub reports: Vec<Report>,
}

impl OpenDocument {
    pub fn new(source: String, version: i32) -> Self {
        Self {
            text: Text::new(source),
            version,
            dirty: true,
            generation: NEXT_GENERATION.fetch_add(1, Ordering::Relaxed),
            syntax: None,
        }
    }

    pub fn syntax(&mut self) -> &Syntax {
        self.syntax.get_or_insert_with(|| {
            let (document, mut reports) = language::parse_recovering(&self.text.source);
            if let Err(mut version_reports) =
                language::select_version(&self.text.source, None, language::Version::default())
            {
                reports.append(&mut version_reports);
            }
            Syntax {
                index: SymbolIndex::new(&document),
                document,
                reports,
            }
        })
    }

    pub fn apply(
        &mut self,
        version: i32,
        changes: Vec<TextDocumentContentChangeEvent>,
    ) -> Result<bool, String> {
        if version <= self.version {
            return Ok(false);
        }
        let mut candidate = Text {
            source: self.text.source.clone(),
            lines: self.text.lines.clone(),
        };
        for change in changes {
            let Some(range) = change.range else {
                candidate = Text::new(change.text);
                continue;
            };
            let start = candidate
                .offset(range.start)
                .ok_or("edit starts outside a UTF-16 character boundary")?;
            let end = candidate
                .offset(range.end)
                .ok_or("edit ends outside a UTF-16 character boundary")?;
            if start > end {
                return Err("edit range is reversed".into());
            }
            candidate.source.replace_range(start..end, &change.text);
            candidate = Text::new(candidate.source);
        }
        self.text = candidate;
        self.version = version;
        self.generation = NEXT_GENERATION.fetch_add(1, Ordering::Relaxed);
        self.dirty = true;
        self.syntax = None;
        Ok(true)
    }
}

pub(super) struct Documents {
    pub open: BTreeMap<Uri, OpenDocument>,
    pub root: PathBuf,
}

impl Documents {
    pub fn path(&self, uri: &Uri) -> Option<PathBuf> {
        url::Url::parse(uri.as_str()).ok()?.to_file_path().ok()
    }

    pub fn context(&self, uri: &Uri) -> crate::compiler::CompileContext {
        let source_file = self.path(uri);
        let pwd = self.root.clone();
        crate::compiler::CompileContext { pwd, source_file }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn utf16_round_trips_utf8_boundaries_and_clamps_line_ends() {
        let text = Text::new("a😀可莉\r\nx\ry".into());
        for offset in text
            .source
            .char_indices()
            .map(|(index, _)| index)
            .chain([text.source.len()])
        {
            if matches!(text.source.as_bytes().get(offset), Some(b'\r' | b'\n')) {
                continue;
            }
            assert_eq!(text.offset(text.position(offset)), Some(offset));
        }
        assert_eq!(text.offset(Position::new(0, 2)), None);
        assert_eq!(text.offset(Position::new(0, 999)), Some("a😀可莉".len()));
        assert_eq!(
            text.offset(Position::new(2, 0)),
            Some("a😀可莉\r\nx\r".len())
        );
    }

    #[test]
    fn incremental_edits_are_sequential_atomic_and_versioned() {
        let mut document = OpenDocument::new("name = \"😀\"\n".into(), 1);
        let edit = TextDocumentContentChangeEvent {
            range: Some(Range::new(Position::new(0, 8), Position::new(0, 10))),
            range_length: None,
            text: "可莉".into(),
        };
        assert!(document.apply(2, vec![edit.clone()]).unwrap());
        assert_eq!(document.text.source, "name = \"可莉\"\n");
        assert!(!document.apply(1, vec![edit]).unwrap());
        let before = document.text.source.clone();
        let invalid = TextDocumentContentChangeEvent {
            range: Some(Range::new(Position::new(99, 0), Position::new(99, 0))),
            range_length: None,
            text: "bad".into(),
        };
        assert!(document.apply(3, vec![invalid]).is_err());
        assert_eq!(document.text.source, before);
        assert_eq!(document.version, 2);
    }
}
