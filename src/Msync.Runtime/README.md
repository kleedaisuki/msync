# Runtime source map

The runtime consumes one physical HPIR program. It does not parse source,
replay debug text, invoke the compiler or replan bound executable artifacts.
The runtime depends on Core, not the optimizer.

| Directory | Responsibility |
| --- | --- |
| `Hpir` | Contextual binding, physical interpreter and x64 emission, guest stack and heap, completion and effect ownership, interactive cells |
| `NativeModel` | Shared host boundaries: typed files, paths, processes, endpoints and standard operations |
| `Scheduling` | Resource dependency index and scheduling contracts |
| `Planning` | Resource identity and planning support |
| `Context` | Execution options |
| `Diagnostics` | Observations, timeline and actual HPIR instruction/allocation profiles |
| `Native` | Primitive scalar services, lexical paths and local/remote process controls |
| `Transfers` | Resource path semantics, shell transport, owned staging publication and overlap handling |
| `Platform` | Native file identity, process admission, executable memory and Windows copy APIs |
| `Text` / `Generated` | Unicode scalar operations and pinned generated tables |

`HpirExecutor` uses a VM-owned native frame stack and heap. Both execution
engines consume the same typed instructions, physical call entries and root
maps. `HpirWork` owns actual asynchronous effects and their typed dependencies.
`HpirExecutionSession` retains canonical physical cells, current-directory
authority and escaped work across submissions.

`HpirBinding` captures selected context and transfer dependencies. It does not
execute guest code. The executor honors the frozen records without
re-enumerating them and performs live observations at their selected points.
See [HPIR](../../docs/design/hpir.md) and
[mutable state and effects](../../docs/design/mutable-state-and-effects-v080.md)
for Path origins, object identity, escaping work and effect lifetimes.
