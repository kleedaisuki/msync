//! Expanded 0.5 IR shares the object validator and block interpreter.

use serde::{Deserialize, Serialize};

use super::{CompileContext, Contextual, CopyOp, Program, Report};
use crate::compiler::structured::Session;
use crate::context::{Context, ContextualizedModule};
use crate::hir::{Argument, Input, InstructionKind, Output};
use crate::object::ObjectModule;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Structured {
    pub module: ObjectModule,
    pub context: Option<CompileContext>,
}

impl Structured {
    pub fn relocatable(module: ObjectModule) -> Result<Self, String> {
        module.validate().map_err(|error| error.to_string())?;
        Ok(Self {
            module,
            context: None,
        })
    }

    pub fn validate(&self) -> Result<(), String> {
        self.module.validate().map_err(|error| error.to_string())?;
        if let Some(context) = &self.context {
            context
                .validate()
                .map_err(|error| error.diagnostic.message)?;
            if !self.module.relocations.is_empty() {
                return Err("contextual structured IR cannot retain context relocations".into());
            }
        }
        Ok(())
    }

    pub fn contextualize(
        &self,
        context: &CompileContext,
        session: &mut Session,
    ) -> Result<ContextualizedModule, String> {
        self.validate()?;
        crate::context::contextualize(
            &self.module,
            &Context::new(self.context.as_ref().unwrap_or(context)),
            session,
        )
        .map_err(|error| error.to_string())
    }

    pub fn bind(&self, context: &CompileContext) -> Result<Self, String> {
        if self.context.is_some() {
            self.validate()?;
            return Ok(self.clone());
        }
        let mut bound = self.contextualize(context, &mut Session::new())?;
        for node in &mut bound.program.body.instructions {
            let span = node.span;
            let freeze = |location: &mut crate::endpoint::Location, source: bool| {
                super::freeze(location, source, context, span).map_err(|reports| {
                    reports
                        .into_iter()
                        .map(|report| report.diagnostic.message)
                        .collect::<Vec<_>>()
                        .join("; ")
                })
            };
            match &mut node.kind {
                InstructionKind::Transfer {
                    source,
                    destination,
                } => {
                    if let Input::File { location, .. } = source {
                        freeze(location, true)?;
                    }
                    if let Output::File { location, .. } = destination {
                        freeze(location, false)?;
                    }
                }
                InstructionKind::Spawn {
                    executable,
                    arguments,
                    ..
                } => {
                    freeze(executable, true)?;
                    for argument in arguments {
                        if let Argument::Path(location) = argument {
                            freeze(location, true)?;
                        }
                    }
                }
                _ => {}
            }
        }
        let module = ObjectModule::from_parts(
            bound.program,
            Vec::new(),
            Vec::new(),
            bound.source,
            bound.sources,
            bound.origins,
            Default::default(),
            self.module.main_module.clone(),
        )
        .map_err(|error| error.to_string())?;
        Ok(Self {
            module,
            context: Some(context.clone()),
        })
    }

    /// Compatibility projection for callers of the original finite-copy API.
    /// Programs, literals and barriers must use the structured interpreter.
    pub(super) fn legacy(&self, context: &CompileContext) -> Result<Contextual, Vec<Report>> {
        let bound = self
            .bind(context)
            .map_err(|message| vec![Report::new(message, Default::default())])?;
        let mut operations = Vec::new();
        for node in &bound.module.program.body.instructions {
            let InstructionKind::Transfer {
                source:
                    Input::File {
                        location: source,
                        pattern,
                    },
                destination:
                    Output::File {
                        location: destination,
                        mode,
                    },
            } = &node.kind
            else {
                return Err(vec![Report::new(
                    "structured IR requires the block interpreter; use Structured::contextualize",
                    node.span,
                )]);
            };
            if *mode != crate::language::DestinationMode::Exact {
                return Err(vec![Report::new(
                    "structured destination mode requires the block interpreter",
                    node.span,
                )]);
            }
            operations.push(CopyOp {
                source: source.clone(),
                destination: destination.clone(),
                pattern: pattern.clone(),
                span: node.span,
            });
        }
        Contextual::capture(&Program { operations }, bound.context.as_ref().unwrap())
    }
}
