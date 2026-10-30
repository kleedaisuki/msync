use std::collections::BTreeSet;

use super::*;
use crate::hir::{Argument, Input, InstructionKind, Output, Region};
use crate::relocatable::{ExportValue, Slot};

pub(super) fn export_key(export: &Export) -> String {
    export.name.clone()
}

pub(super) struct Tables {
    pub symbols: Vec<Symbol>,
    pub imports: Vec<Import>,
    pub constraints: Vec<Constraint>,
}

pub(super) fn derive(module: &ObjectModule) -> Result<Tables, ObjectError> {
    let nodes = nodes(&module.program);
    let by_id = nodes
        .iter()
        .map(|node| (node.id, *node))
        .collect::<BTreeMap<_, _>>();
    let mut table = Builder::default();
    for node in &nodes {
        match &node.kind {
            InstructionKind::Spawn {
                process,
                executable,
                arguments,
                ..
            } => {
                table.symbols.insert(
                    format!("process:{}", process.0),
                    SymbolKind::Process { process: *process },
                );
                table.filesystem(&executable.backend, node.id)?;
                for argument in arguments {
                    if let Argument::Path(location) = argument {
                        table.filesystem(&location.backend, node.id)?;
                    }
                }
            }
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                if let Input::File { location, .. } = source {
                    table.filesystem(&location.backend, node.id)?;
                }
                if let Output::File { location, .. } = destination {
                    table.filesystem(&location.backend, node.id)?;
                }
            }
            _ => {}
        }
    }
    for relocation in &module.relocations {
        let node = by_id
            .get(&relocation.instruction)
            .ok_or_else(|| ObjectError::new("relocation references an undefined instruction"))?;
        let backend = super::validate::slot_backend(node, relocation.slot)?;
        table.context(
            &relocation.path.anchor,
            &backend,
            Some(relocation.instruction),
        )?;
    }
    for export in &module.exports {
        if let ExportValue::Path {
            backend,
            relocation: Some(path),
            ..
        } = &export.value
        {
            table.context(
                &path.anchor,
                backend.as_ref().unwrap_or(&Backend::Local),
                None,
            )?;
        }
    }
    let symbols = table
        .symbols
        .into_iter()
        .map(|(name, kind)| Symbol { name, kind })
        .collect();
    let imports = table
        .imports
        .into_iter()
        .map(|(symbol, (requirement, users))| Import {
            symbol,
            requirement,
            users: users.into_iter().collect(),
        })
        .collect();
    let relocations = module
        .relocations
        .iter()
        .map(|relocation| ((relocation.instruction, relocation.slot), &relocation.path))
        .collect::<BTreeMap<_, _>>();
    let mut constraints = Vec::new();
    candidates(&module.program.body, &relocations, &mut constraints);
    Ok(Tables {
        symbols,
        imports,
        constraints,
    })
}

#[derive(Default)]
struct Builder {
    symbols: BTreeMap<String, SymbolKind>,
    imports: BTreeMap<String, (ImportKind, BTreeSet<InstructionId>)>,
}

impl Builder {
    fn filesystem(
        &mut self,
        backend: &Backend,
        instruction: InstructionId,
    ) -> Result<(), ObjectError> {
        self.capability(
            ImportKind::Endpoint {
                backend: backend.clone(),
            },
            instruction,
        )
    }

    fn capability(
        &mut self,
        requirement: ImportKind,
        instruction: InstructionId,
    ) -> Result<(), ObjectError> {
        let name = format!("capability:{}", json_key(&requirement)?);
        self.symbols
            .insert(name.clone(), SymbolKind::Capability { name: name.clone() });
        self.import(name, requirement, Some(instruction));
        Ok(())
    }

    fn context(
        &mut self,
        anchor: &Anchor,
        backend: &Backend,
        instruction: Option<InstructionId>,
    ) -> Result<(), ObjectError> {
        let requirement = ImportKind::Context {
            anchor: anchor.clone(),
            backend: backend.clone(),
        };
        let name = format!("context:{}", json_key(&requirement)?);
        self.symbols.insert(
            name.clone(),
            SymbolKind::Context {
                anchor: anchor.clone(),
                backend: backend.clone(),
            },
        );
        self.import(name, requirement, instruction);
        Ok(())
    }

    fn import(
        &mut self,
        name: String,
        requirement: ImportKind,
        instruction: Option<InstructionId>,
    ) {
        let (_, users) = self
            .imports
            .entry(name)
            .or_insert_with(|| (requirement, BTreeSet::new()));
        if let Some(instruction) = instruction {
            users.insert(instruction);
        }
    }
}

fn json_key(value: &impl Serialize) -> Result<String, ObjectError> {
    serde_json::to_string(value)
        .map_err(|cause| ObjectError::new(format!("cannot form object symbol: {cause}")))
}

fn candidates(
    region: &Region,
    relocations: &BTreeMap<(InstructionId, Slot), &crate::relocatable::Path>,
    constraints: &mut Vec<Constraint>,
) {
    for block in region.blocks() {
        for pair in block.instructions.windows(2) {
            if !same_destination(&pair[0], &pair[1], relocations) {
                continue;
            }
            if let Some(candidate) =
                crate::hir::rewrite::AdjacentOverwrite::recognize(&pair[0], &pair[1])
            {
                constraints.push(Constraint::GuardedOverwrite {
                    first: candidate.first,
                    last: candidate.last,
                });
            }
        }
    }
}

fn same_destination(
    first: &hir::Instruction,
    last: &hir::Instruction,
    relocations: &BTreeMap<(InstructionId, Slot), &crate::relocatable::Path>,
) -> bool {
    match (
        relocations.get(&(first.id, Slot::Destination)),
        relocations.get(&(last.id, Slot::Destination)),
    ) {
        (Some(first), Some(last)) => first == last,
        (None, None) => true,
        _ => false,
    }
}
