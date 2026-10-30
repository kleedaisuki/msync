//! Independent contracts for directional barriers and pure block-local planning.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Arc;

use msync::compiler::{Backend, CompileContext, Location, structured::Session};
use msync::hir::graph;
use msync::hir::{
    BarrierKind, Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, Program,
    Region,
};
use msync::language::{self, DestinationMode};
use msync::syntax::Span;
use proptest::prelude::*;

fn context() -> CompileContext {
    CompileContext {
        // The frontend must not require this directory or any named input to exist.
        pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join(".temp")
            .join("barrier-frontend-no-filesystem-needed"),
        source_file: None,
    }
}

fn compile(source: &str) -> Program {
    Session::new()
        .compile(&language::parse(source).unwrap(), &context())
        .unwrap()
}

fn operation_shape(program: &Program) -> Vec<String> {
    program
        .body
        .instructions
        .iter()
        .map(|instruction| match &instruction.kind {
            InstructionKind::Transfer { .. } => "transfer".into(),
            InstructionKind::Spawn { process, .. } => format!("spawn:{}", process.0),
            InstructionKind::Await { process } => format!("await:{process:?}"),
            InstructionKind::Barrier { kind } => format!("barrier:{kind:?}"),
        })
        .collect()
}

fn block_by_instruction(program: &Program) -> BTreeMap<InstructionId, usize> {
    program
        .blocks()
        .enumerate()
        .flat_map(|(block, body)| {
            body.instructions
                .iter()
                .map(move |instruction| (instruction.id, block))
        })
        .collect()
}

fn transfer_named(program: &Program, name: &str) -> InstructionId {
    program
        .body
        .instructions
        .iter()
        .find_map(|instruction| match &instruction.kind {
            InstructionKind::Transfer {
                destination: Output::File { location, .. },
                ..
            } if std::path::Path::new(&location.path).file_name().unwrap() == name => {
                Some(instruction.id)
            }
            _ => None,
        })
        .unwrap()
}

#[test]
fn bare_program_call_has_the_same_barriers_as_explicit_async_await() {
    let bare = compile("Program => $path::does_not_need_to_exist(\"argument\")\n");
    let explicit = compile("async await Program => $path::does_not_need_to_exist(\"argument\")\n");
    assert_eq!(operation_shape(&bare), operation_shape(&explicit));
    assert_eq!(
        operation_shape(&bare),
        ["barrier:Before", "spawn:0", "await:Some(ProcessId(0))",]
    );
}

#[test]
fn explicit_launch_and_join_leave_the_middle_block_available_for_other_work() {
    let program = compile(
        "\"before\" -> before\n\
         async Program => $path::does_not_need_to_exist(\"argument\")\n\
         Program::stdout -> captured\n\
         \"independent\" -> independent\n\
         await Program\n\
         \"after\" -> after\n",
    );
    let map = block_by_instruction(&program);
    let launch = program
        .body
        .instructions
        .iter()
        .find(|instruction| matches!(instruction.kind, InstructionKind::Spawn { .. }))
        .unwrap();
    let join = program
        .body
        .instructions
        .iter()
        .find(|instruction| {
            matches!(
                instruction.kind,
                InstructionKind::Await { process: Some(_) }
            )
        })
        .unwrap();
    let before = transfer_named(&program, "before");
    let captured = transfer_named(&program, "captured");
    let independent = transfer_named(&program, "independent");
    let after = transfer_named(&program, "after");
    assert!(map[&before] < map[&launch.id]);
    assert!(map[&launch.id] < map[&captured]);
    assert_eq!(map[&captured], map[&independent]);
    assert!(map[&independent] < map[&join.id]);
    assert!(map[&join.id] < map[&after]);
    assert_eq!(
        program
            .body
            .instructions
            .iter()
            .filter(|instruction| matches!(instruction.kind, InstructionKind::Await { .. }))
            .count(),
        1
    );
}

