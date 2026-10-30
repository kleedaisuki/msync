//! Owned dispatch/debug data shared by source compilations and object contexts.

use std::sync::Arc;

use msync::hir::{Program, graph::PlanGraph};
use msync::pipeline::Compilation;
use msync::source::{LocatedReport, Origins, Source, SourceMap};
use msync::syntax::Report;

pub(super) struct ExecutionUnit {
    pub program: Program,
    pub graph: PlanGraph,
    pub source: Arc<Source>,
    pub sources: SourceMap,
    pub origins: Origins,
    pub obligations: Vec<msync::context::Obligation>,
}

impl ExecutionUnit {
    pub fn locate(&self, report: Report) -> LocatedReport {
        let source = self.sources.get(self.source.id).unwrap_or(&self.source);
        LocatedReport::new(source.clone(), report)
    }
}

impl From<Compilation> for ExecutionUnit {
    fn from(compiled: Compilation) -> Self {
        Self {
            program: compiled.program,
            graph: compiled.graph,
            source: compiled.source,
            sources: compiled.sources,
            origins: compiled.origins,
            obligations: Vec::new(),
        }
    }
}

impl From<msync::context::ContextualizedModule> for ExecutionUnit {
    fn from(linked: msync::context::ContextualizedModule) -> Self {
        Self {
            program: linked.program,
            graph: linked.graph,
            source: linked.source,
            sources: linked.sources,
            origins: linked.origins,
            obligations: linked.obligations,
        }
    }
}
