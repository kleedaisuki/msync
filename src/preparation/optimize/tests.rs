//! Slow baseline sweep oracle retained only for generated differential tests.
use super::*;
use proptest::prelude::*;

fn reference_forward(
    nodes: &mut [Option<Instruction>],
    resources: &HashMap<Location, Resource>,
    epoch: &Arc<[InstructionId]>,
    records: &mut Vec<Rewrite>,
) -> bool {
    let mut changed = false;
    for index in 0..nodes.len() {
        let Some(node) = nodes[index].as_ref() else {
            continue;
        };
        let Input::File { location: read, .. } = source(node) else {
            continue;
        };
        let read_resource = &resources[read];
        let producer = (0..index).rev().find(|previous| {
            nodes[*previous]
                .as_ref()
                .is_some_and(|node| resources[destination(node)].overlaps(read_resource))
        });
        let Some(producer) = producer else { continue };
        let producer_node = nodes[producer].as_ref().expect("retained producer");
        if !resources[destination(producer_node)].same_path(read_resource) {
            continue;
        }
        let replacement = source(producer_node).clone();
        // A literal stream has no file metadata. Once materialized, its file
        // version has a creation mtime that a later file copy must preserve.
        // Replacing that read with a fresh literal would preserve bytes only.
        if matches!(replacement, Input::Literal(_)) {
            continue;
        }
        if value(&replacement) == value(source(node)) {
            continue;
        }
        if let Input::File {
            location: origin, ..
        } = &replacement
        {
            let crosses_snapshot =
                !matches!(origin.backend, Backend::Local) && origin.backend != read.backend;
            if crosses_snapshot || resources[origin].overlaps(&resources[destination(node)]) {
                continue;
            }
            if nodes[producer + 1..index]
                .iter()
                .flatten()
                .any(|node| resources[destination(node)].overlaps(&resources[origin]))
            {
                continue;
            }
        }
        records.push(Rewrite {
            instruction: node.id,
            epoch: epoch.clone(),
            kind: RewriteKind::Forward {
                producer: producer_node.id,
                from: Box::new(value(source(node))),
                to: Box::new(value(&replacement)),
            },
        });
        let InstructionKind::Transfer { source, .. } =
            &mut nodes[index].as_mut().expect("retained consumer").kind
        else {
            unreachable!()
        };
        *source = replacement;
        changed = true;
    }
    changed
}

fn reference_remove_redundant(
    nodes: &mut [Option<Instruction>],
    resources: &HashMap<Location, Resource>,
    permissions: &BTreeMap<InstructionId, Permission>,
    epoch: &Arc<[InstructionId]>,
    records: &mut Vec<Rewrite>,
) -> bool {
    let mut changed = false;
    for index in 0..nodes.len() {
        let Some(node) = nodes[index].as_ref() else {
            continue;
        };
        let Input::File {
            location: origin, ..
        } = source(node)
        else {
            continue;
        };
        let target = &resources[destination(node)];
        let Some(producer) = (0..index).rev().find(|previous| {
            nodes[*previous]
                .as_ref()
                .is_some_and(|previous| resources[destination(previous)].overlaps(target))
        }) else {
            continue;
        };
        let previous = nodes[producer].as_ref().expect("retained write");
        if !permissions[&node.id].overwrite || permissions[&previous.id].result != Some(true) {
            continue;
        }
        let Input::File {
            location: prior, ..
        } = source(previous)
        else {
            continue;
        };
        if !target.same_path(&resources[destination(previous)])
            || !resources[origin].same_path(&resources[prior])
        {
            continue;
        }
        let observed_or_changed = nodes[producer + 1..index].iter().flatten().any(|between| {
            let write = &resources[destination(between)];
            if write.overlaps(&resources[origin]) || write.overlaps(target) { return true; }
            matches!(source(between), Input::File { location, .. }
                if resources[location].overlaps(target) || resources[location].overlaps(&resources[origin]))
        });
        if observed_or_changed {
            continue;
        }
        records.push(Rewrite {
            instruction: node.id,
            epoch: epoch.clone(),
            kind: RewriteKind::RedundantWrite {
                same_as: previous.id,
            },
        });
        nodes[index] = None;
        changed = true;
    }
    changed
}

