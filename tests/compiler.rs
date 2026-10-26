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

fn reports(source: &str) -> Vec<syntax::Report> {
    let script = syntax::parse_rich(source).unwrap();
    Compiler::new().compile_rich(&script).unwrap_err()
}

#[test]
fn unknown_endpoint_points_at_only_the_qualifier_with_constructive_help() {
    let source = "  missing::some/long/file -> $pwd::out\n";
    let report = &reports(source)[0];
    assert_eq!(
        &source[report.diagnostic.span.start..report.diagnostic.span.end],
        "missing"
    );
    assert!(
        report
            .help
            .iter()
            .any(|help| help.contains("Declare an endpoint"))
    );
}

#[test]
fn compiler_spans_map_through_quoted_escapes() {
    let source = r#""missing::a\\b" -> "$pwd::out""#;
    let report = &reports(source)[0];
    assert_eq!(
        &source[report.diagnostic.span.start..report.diagnostic.span.end],
        "missing"
    );
    let source = r#""dir/<\\p{NoSuchProperty}>" -> "$pwd::out/""#;
    let report = &reports(source)[0];
    assert_eq!(
        &source[report.diagnostic.span.start..report.diagnostic.span.end],
        r"\\p{NoSuchProperty}"
    );
}

#[test]
fn binding_and_readonly_destination_have_operand_spans() {
    let source = "bad/name => $local\n";
    let report = &reports(source)[0];
    assert_eq!(
        &source[report.diagnostic.span.start..report.diagnostic.span.end],
        "bad/name"
    );
    let source = "web => https://example.org\n$pwd::input -> web::output\n";
    let report = &reports(source)[0];
    assert_eq!(
        &source[report.diagnostic.span.start..report.diagnostic.span.end],
        "web"
    );
    assert!(!report.labels.is_empty());
    assert!(!report.help.is_empty());
}

#[test]
fn qualifier_conflict_labels_both_endpoint_and_alias() {
    let source = "root := $local::folder\n$wsl::root/file -> $pwd::out\n";
    let report = &reports(source)[0];
    assert_eq!(
        &source[report.diagnostic.span.start..report.diagnostic.span.end],
        "$wsl"
    );
    assert_eq!(
        &source[report.labels[0].span.start..report.labels[0].span.end],
        "root"
    );
}

#[test]
fn selector_destination_suggests_exact_trailing_slash_insertion() {
    let source = "$pwd::files/<.+> -> $pwd::out\n";
    let report = &reports(source)[0];
    assert_eq!(report.diagnostic.span.start, source.trim_end().len());
    assert_eq!(report.diagnostic.span.start, report.diagnostic.span.end);
    assert!(report.help[0].contains("Append '/'"));
}

#[test]
fn explicit_pwd_prefix_and_endpoint_are_local_and_aliases_retain_locality() {
    let program =
        compile("root := $pwd/configs\n$pwd::source -> root/target\n$pwd/source -> $pwd\n");
    assert_eq!(program.operations[0].source.backend, Backend::Local);
    assert_eq!(program.operations[0].source.path, "source");
    assert_eq!(program.operations[0].destination.path, "./configs/target");
    assert_eq!(program.operations[1].source.path, "./source");
    assert_eq!(program.operations[1].destination.path, ".");
    assert!(
        reports("root := $pwd/configs\n$wsl::root/file -> $pwd::out\n")[0]
            .diagnostic
            .message
            .contains("conflicting")
    );
}

#[test]
fn tcp_endpoints_have_explicit_ports_and_no_credentials_or_paths() {
    let program = compile("wire => tcp://127.0.0.1:9000\n$pwd::input -> wire::/\n");
    assert!(
        matches!(&program.operations[0].destination.backend, Backend::Tcp {host,port} if host == "127.0.0.1" && *port == 9000)
    );
    for endpoint in [
        "tcp://localhost",
        "tcp://user@localhost:9000",
        "tcp://localhost:9000/path",
        "tcp://localhost:9000?q=x",
    ] {
        assert!(!reports(&format!("wire => {endpoint}\n")).is_empty());
    }
}

#[test]
fn tcp_operands_require_explicit_socket_marker_with_precise_path_span() {
    for operand in [
        "wire::arbitrary",
        "wire::/file",
        "wire::.",
        "wire::~/file",
        "wire::",
    ] {
        let source = format!("wire => tcp://127.0.0.1:9000\n{operand} -> $pwd::out\n");
        let report = &reports(&source)[0];
        let path = operand.split_once("::").unwrap().1;
        assert_eq!(
            &source[report.diagnostic.span.start..report.diagnostic.span.end],
            path
        );
        assert!(report.help.iter().any(|help| help.contains("endpoint::/")));
    }
    let source = r#"wire => tcp://127.0.0.1:9000
"wire::folder\\file" -> $pwd::out
"#;
    let report = &reports(source)[0];
    assert_eq!(
        &source[report.diagnostic.span.start..report.diagnostic.span.end],
        r"folder\\file"
    );
}

#[test]
fn tcp_path_aliases_preserve_socket_marker_and_reject_suffixes() {
    let program = compile(
        "wire => tcp://127.0.0.1:9000\nchannel := wire::/\nchannel -> $pwd::out\n$pwd::input -> channel\n",
    );
    assert_eq!(program.operations[0].source.path, "/");
    assert_eq!(program.operations[1].destination.path, "/");
    for statement in [
        "channel/file -> $pwd::out",
        "$pwd::input -> channel/file",
        "bad := channel/file",
        "bad := wire::file",
    ] {
        let source = format!("wire => tcp://127.0.0.1:9000\nchannel := wire::/\n{statement}\n");
        let report = &reports(&source)[0];
        let highlighted = &source[report.diagnostic.span.start..report.diagnostic.span.end];
        assert!(
            matches!(highlighted, "channel/file" | "file"),
            "{highlighted}"
        );
        assert!(report.help[0].contains("TCP has no filesystem path"));
    }
}
