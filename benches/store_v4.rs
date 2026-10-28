//! MVCC component candidate cost, not filesystem throughput or process startup.
use msync::hir::TransactionId;
use msync::language::Isolation;
use msync::transaction::{ResourceKey, Store};
use std::hint::black_box;
use std::sync::{
    Arc,
    atomic::{AtomicUsize, Ordering},
};
use std::time::Instant;
static CLONES: AtomicUsize = AtomicUsize::new(0);
#[derive(Debug)]
struct Counted;
impl Clone for Counted {
    fn clone(&self) -> Self {
        CLONES.fetch_add(1, Ordering::Relaxed);
        Self
    }
}
fn fixture<V: Clone>(
    versions: usize,
    resources: usize,
    mut value: impl FnMut(u64) -> V,
) -> (Store<V>, Vec<ResourceKey>) {
    let mut store = Store::new();
    let keys = (0..resources)
        .map(|i| ResourceKey::new("bench", &format!("/file-{i}")).unwrap())
        .collect::<Vec<_>>();
    for i in 0..versions {
        store
            .seed(keys[i % keys.len()].clone(), Some(value(i as u64)))
            .unwrap();
    }
    store
        .begin(TransactionId(1), Isolation::Serializable)
        .unwrap();
    (store, keys)
}
fn operation<V: Clone>(store: &Store<V>, key: &ResourceKey, replacement: Option<&V>) {
    let mut candidate = black_box(store).clone();
    if let Some(value) = replacement {
        candidate
            .write(TransactionId(1), key.clone(), value.clone())
            .unwrap();
        black_box(candidate.checkpoint(TransactionId(1)).unwrap());
    } else {
        black_box(candidate.read(TransactionId(1), key).unwrap());
    }
    black_box(candidate);
}
fn main() {
    if cfg!(debug_assertions) {
        println!("store_v4: skip timing in debug builds");
        return;
    }
    for (resources, versions) in [(1, 1), (10, 10), (100, 1_000), (100, 10_000), (100, 50_000)] {
        let (store, keys) = fixture(versions, resources, |value| {
            Arc::<[u8]>::from(value.to_le_bytes())
        });
        let (counted, count_keys) = fixture(versions, resources, |_| Counted);
        let new = Arc::<[u8]>::from(123u64.to_le_bytes());
        let origin = store.committed(&keys[0]);
        let mut verified = store.clone();
        verified
            .write(TransactionId(1), keys[0].clone(), new.clone())
            .unwrap();
        verified.checkpoint(TransactionId(1)).unwrap();
        assert_eq!(verified.committed(&keys[0]), Some(new.clone()));
        assert_eq!(verified.retention().versions, versions + 1);
        verified.rollback_origin(TransactionId(1)).unwrap();
        assert_eq!(verified.committed(&keys[0]), origin);
        drop(verified);
        for write in [false, true] {
            let replacement = write.then_some(&new);
            operation(&store, &keys[0], replacement);
            let mut samples = Vec::new();
            let iterations = if versions <= 10 { 10_000 } else { 100 };
            for _ in 0..5 {
                let start = Instant::now();
                for _ in 0..iterations {
                    operation(&store, &keys[0], replacement);
                }
                samples.push(start.elapsed().as_secs_f64() * 1000.0 / iterations as f64);
            }
            samples.sort_by(f64::total_cmp);
            CLONES.store(0, Ordering::Relaxed);
            operation(&counted, &count_keys[0], write.then_some(&Counted));
            println!(
                "case={} resources={resources} versions={versions} median_ms={:.6} value_clones={} samples=5 iterations={iterations}",
                if write {
                    "candidate-write"
                } else {
                    "candidate-read"
                },
                samples[2],
                CLONES.load(Ordering::Relaxed)
            );
            assert_eq!(store.retention().versions, versions);
            assert_eq!(
                store.committed(&keys[0]).unwrap().as_ref(),
                ((versions - resources) as u64).to_le_bytes()
            );
        }
    }
}
