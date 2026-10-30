# Relocatable execution: independent acceptance plan

> Historical v0.4.1 acceptance plan, not a v0.5 completion claim. The exported
> typed-IR checks remain useful; transaction and native-view requirements below
> are superseded by [barrier validation](barrier-validation.md).

This is the independent validation workstream for the exportable, relocatable
module and staged diagnostic follow-up. It does not certify work in progress.
Historical v0.4 gates remain in [validation.md](validation.md); current execution
ownership and observation rules are in [late-bound-execution.md](late-bound-execution.md).

## What must remain distinct

The compiler produces typed expanded operations and symbolic relocation intent.
The object codec transports that intent, not trusted host observations. Linking
binds the module to a supplied context. Runtime specialization owns fresh facts,
input versions and guards; scheduling and physical IO follow those obligations.
An object that decodes successfully is therefore not automatically executable.

Debug source text is diagnostic material. It must neither be reparsed as an
execution fallback nor used to guess a filesystem binding. A default zero span
is not evidence that a macro's first token caused an environmental failure.

## Discriminating user journeys

| Contract | Acceptance observation | Negative/control case |
| --- | --- | --- |
| Compile/export is static | A four-operation local/WSL macro exports without endpoint helpers, private execution storage or native view setup | Poison child PATH and native-backend configuration; an unused macro containing a Spawn must not import native execution capability |
| The module is actually relocatable | One object executes in two different supplied cwd/home/environment contexts using their different input values | A user-written absolute constant must stay absolute even if it happens to share the compile cwd prefix |
| Input identity and debug identity differ | A moved object binds the main module's `$file` according to its documented object-input contract while retained source names still diagnose original operations | Remove the original source before execution; do not permit reopening/recompiling it |
| Typed exports have deliberate lifetimes | Repeated module instances receive valid fresh runtime identities, or a documented symbol collision fails before effects | An imported live process/origin must not be fabricated by copying a serialized numeric ID |
| Object loading validates semantic structure | Unknown operations/versions, duplicate IDs and invalid relocation/debug references fail before backend or filesystem effects | Start with a real valid module and mutate one meaningful table field, not only random invalid bytes |
| Diagnostics describe the failed phase | Binding capability failures cite the actual requirement as context, not a fabricated syntax caret; file IO failures retain their real operand token | Keep compiler type/name failures visibly separate; a leading macro definition must not become the location for every error |
| Artifact publication is safe | Failed compilation leaves an existing object unchanged; object bytes remain exact machine input under color modes | Refuse overwriting the source, and refuse source formatting of a compiled object without modifying it |
| Source and object paths converge | Equivalent source/object executions produce the same selected data, supported metadata and explicit transaction counts | Rebind after file kind or permission changes and require fresh runtime behavior rather than cached compile-time facts |

The v1 object is an expanded executable module, not a serialized compiler AST or
macro-template library. Object `/load` must expose only its documented typed
exports. Source `/load` retains source macro-definition semantics; tests must not
pretend the two forms install identical compile-time environments.

## Work ownership and execution policy

Independent tests use these distinct targets:

- `tests/independent_object_journeys_v4.rs`: cross-context actual CLI journeys.
- `tests/independent_phase_journeys_v4.rs`: capability import and phase/location counterexamples.
- `tests/independent_object_integrity_v4.rs`: structured corruption and metamorphic/property checks.

Implementation-owned tests such as `artifact_cli_v4`, `relocation_lowering_v4`,
`doctor_v4` and `runtime_diagnostics_v4` are reused as evidence rather than copied.
The independent suite is intended to challenge the integration boundary, not
inflate its test count.

Fixtures stay under repository `.temp`. Child environment overrides must not
change user environment, configuration, SSH setup or installed drivers. No test
reads personal `.codex` payloads. Optional real WSL/native-provider evidence is
reported separately from ordinary subprocess acceptance. Cargo work is bounded
to two jobs and coordinated by the root integrator; broad platform matrices run
in the cloud when the changed contracts require them.

## Evidence record

Pending. Test authors and the root integrator must record the exact selected
source, commands/targets, terminal results, ignored cases and artifact identity
after execution. Merely adding tests or observing a successful codec round trip
does not close the end-to-end export, binding, execution or diagnostic contract.