#[test]
fn user_async_flow_await_example_has_three_serial_blocks() {
    let program = compile(
        "async Program => $path::does_not_need_to_exist(\"argument\")\nProgram::stdout -> stdout.txt\nawait Program\n",
    );
    let blocks: Vec<_> = program.blocks().collect();
    assert_eq!(blocks.len(), 3);
    assert!(matches!(
        blocks[0].instructions.last().unwrap().kind,
        InstructionKind::Spawn { .. }
    ));
    assert!(matches!(
        blocks[1].instructions,
        [Instruction {
            kind: InstructionKind::Transfer { .. },
            ..
        }]
    ));
    assert!(matches!(
        blocks[2].instructions,
        [Instruction {
            kind: InstructionKind::Await { process: Some(_) },
            ..
        }]
    ));
}

#[test]
fn async_and_await_are_independent_directional_fences_for_file_statements() {
    let program = compile(
        "\"zero\" -> zero\n\
         async \"one\" -> one\n\
         \"two\" -> two\n\
         await \"three\" -> three\n\
         \"four\" -> four\n",
    );
    let map = block_by_instruction(&program);
    let ids: Vec<_> = ["zero", "one", "two", "three", "four"]
        .iter()
        .map(|name| transfer_named(&program, name))
        .collect();
    assert_eq!(map[&ids[0]], map[&ids[1]]);
    assert_eq!(map[&ids[1]], map[&ids[2]]);
    assert_eq!(map[&ids[2]], map[&ids[3]]);
    assert!(map[&ids[3]] < map[&ids[4]]);
    assert!(
        program
            .body
            .instructions
            .iter()
            .all(|instruction| !matches!(instruction.kind, InstructionKind::Await { .. }))
    );
}

#[test]
fn macro_exit_joins_its_async_program_before_the_callers_next_statement() {
    let program = compile(
        "macro launch():\n    async Child => $path::does_not_need_to_exist(\"argument\")\nlaunch()\n\"caller\" -> caller\n",
    );
    let instructions = &program.body.instructions;
    let launch = instructions
        .iter()
        .position(|instruction| matches!(instruction.kind, InstructionKind::Spawn { .. }))
        .unwrap();
    let join = instructions
        .iter()
        .position(|instruction| {
            matches!(
                instruction.kind,
                InstructionKind::Await { process: Some(_) }
            )
        })
        .unwrap();
    let caller = instructions
        .iter()
        .position(|instruction| instruction.id == transfer_named(&program, "caller"))
        .unwrap();
    assert!(launch < join && join < caller);
    let map = block_by_instruction(&program);
    assert!(map[&instructions[join].id] < map[&instructions[caller].id]);
}

#[test]
fn explicit_macro_join_does_not_gain_a_second_implicit_join() {
    for join in ["await Child", "await"] {
        let program = compile(&format!(
            "macro launch():\n    async Child => $path::does_not_need_to_exist(\"argument\")\n    {join}\nlaunch()\n"
        ));
        assert_eq!(
            program
                .body
                .instructions
                .iter()
                .filter(|instruction| matches!(instruction.kind, InstructionKind::Await { .. }))
                .count(),
            1,
            "{join}: {:?}",
            operation_shape(&program)
        );
    }
}

#[test]
fn removed_transaction_statements_are_not_silently_accepted() {
    for source in [
        "transaction Old:\n    \"data\" -> output\n",
        "transaction Old(serializable):\n    \"data\" -> output\n",
        "commit\n",
        "rollback\n",
        "rollback Old\n",
    ] {
        let result = language::parse(source).and_then(|document| {
            Session::new()
                .compile(&document, &context())
                .map(|_| document)
        });
        assert!(result.is_err(), "removed syntax compiled: {source}");
    }
}

#[test]
fn canonical_barrier_format_roundtrips_without_changing_expanded_operations() {
    let source = "# execution boundaries\nasync Program=>$path::missing(\"x\")\nProgram::stdout->captured\nawait Program\nasync \"data\"->out\nawait \"done\"->last\n";
    let formatted = language::format(source).unwrap();
    assert_eq!(language::format(&formatted).unwrap(), formatted);
    assert!(formatted.contains("# execution boundaries"));
    assert_eq!(
        operation_shape(&compile(source)),
        operation_shape(&compile(&formatted))
    );
}