fn reference_eliminate(
    nodes: &mut [Option<Instruction>],
    resources: &HashMap<Location, Resource>,
    facts: &BTreeMap<InstructionId, OperationFacts>,
    permissions: &BTreeMap<InstructionId, Permission>,
    epoch: &Arc<[InstructionId]>,
    records: &mut Vec<Rewrite>,
) -> bool {
    let mut changed = false;
    for index in (0..nodes.len()).rev() {
        let Some(node) = &nodes[index] else { continue };
        // Creation stamps its containing namespace. Keep that event until the
        // IR can carry its metadata provenance separately from payload copying.
        if facts[&node.id].destination == FileClass::Missing {
            continue;
        }
        if !permissions[&node.id].overwrite || permissions[&node.id].result != Some(true) {
            continue;
        }
        let target = &resources[destination(node)];
        let mut overwritten_by = None;
        for later in nodes[index + 1..].iter().flatten() {
            if let Input::File { location, .. } = source(later)
                && resources[location].overlaps(target)
            {
                break;
            }
            let write = &resources[destination(later)];
            if !write.overlaps(target) {
                continue;
            }
            // Literal publication replaces bytes/mtime but inherits destination
            // permissions. It therefore observes the preceding file version;
            // only a file-valued assignment can kill its metadata as well.
            if write.same_path(target) && matches!(source(later), Input::File { .. }) {
                overwritten_by = Some(later.id);
            }
            break;
        }
        if let Some(overwritten_by) = overwritten_by {
            records.push(Rewrite {
                instruction: node.id,
                epoch: epoch.clone(),
                kind: RewriteKind::DeadWrite { overwritten_by },
            });
            nodes[index] = None;
            changed = true;
        }
    }
    changed
}

fn reference_run(
    original: &hir::Program,
    facts: &BTreeMap<InstructionId, OperationFacts>,
    resources: &HashMap<Location, Resource>,
    unstable: &BTreeSet<EndpointKey>,
) -> (hir::Program, Vec<Rewrite>) {
    let mut output = Vec::new();
    let mut records = Vec::new();
    let mut epoch = Vec::new();
    for node in &original.body.instructions {
        if eligible(node, facts, resources) {
            epoch.push(node.clone());
        } else {
            reference_flush(
                &mut epoch,
                &mut output,
                facts,
                resources,
                unstable,
                &mut records,
            );
            output.push(node.clone());
        }
    }
    reference_flush(
        &mut epoch,
        &mut output,
        facts,
        resources,
        unstable,
        &mut records,
    );
    (
        hir::Program {
            body: Region {
                instructions: output,
            },
        },
        records,
    )
}

fn reference_flush(
    epoch: &mut Vec<Instruction>,
    output: &mut Vec<Instruction>,
    facts: &BTreeMap<InstructionId, OperationFacts>,
    resources: &HashMap<Location, Resource>,
    unstable: &BTreeSet<EndpointKey>,
    records: &mut Vec<Rewrite>,
) {
    if epoch.is_empty() {
        return;
    }
    let original = std::mem::take(epoch);
    let ids = original.iter().map(|node| node.id).collect::<Arc<[_]>>();
    let permissions = permissions(&original, facts, resources, unstable);
    let mut facts = original
        .iter()
        .map(|node| (node.id, facts[&node.id]))
        .collect();
    let mut nodes = original.into_iter().map(Some).collect::<Vec<_>>();
    loop {
        let forwarded = reference_forward(&mut nodes, resources, &ids, records);
        let redundant =
            reference_remove_redundant(&mut nodes, resources, &permissions, &ids, records);
        let coalesced = coalesce_creation(
            &mut nodes,
            resources,
            &mut facts,
            &permissions,
            &ids,
            records,
        );
        let removed =
            reference_eliminate(&mut nodes, resources, &facts, &permissions, &ids, records);
        if !forwarded && !redundant && !coalesced && !removed {
            break;
        }
    }
    output.extend(nodes.into_iter().flatten());
}

