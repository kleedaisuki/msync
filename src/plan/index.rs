use std::collections::{BTreeMap, BTreeSet};

use super::Resource;

/// Conservative overlap candidates. Identity and projection axes never prove equality.
#[derive(Default)]
pub(super) struct ResourceIndex {
    endpoints: BTreeMap<String, PathIndex>,
    projection: PathIndex,
    identities: BTreeMap<String, BTreeMap<u64, IdSet>>,
    wildcard: BTreeSet<usize>,
    all: BTreeSet<usize>,
}

impl ResourceIndex {
    pub(super) fn insert(&mut self, resource: &Resource, id: usize) {
        self.all.insert(id);
        if resource.key.endpoint == "*" {
            self.wildcard.insert(id);
            return;
        }
        self.endpoints
            .entry(resource.key.endpoint.clone())
            .or_default()
            .insert(&resource.comparison, id);
        if let Some(path) = &resource.possible_windows_path {
            self.projection.insert(path, id);
        }
        if let Some(identity) = resource.identity_hint {
            self.identities
                .entry(resource.key.endpoint.clone())
                .or_default()
                .entry(identity)
                .or_default()
                .insert(id);
        }
    }

    /// Callers retire every resource associated with an access ID together.
    pub(super) fn remove(&mut self, resource: &Resource, id: usize) {
        self.all.remove(&id);
        if resource.key.endpoint == "*" {
            self.wildcard.remove(&id);
            return;
        }
        if let Some(index) = self.endpoints.get_mut(&resource.key.endpoint) {
            index.remove(&resource.comparison, id);
        }
        if let Some(path) = &resource.possible_windows_path {
            self.projection.remove(path, id);
        }
        if let Some(identity) = resource.identity_hint
            && let Some(entries) = self
                .identities
                .get_mut(&resource.key.endpoint)
                .and_then(|index| index.get_mut(&identity))
        {
            entries.remove(id);
        }
    }

    pub(super) fn candidates(&self, resource: &Resource, result: &mut BTreeSet<usize>) {
        if resource.key.endpoint == "*" {
            result.extend(&self.all);
            return;
        }
        result.extend(&self.wildcard);
        if let Some(index) = self.endpoints.get(&resource.key.endpoint) {
            index.candidates(&resource.comparison, result);
        }
        if let Some(path) = &resource.possible_windows_path {
            self.projection.candidates(path, result);
        }
        if let Some(identity) = resource.identity_hint
            && let Some(entries) = self
                .identities
                .get(&resource.key.endpoint)
                .and_then(|index| index.get(&identity))
        {
            entries.extend_to(result);
        }
    }

    pub(super) fn earliest(&self, resource: &Resource) -> Option<usize> {
        self.extreme(resource, false)
    }

    pub(super) fn latest(&self, resource: &Resource) -> Option<usize> {
        self.extreme(resource, true)
    }

    fn extreme(&self, resource: &Resource, latest: bool) -> Option<usize> {
        if resource.key.endpoint == "*" {
            return pick(&self.all, latest);
        }
        let mut best = pick(&self.wildcard, latest);
        if let Some(index) = self.endpoints.get(&resource.key.endpoint) {
            merge(
                &mut best,
                index.extreme(&resource.comparison, latest),
                latest,
            );
        }
        if let Some(path) = &resource.possible_windows_path {
            merge(&mut best, self.projection.extreme(path, latest), latest);
        }
        if let Some(identity) = resource.identity_hint
            && let Some(entries) = self
                .identities
                .get(&resource.key.endpoint)
                .and_then(|index| index.get(&identity))
        {
            merge(&mut best, entries.extreme(latest), latest);
        }
        best
    }
}

#[derive(Default)]
struct PathNode {
    children: BTreeMap<String, usize>,
    exact: IdSet,
    subtree: IdSet,
}

#[derive(Default)]
enum IdSet {
    #[default]
    Empty,
    One(usize),
    Many(BTreeSet<usize>),
}

impl IdSet {
    fn insert(&mut self, id: usize) {
        match self {
            Self::Empty => *self = Self::One(id),
            Self::One(previous) if *previous != id => {
                *self = Self::Many(BTreeSet::from([*previous, id]));
            }
            Self::Many(entries) => {
                entries.insert(id);
            }
            Self::One(_) => {}
        }
    }

    fn remove(&mut self, id: usize) {
        match self {
            Self::One(previous) if *previous == id => *self = Self::Empty,
            Self::Many(entries) => {
                entries.remove(&id);
                if entries.len() == 1 {
                    *self = Self::One(*entries.first().unwrap());
                }
            }
            _ => {}
        }
    }

    fn extend_to(&self, result: &mut BTreeSet<usize>) {
        match self {
            Self::Empty => {}
            Self::One(id) => {
                result.insert(*id);
            }
            Self::Many(entries) => result.extend(entries),
        }
    }

