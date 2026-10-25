use msync::{
    compiler::{Backend, Compiler},
    syntax,
};

fn compile(source: &str) -> msync::compiler::Program {
    Compiler::new()
        .compile(&syntax::parse(source).unwrap())
        .unwrap()
}

#[test]
fn seed_is_a_compilable_specification() {
    let program = compile(include_str!("../docs/dsl/spread.msync"));
    assert_eq!(program.operations.len(), 5);
    assert_eq!(
        program.operations.last().unwrap().source.path,
        "~/.codex/agents/"
    );
    assert_eq!(
        program.operations.last().unwrap().destination.backend,
        Backend::Wsl
    );
    assert_eq!(
        program.operations.last().unwrap().destination.path,
        "~/.codex/agents/"
    );
}

#[test]
fn failed_submission_rolls_back_all_bindings() {
    let mut compiler = Compiler::new();
    compiler
        .compile(&syntax::parse("good := original\n").unwrap())
        .unwrap();
    assert!(
        compiler
            .compile(&syntax::parse("good := replacement\nnew => $local\nbad::x -> y\n").unwrap())
            .is_err()
    );
    let plan = compiler
        .compile(&syntax::parse("good -> output\n").unwrap())
        .unwrap();
    assert_eq!(plan.operations[0].source.path, "original");
    assert_eq!(compiler.bindings().len(), 1);
}

#[test]
fn aliases_are_component_scoped_and_endpoint_polymorphic() {
    let plan = compile(
        "a := ~/base\na-long := a/child\nremote => $wsl\nremote::a-long/x -> a-unrelated\n",
    );
    assert_eq!(plan.operations[0].source.path, "~/base/child/x");
    assert_eq!(plan.operations[0].source.backend, Backend::Wsl);
    assert_eq!(plan.operations[0].destination.path, "a-unrelated");
    assert!(
        Compiler::new()
            .compile(&syntax::parse("a := $local::x\n$wsl::a/file -> out\n").unwrap())
            .is_err()
    );
}

#[test]
fn invalid_operations_fail_before_runtime() {
    for source in [
        "a => $unknown\n",
        "a => ftp://host\n",
        "a => ssh://-bad\n",
        "a => ssh://user:secret@host\n",
        "a => ssh://host/subdir\n",
        "a => https://host\nx -> a::y\n",
        "x -> $path::y\n",
        "x/<[> -> out/\n",
        "x/<.+> -> out\n",
        "x -> out/<.+>\n",
        "a := $local::x\n$wsl::a/file -> out\n",
    ] {
        let result = syntax::parse(source).and_then(|ast| Compiler::new().compile(&ast));
        assert!(result.is_err(), "unexpected success: {source}");
    }
}

#[test]
fn ssh_and_http_endpoints_are_typed() {
    let plan = compile(
        "server => ssh://alice@127.0.0.1:2222\nweb => https://example.com/base/\nserver::~/x -> local/x\nweb::~/v1/help.html -> help.html\n",
    );
    assert!(
        matches!(&plan.operations[0].source.backend, Backend::Ssh {host,user,port} if host == "127.0.0.1" && user.as_deref() == Some("alice") && *port == Some(2222))
    );
    assert!(
        matches!(&plan.operations[1].source.backend, Backend::Http {base} if base == "https://example.com/base/")
    );
}

#[test]
fn selector_regex_is_validated_and_full_name_anchoring_is_possible() {
    let plan = compile("config/<.+\\.toml> -> output/\n");
    assert_eq!(plan.operations[0].pattern.as_deref(), Some(".+\\.toml"));
    assert_eq!(plan.operations[0].source.path, "config/");
}