fn fixture() -> (
    tempfile::TempDir,
    Vec<Location>,
    HashMap<Location, Resource>,
) {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    std::fs::create_dir_all(&root).unwrap();
    let root = tempfile::Builder::new()
        .prefix("prepare-history-")
        .tempdir_in(root)
        .unwrap();
    let mut locations = Vec::new();
    for index in 0..8 {
        let path = root.path().join(format!("file{index}"));
        std::fs::write(&path, [index as u8]).unwrap();
        locations.push(Location {
            backend: Backend::Local,
            path: path.to_string_lossy().into_owned(),
        });
    }
    let alias = root.path().join("hardlink");
    std::fs::hard_link(&locations[0].path, &alias).unwrap();
    locations.push(Location {
        backend: Backend::Local,
        path: alias.to_string_lossy().into_owned(),
    });
    let mut cache = crate::plan::ResourceSnapshots::default();
    let mut resources = locations
        .iter()
        .map(|location| {
            (
                location.clone(),
                cache.capture(location, location, crate::plan::AccessScope::Exact),
            )
        })
        .collect::<HashMap<_, _>>();
    for index in 0..3 {
        let location = Location {
            backend: Backend::Wsl,
            path: format!("/home/history/file{index}"),
        };
        resources.insert(
            location.clone(),
            crate::plan::logical_remote_resource(&location, crate::plan::AccessScope::Exact)
                .unwrap(),
        );
        locations.push(location);
    }
    let other = resources[&locations[9]].clone();
    resources
        .get_mut(&locations[0])
        .unwrap()
        .prove_disjoint(&other);
    (root, locations, resources)
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(128))]
    #[test]
    fn indexed_history_extrema_match_overlap_scan(indices in prop::collection::vec(0usize..12, 0..80)) {
        let (_root, locations, resources) = fixture();
        let mut history = History::new(indices.len());
        let mut inserted = Vec::new();
        for (id, resource) in indices.into_iter().enumerate() {
            let resource = &resources[&locations[resource]];
            history.insert(resource, id);
            inserted.push(resource);
            for query in resources.values() {
                let matching = inserted.iter().enumerate().filter(|(_, prior)| prior.overlaps(query)).map(|(id, _)| id).collect::<Vec<_>>();
                prop_assert_eq!(history.latest(query), matching.last().copied());
                prop_assert_eq!(history.earliest(query), matching.first().copied());
            }
        }
    }

    #[test]
    fn indexed_rewrites_match_slow_scan_oracle(rows in prop::collection::vec((any::<bool>(), 0usize..12, 0usize..12, any::<bool>()), 0..45)) {
        let (_root, locations, resources) = fixture();
        let mut facts = BTreeMap::new();
        let mut unstable = BTreeSet::new();
        let mut writes = BTreeSet::new();
        let instructions = rows.into_iter().enumerate().map(|(index, (literal, read, write, source_unstable))| {
            let id = InstructionId(index);
            let source = if literal { Input::Literal(Arc::from(if read % 2 == 0 { "A" } else { "B" })) } else { Input::File { location: locations[read].clone(), pattern: None } };
            let destination = locations[write].clone();
            let destination_kind = if writes.insert(write) && write % 3 == 0 { FileClass::Missing } else { FileClass::Regular };
            facts.insert(id, OperationFacts { source: FileClass::Regular, destination: destination_kind });
            if source_unstable { unstable.insert(EndpointKey { instruction: id, role: EndpointRole::Source }); }
            Instruction { id, kind: InstructionKind::Transfer { source, destination: Output::File { location: destination, mode: DestinationMode::Exact } }, span: Default::default(), expansion: Vec::new() }
        }).collect();
        let program = hir::Program { body: Region { instructions } };
        let indexed = run(&program, &facts, &resources, &unstable, false);
        let reference = reference_run(&program, &facts, &resources, &unstable);
        prop_assert!(crate::preparation::certificate::same_program(&indexed.0, &reference.0));
        prop_assert_eq!(indexed.1, reference.1);
    }
}
