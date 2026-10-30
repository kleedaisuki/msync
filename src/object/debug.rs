use super::*;
use crate::source::{InstructionOrigin, SourceId};
use crate::syntax::Span;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DebugSource {
    pub name: String,
    pub text: String,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DebugSite {
    pub source: usize,
    pub span: Span,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DebugOrigin {
    pub instruction: InstructionId,
    pub site: DebugSite,
    pub definition: DebugSite,
    pub expansion: Vec<DebugSite>,
    pub operands: Option<(DebugSite, DebugSite)>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ExportSite {
    pub name: String,
    pub site: DebugSite,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DebugInfo {
    pub primary: usize,
    pub sources: Vec<DebugSource>,
    pub origins: Vec<DebugOrigin>,
    pub export_sites: Vec<ExportSite>,
}

pub type MaterializedDebug = (
    Arc<Source>,
    SourceMap,
    Origins,
    BTreeMap<String, SourceSpan>,
);

impl DebugInfo {
    pub(super) fn from_parts(
        primary: Arc<Source>,
        sources: SourceMap,
        origins: Origins,
        export_sites: BTreeMap<String, SourceSpan>,
    ) -> Self {
        let mut builder = Builder::default();
        let primary = builder.source(&primary);
        for source in sources.iter() {
            builder.source(source);
        }
        let origins = origins
            .into_iter()
            .map(|(instruction, origin)| DebugOrigin {
                instruction,
                site: builder.site(&origin.site),
                definition: builder.site(&origin.definition),
                expansion: origin
                    .expansion
                    .iter()
                    .map(|site| builder.site(site))
                    .collect(),
                operands: origin
                    .operands
                    .map(|(left, right)| (builder.site(&left), builder.site(&right))),
            })
            .collect();
        let export_sites = export_sites
            .into_iter()
            .map(|(name, site)| ExportSite {
                name,
                site: builder.site(&site),
            })
            .collect();
        Self {
            primary,
            sources: builder.sources,
            origins,
            export_sites,
        }
    }

    pub fn validate(&self) -> Result<(), ObjectError> {
        if self.sources.is_empty() || self.primary >= self.sources.len() {
            return Err(ObjectError::new(
                "object debug table has no valid primary source",
            ));
        }
        for source in &self.sources {
            if source.name.is_empty() || source.name.contains('\0') {
                return Err(ObjectError::new(
                    "object source identity is empty or contains NUL",
                ));
            }
        }
        let mut instructions = std::collections::BTreeSet::new();
        for origin in &self.origins {
            if !instructions.insert(origin.instruction) {
                return Err(ObjectError::new("duplicate object instruction origin"));
            }
            self.validate_site(&origin.site)?;
            self.validate_site(&origin.definition)?;
            for site in &origin.expansion {
                self.validate_site(site)?;
            }
            if let Some((left, right)) = &origin.operands {
                self.validate_site(left)?;
                self.validate_site(right)?;
            }
        }
        let mut exports = std::collections::BTreeSet::new();
        for export in &self.export_sites {
            if !exports.insert(&export.name) {
                return Err(ObjectError::new("duplicate object export debug site"));
            }
            self.validate_site(&export.site)?;
        }
        Ok(())
    }

    fn validate_site(&self, site: &DebugSite) -> Result<(), ObjectError> {
        let source = self
            .sources
            .get(site.source)
            .ok_or_else(|| ObjectError::new("object source-map index is out of range"))?;
        if site.span.start > site.span.end
            || site.span.end > source.text.len()
            || !source.text.is_char_boundary(site.span.start)
            || !source.text.is_char_boundary(site.span.end)
        {
            return Err(ObjectError::new(
                "object source span is not a bounded UTF-8 interval",
            ));
        }
        Ok(())
    }

    pub fn materialize(&self) -> Result<MaterializedDebug, ObjectError> {
        self.validate()?;
        let sources = self
            .sources
            .iter()
            .map(|source| Source::new(source.name.as_str(), source.text.as_str()))
            .collect::<Vec<_>>();
        let mut map = SourceMap::default();
        for source in &sources {
            map.insert(source.clone());
        }
        let site = |site: &DebugSite| sources[site.source].at(site.span);
        let origins = self
            .origins
            .iter()
            .map(|origin| {
                (
                    origin.instruction,
                    InstructionOrigin {
                        site: site(&origin.site),
                        definition: site(&origin.definition),
                        expansion: origin.expansion.iter().map(site).collect(),
                        operands: origin
                            .operands
                            .as_ref()
                            .map(|(left, right)| (site(left), site(right))),
                    },
                )
            })
            .collect();
        let exports = self
            .export_sites
            .iter()
            .map(|export| (export.name.clone(), site(&export.site)))
            .collect();
        Ok((sources[self.primary].clone(), map, origins, exports))
    }
}

#[derive(Default)]
struct Builder {
    sources: Vec<DebugSource>,
    ids: BTreeMap<SourceId, usize>,
}

impl Builder {
    fn source(&mut self, source: &Arc<Source>) -> usize {
        if let Some(index) = self.ids.get(&source.id) {
            return *index;
        }
        let index = self.sources.len();
        self.sources.push(DebugSource {
            name: source.name.to_string(),
            text: source.text.to_string(),
        });
        self.ids.insert(source.id, index);
        index
    }

    fn site(&mut self, site: &SourceSpan) -> DebugSite {
        DebugSite {
            source: self.source(&site.source),
            span: site.span,
        }
    }
}