fn node(id: usize, kind: InstructionKind) -> Instruction {
    Instruction {
        id: InstructionId(id),
        kind,
        span: Span::default(),
        expansion: Vec::new(),
    }
}

fn generated_program(kinds: &[u8]) -> Program {
    let instructions = kinds
        .iter()
        .enumerate()
        .map(|(id, kind)| {
            let kind = match kind {
                0 => InstructionKind::Transfer {
                    source: Input::Literal(Arc::from("value")),
                    destination: Output::File {
                        location: Location {
                            backend: Backend::Local,
                            path: context()
                                .pwd
                                .join(format!("target-{}", id % 3))
                                .to_string_lossy()
                                .into_owned(),
                        },
                        mode: DestinationMode::Exact,
                    },
                },
                1 => InstructionKind::Barrier {
                    kind: BarrierKind::Before,
                },
                2 => InstructionKind::Barrier {
                    kind: BarrierKind::After,
                },
                3 => InstructionKind::Await { process: None },
                _ => InstructionKind::Spawn {
                    process: ProcessId(id),
                    executable: Location {
                        backend: Backend::Local,
                        path: "not-inspected".into(),
                    },
                    arguments: Vec::new(),
                    cwd: context().pwd,
                },
            };
            node(id, kind)
        })
        .collect();
    Program {
        body: Region { instructions },
    }
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(96))]

    #[test]
    fn blocks_partition_every_instruction_and_respect_both_fence_directions(
        kinds in prop::collection::vec(0u8..5, 0..80)
    ) {
        let program = generated_program(&kinds);
        let blocks: Vec<_> = program.blocks().collect();
        let flattened: Vec<_> = blocks.iter().flat_map(|block| block.instructions.iter().map(|node| node.id)).collect();
        let original: Vec<_> = program.body.instructions.iter().map(|node| node.id).collect();
        prop_assert_eq!(flattened, original);
        for (index, block) in blocks.iter().enumerate() {
            prop_assert_eq!(block.id.0, index);
            prop_assert!(!block.instructions.is_empty());
            for (position, instruction) in block.instructions.iter().enumerate() {
                match instruction.kind {
                    InstructionKind::Barrier { kind: BarrierKind::Before } if position > 0 => {
                        prop_assert!(matches!(block.instructions.get(position + 1).map(|node| &node.kind),
                            Some(InstructionKind::Barrier { kind: BarrierKind::After })),
                            "an internal before fence must belong to the hard-cut pair");
                    },
                    InstructionKind::Await { .. } => {
                        prop_assert_eq!(position, 0);
                        prop_assert_eq!(block.instructions.len(), 1);
                    }
                    InstructionKind::Barrier { kind: BarrierKind::After }
                        | InstructionKind::Spawn { .. } => prop_assert_eq!(position + 1, block.instructions.len()),
                    _ => {}
                }
            }
        }
    }

    #[test]
    fn graph_edges_and_commutation_proofs_never_cross_basic_blocks(
        kinds in prop::collection::vec(0u8..5, 0..64)
    ) {
        let program = generated_program(&kinds);
        let map = block_by_instruction(&program);
        let plan = graph::build(&program);
        let declared_blocks: Vec<_> = plan.blocks.iter().map(|block| block.instructions.clone()).collect();
        let actual_blocks: Vec<_> = program.blocks().map(|block| block.instructions.iter().map(|node| node.id).collect::<Vec<_>>()).collect();
        prop_assert_eq!(declared_blocks, actual_blocks);
        for dependency in &plan.dependencies {
            prop_assert_eq!(map[&dependency.before], map[&dependency.after]);
            prop_assert!(dependency.before.0 < dependency.after.0);
        }
        for proof in &plan.commutations {
            prop_assert_eq!(map[&proof.left], map[&proof.right]);
        }
        for block in program.blocks() {
            let local = graph::build_block_with_context(block, &graph::PlanningContext::default());
            prop_assert!(local.nodes.iter().all(|node| map[&node.instruction] == block.id.0));
            prop_assert!(local.dependencies.iter().all(|edge| map[&edge.before] == block.id.0 && map[&edge.after] == block.id.0));
        }
    }
}