    fn extreme(&self, latest: bool) -> Option<usize> {
        match self {
            Self::Empty => None,
            Self::One(id) => Some(*id),
            Self::Many(entries) => pick(entries, latest),
        }
    }
}

struct PathIndex {
    nodes: Vec<PathNode>,
}

impl Default for PathIndex {
    fn default() -> Self {
        Self {
            nodes: vec![PathNode::default()],
        }
    }
}

impl PathIndex {
    fn insert(&mut self, path: &[String], id: usize) {
        let mut node = 0;
        self.nodes[node].subtree.insert(id);
        for component in path {
            let next = match self.nodes[node].children.get(component) {
                Some(&next) => next,
                None => {
                    let next = self.nodes.len();
                    self.nodes.push(PathNode::default());
                    self.nodes[node].children.insert(component.clone(), next);
                    next
                }
            };
            node = next;
            self.nodes[node].subtree.insert(id);
        }
        self.nodes[node].exact.insert(id);
    }

    fn remove(&mut self, path: &[String], id: usize) {
        let mut node = 0;
        self.nodes[node].subtree.remove(id);
        for component in path {
            let Some(&next) = self.nodes[node].children.get(component) else {
                return;
            };
            node = next;
            self.nodes[node].subtree.remove(id);
        }
        self.nodes[node].exact.remove(id);
    }

    fn candidates(&self, path: &[String], result: &mut BTreeSet<usize>) {
        let mut node = 0;
        for component in path {
            self.nodes[node].exact.extend_to(result);
            let Some(&next) = self.nodes[node].children.get(component) else {
                return;
            };
            node = next;
        }
        self.nodes[node].subtree.extend_to(result);
    }

    fn extreme(&self, path: &[String], latest: bool) -> Option<usize> {
        let mut node = 0;
        let mut best = None;
        for component in path {
            merge(&mut best, self.nodes[node].exact.extreme(latest), latest);
            let Some(&next) = self.nodes[node].children.get(component) else {
                return best;
            };
            node = next;
        }
        merge(&mut best, self.nodes[node].subtree.extreme(latest), latest);
        best
    }
}

fn pick(entries: &BTreeSet<usize>, latest: bool) -> Option<usize> {
    if latest {
        entries.last().copied()
    } else {
        entries.first().copied()
    }
}

fn merge(best: &mut Option<usize>, value: Option<usize>, latest: bool) {
    if let Some(value) = value {
        *best = Some(match *best {
            Some(previous) if latest => previous.max(value),
            Some(previous) => previous.min(value),
            None => value,
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::compiler::{Backend, Location};
    use crate::plan::{AccessScope, ResourceKey};
    use proptest::prelude::*;

    fn resource(
        endpoint: u8,
        path: Vec<u8>,
        projection: Option<Vec<u8>>,
        identity: Option<u8>,
    ) -> Resource {
        let components = |path: Vec<u8>| {
            path.into_iter()
                .map(|part| part.to_string())
                .collect::<Vec<_>>()
        };
        let comparison: std::sync::Arc<[String]> = components(path).into();
        Resource {
            location: Location {
                backend: Backend::Local,
                path: comparison.join("/"),
            },
            scope: AccessScope::Exact,
            key: ResourceKey {
                endpoint: if endpoint == 0 {
                    "*".into()
                } else {
                    endpoint.to_string()
                },
                components: comparison.clone(),
                rooted: false,
            },
            comparison,
            possible_windows_path: projection.map(|path| components(path).into()),
            identity_hint: identity.map(u64::from),
            writable_file: None,
            require_wsl_proof: false,
            disjoint: None,
        }
    }

    proptest! {
        #![proptest_config(ProptestConfig::with_cases(128))]
        #[test]
        fn indexed_overlap_extremes_and_retirement_match_brute_force(
            descriptions in prop::collection::vec((0_u8..4, prop::collection::vec(0_u8..4, 0..5), prop::option::of(prop::collection::vec(0_u8..4, 0..5)), prop::option::of(0_u8..4)), 0..60)
        ) {
            let resources = descriptions.into_iter().map(|(endpoint,path,projection,identity)| resource(endpoint,path,projection,identity)).collect::<Vec<_>>();
            let mut index = ResourceIndex::default();
            for (id, resource) in resources.iter().enumerate() { index.insert(resource,id); }
            for removed in [false, true] {
                if removed {
                    for (id, resource) in resources.iter().enumerate().filter(|(id,_)| id % 2 == 0) { index.remove(resource,id); }
                }
                for query in &resources {
                    let expected: BTreeSet<usize> = resources.iter().enumerate().filter(|(id, resource)| (!removed || id % 2 != 0) && resource.overlaps(query)).map(|(id,_)| id).collect();
                    let mut actual = BTreeSet::new();
                    index.candidates(query, &mut actual);
                    prop_assert_eq!(&actual, &expected);
                    prop_assert_eq!(index.earliest(query), expected.first().copied());
                    prop_assert_eq!(index.latest(query), expected.last().copied());
                }
            }
        }
    }
}
