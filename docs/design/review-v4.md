# Independent v0.4 foundation review

Reviewed the structured compiler, effect graph, logical version store, disk
publication adapter and process ownership foundation at `5551841`, with focused
follow-up against the filesystem owner's uncommitted correction. This is a
component review, not acceptance of the unimplemented unified executor or OS
resource-view providers. Those remaining obligations are explicitly tracked in
[architecture-v4.md](architecture-v4.md).

## Resolved P1: journal cleanup changed external hard-link metadata

The original `transaction::filesystem::make_removable` recursively applied owner
permissions on Unix and cleared the read-only attribute on Windows to every file
in a retained publication journal. However, an old destination leaf can have a
hard-link alias outside the transaction's managed root. Renaming that leaf into
the journal does not give the manager exclusive ownership of the underlying
inode's permissions.

A real Windows reproduction creates a managed directory containing `old`, links
it to an external sibling, marks the shared file read-only, replaces the managed
directory, then finalizes its journal. At `5551841`, finalization succeeds but
the external alias silently becomes writable (`true -> false` for read-only),
violating the write footprint and weakening an unrelated file's protection.
Ordinary Unix files also acquired execute bits because cleanup added `0700`.

The correction separates directory traversal permissions from ordinary-file
deletion. Unix ordinary files are unlinked without chmod. Windows uses native
file disposition with ignore-readonly semantics; an unsupported capability
returns a visible native failure and retains the journal instead of changing
shared attributes. Directory-specific permission handling remains separate.

Independent regression artifact:

```text
tests/review_foundation_v4.rs
  finalizing_a_replaced_tree_must_not_change_permissions_through_external_hardlinks
  finalizing_an_old_hardlink_does_not_add_execute_bits_to_its_external_alias [Unix]
```

The first test failed on the Windows host before the correction and passed after
it. The Unix mode-bit regression is retained for the remote Unix matrix; it has
not been counted as a local Windows pass. Fixture data and disposable outputs
remain under the project's `.temp`.

Follow-up: the two independent Unix counterexamples and the Windows counterexample
passed at `e7fd5bd` in the targeted two-platform
[Actions run](https://github.com/kleedaisuki/msync/actions/runs/40481406869).

## Other assessed boundaries

The version store validates an entire pending segment before modifying published
epochs and keeps origin retention separate from checkpoint retention. The process
layer distinguishes a native launch from transactional mediation, immediately
drains both output pipes, and documents the limits of Unix process-group ownership.
The graph retains unknown-effect obligations rather than assuming argv proves
purity. No additional concrete P1/P2 counterexample was established in this bounded
review; that statement is not a proof of correctness or completed OS isolation.
