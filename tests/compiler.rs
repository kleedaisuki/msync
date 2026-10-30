use msync::{
    compiler::{Backend, CompileContext, Compiler},
    syntax,
};

fn compile(source: &str) -> msync::compiler::Program {
    Compiler::new()
        .compile(&syntax::parse(source).unwrap())
        .unwrap()
}

#[test]
fn displayed_bindings_quote_values_for_readable_reusable_declarations() {
    let source =
        "root := \"folder name/#tag\\nchild\"\npublic => https://example.com/path%20name/\n";
    let mut compiler = Compiler::new();
    compiler.compile(&syntax::parse(source).unwrap()).unwrap();
    let displayed = compiler.bindings().join("\n");
    assert!(displayed.contains("\\n"));
    let mut reloaded = Compiler::new();
    reloaded
        .compile(&syntax::parse(&displayed).unwrap())
        .unwrap();
    assert_eq!(compiler.bindings(), reloaded.bindings());
}

#[test]
fn invalid_selector_explanations_are_compact_without_losing_the_typed_cause() {
    let errors = Compiler::new()
        .compile_rich(&syntax::parse("dir/<(> -> output/\n").unwrap())
        .unwrap_err();
    assert!(errors[0].diagnostic.message.contains("unclosed group"));
    assert!(!errors[0].diagnostic.message.contains('\n'));
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
    let source = "  missing::some/long/file -> $pwd::pwd/out\n";
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
    let source = r#""missing::a\\b" -> "$pwd::pwd/out""#;
    let report = &reports(source)[0];
    assert_eq!(
        &source[report.diagnostic.span.start..report.diagnostic.span.end],
        "missing"
    );
    let source = r#""dir/<\\p{NoSuchProperty}>" -> "$pwd::pwd/out/""#;
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
    let source = "web => https://example.org\n$pwd::pwd/input -> web::output\n";
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
    let source = "root := $local::folder\n$wsl::root/file -> $pwd::pwd/out\n";
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
    let source = "$pwd::pwd/files/<.+> -> $pwd::pwd/out\n";
    let report = &reports(source)[0];
    assert_eq!(report.diagnostic.span.start, source.trim_end().len());
    assert_eq!(report.diagnostic.span.start, report.diagnostic.span.end);
    assert!(report.help[0].contains("Append '/'"));
}

#[test]
fn explicit_pwd_item_is_local_and_value_aliases_retain_locality() {
    let program = compile(
        "root := $pwd::pwd/configs\n$pwd::pwd/source -> root/target\n$pwd::pwd/source -> $pwd::pwd\n",
    );
    let pwd = std::env::current_dir().unwrap();
    assert_eq!(program.operations[0].source.backend, Backend::Local);
    assert_eq!(
        std::path::Path::new(&program.operations[0].source.path),
        pwd.join("source")
    );
    assert_eq!(
        std::path::Path::new(&program.operations[0].destination.path),
        pwd.join("configs/target")
    );
    assert_eq!(
        std::path::Path::new(&program.operations[1].source.path),
        pwd.join("source")
    );
    assert_eq!(
        std::path::Path::new(&program.operations[1].destination.path),
        pwd
    );
    assert!(
        reports("root := $pwd::pwd/configs\n$wsl::root/file -> $pwd::pwd/out\n")[0]
            .diagnostic
            .message
            .contains("conflicting")
    );
}

fn namespace_context(pwd: &str, source: Option<&str>) -> CompileContext {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/namespace-context");
    CompileContext {
        pwd: root.join(pwd),
        source_file: source.map(|source| root.join(source)),
    }
}

fn compile_in(
    compiler: &mut Compiler,
    source: &str,
    context: &CompileContext,
) -> msync::compiler::Program {
    compiler
        .compile_rich_with_context(&syntax::parse_rich(source).unwrap(), context)
        .unwrap()
}

#[test]
fn namespace_aliases_resolve_current_context_but_value_aliases_snapshot_declaration() {
    let first = namespace_context("cwd-one", Some("scripts-one/sync.msync"));
    let second = namespace_context("cwd-two", Some("scripts-two/sync.msync"));
    let mut compiler = Compiler::new();
    compile_in(
        &mut compiler,
        "this => $pwd\nfile => $file\nfixed := $pwd::pwd\nfixedfile := $file::this\nfixeddir := $file::parent\n",
        &first,
    );
    let bindings = compiler.bindings();
    assert!(bindings.contains(&"this => $pwd".to_string()));
    assert!(bindings.contains(&"file => $file".to_string()));
    let program = compile_in(
        &mut compiler,
        "this::pwd/input -> fixed/out\nfile::this -> fixedfile\nfile::parent/assets -> fixeddir/new\n",
        &second,
    );
    assert_eq!(
        std::path::Path::new(&program.operations[0].source.path),
        second.pwd.join("input")
    );
    assert_eq!(
        std::path::Path::new(&program.operations[0].destination.path),
        first.pwd.join("out")
    );
    assert_eq!(
        std::path::Path::new(&program.operations[1].source.path),
        second.source_file.as_ref().unwrap()
    );
    assert_eq!(
        std::path::Path::new(&program.operations[1].destination.path),
        first.source_file.as_ref().unwrap()
    );
    assert_eq!(
        std::path::Path::new(&program.operations[2].source.path),
        second
            .source_file
            .as_ref()
            .unwrap()
            .parent()
            .unwrap()
            .join("assets")
    );
    assert_eq!(
        std::path::Path::new(&program.operations[2].destination.path),
        first
            .source_file
            .as_ref()
            .unwrap()
            .parent()
            .unwrap()
            .join("new")
    );
}

#[test]
fn same_named_script_files_have_distinct_source_and_parent_items() {
    for directory in ["one", "two"] {
        let context = namespace_context("unrelated-cwd", Some(&format!("{directory}/sync.msync")));
        let program = compile_in(
            &mut Compiler::new(),
            "$file::this -> $file::parent/copy.msync\n",
            &context,
        );
        let source = context.source_file.unwrap();
        assert_eq!(
            std::path::Path::new(&program.operations[0].source.path),
            source
        );
        assert_eq!(
            std::path::Path::new(&program.operations[0].destination.path),
            source.parent().unwrap().join("copy.msync")
        );
    }
}

#[test]
fn missing_source_context_points_to_namespace_qualifier_without_cwd_fallback() {
    for source in [
        "$file::this -> output\n",
        "$file::parent/assets -> output\n",
        "file => $file\nfile::this -> output\n",
    ] {
        let script = syntax::parse_rich(source).unwrap();
        let context = namespace_context("cwd", None);
        let report = &Compiler::new()
            .compile_rich_with_context(&script, &context)
            .unwrap_err()[0];
        let span = report.diagnostic.span;
        assert!(matches!(&source[span.start..span.end], "$file" | "file"));
        assert!(report.diagnostic.message.contains("no source-file context"));
        assert!(report.help[0].contains("stdin and direct REPL"));
    }
    let mut compiler = Compiler::new();
    compile_in(
        &mut compiler,
        "file => $file\n",
        &namespace_context("cwd", None),
    );
    assert_eq!(compiler.bindings(), ["file => $file"]);
}

#[test]
fn namespace_unknown_items_never_become_child_paths_or_value_alias_lookups() {
    for (source, expected) in [
        ("$pwd::foo -> output\n", "foo"),
        ("$pwd:: -> output\n", ""),
        ("$file::unknown -> output\n", "unknown"),
        ("this => $pwd\nthis::foo -> output\n", "foo"),
        ("foo := known\n$pwd::foo -> output\n", "foo"),
    ] {
        let script = syntax::parse_rich(source).unwrap();
        let report = &Compiler::new()
            .compile_rich_with_context(
                &script,
                &namespace_context("cwd", Some("scripts/sync.msync")),
            )
            .unwrap_err()[0];
        assert_eq!(
            &source[report.diagnostic.span.start..report.diagnostic.span.end],
            expected
        );
        assert!(report.diagnostic.message.contains("unknown item"));
        assert!(!report.help.is_empty());
    }
}

#[test]
fn old_pwd_path_shorthand_has_precise_namespace_migration_help() {
    for (operand, suggestion) in [
        ("$pwd/configs", "$pwd::pwd/configs"),
        ("$pwd", "$pwd::pwd"),
        (r"$pwd\configs", r"$pwd::pwd\configs"),
    ] {
        let source = format!("{operand} -> output\n");
        let report = &reports(&source)[0];
        assert_eq!(
            &source[report.diagnostic.span.start..report.diagnostic.span.end],
            "$pwd"
        );
        assert!(report.help[0].contains(suggestion), "{:?}", report.help);
    }
    assert_eq!(
        compile("legacy/input -> legacy/output\n").operations[0]
            .source
            .path,
        "legacy/input"
    );
}

#[test]
fn script_file_items_reject_children_even_through_value_aliases() {
    let context = namespace_context("cwd", Some("scripts/sync.msync"));
    for source in [
        "$file::this/child -> output\n",
        "$file::this/ -> output\n",
        "script := $file::this\nscript/child -> output\n",
        "a := $file::this\nb := a\nb/child -> output\n",
    ] {
        let script = syntax::parse_rich(source).unwrap();
        let report = &Compiler::new()
            .compile_rich_with_context(&script, &context)
            .unwrap_err()[0];
        assert!(matches!(
            &source[report.diagnostic.span.start..report.diagnostic.span.end],
            "/child" | "/"
        ));
        assert!(report.help[0].contains("$file::parent/path"));
    }
}

#[test]
fn context_capture_is_absolute_and_compiler_rejects_relative_explicit_contexts() {
    let context =
        CompileContext::capture(Some(std::path::Path::new("scripts/main.msync"))).unwrap();
    assert!(context.pwd.is_absolute());
    assert!(context.source_file.as_ref().unwrap().is_absolute());
    let source = syntax::parse("a -> b\n").unwrap();
    let invalid = CompileContext {
        pwd: "relative".into(),
        source_file: None,
    };
    assert!(
        Compiler::new()
            .compile_with_context(&source, &invalid)
            .is_err()
    );
}

#[test]
fn failed_namespace_resolution_rolls_back_namespace_and_value_bindings() {
    let context = namespace_context("cwd", None);
    let mut compiler = Compiler::new();
    compile_in(&mut compiler, "kept => $pwd\n", &context);
    let script =
        syntax::parse_rich("newfile => $file\nnewpwd := $pwd::pwd\nnewfile::this -> output\n")
            .unwrap();
    assert!(
        compiler
            .compile_rich_with_context(&script, &context)
            .is_err()
    );
    assert_eq!(compiler.bindings(), ["kept => $pwd"]);
}

#[test]
fn tcp_endpoints_have_explicit_ports_and_no_credentials_or_paths() {
    let program = compile("wire => tcp://127.0.0.1:9000\n$pwd::pwd/input -> wire::/\n");
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
        let source = format!("wire => tcp://127.0.0.1:9000\n{operand} -> $pwd::pwd/out\n");
        let report = &reports(&source)[0];
        let path = operand.split_once("::").unwrap().1;
        assert_eq!(
            &source[report.diagnostic.span.start..report.diagnostic.span.end],
            path
        );
        assert!(report.help.iter().any(|help| help.contains("endpoint::/")));
    }
    let source = r#"wire => tcp://127.0.0.1:9000
"wire::folder\\file" -> $pwd::pwd/out
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
        "wire => tcp://127.0.0.1:9000\nchannel := wire::/\nchannel -> $pwd::pwd/out\n$pwd::pwd/input -> channel\n",
    );
    assert_eq!(program.operations[0].source.path, "/");
    assert_eq!(program.operations[1].destination.path, "/");
    for statement in [
        "channel/file -> $pwd::pwd/out",
        "$pwd::pwd/input -> channel/file",
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

#[test]
fn ssh_configuration_aliases_preserve_exact_names_and_optional_overrides() {
    for (url, host, user, port) in [
        ("ssh://Production_Box", "Production_Box", None, None),
        ("ssh://Build.Host", "Build.Host", None, None),
        ("ssh://开发机", "开发机", None, None),
        ("ssh://开发%机", "开发%机", None, None),
        (
            "ssh://Alice@开发机:2222",
            "开发机",
            Some("Alice"),
            Some(2222),
        ),
        (
            "ssh://Alice@Production_Box:2222",
            "Production_Box",
            Some("Alice"),
            Some(2222),
        ),
        ("ssh://alice@[::1]:2222", "[::1]", Some("alice"), Some(2222)),
    ] {
        let source = format!("server => {url}\nserver::input -> output\n");
        let program = compile(&source);
        assert_eq!(
            program.operations[0].source.backend,
            Backend::Ssh {
                host: host.to_owned(),
                user: user.map(str::to_owned),
                port,
            }
        );
        assert_eq!(program.operations[0].source.backend.to_string(), url);
    }
}

#[test]
fn ssh_configuration_lookup_never_silently_strips_host_controls() {
    for endpoint in [
        "ssh://host\nother",
        "ssh://host\tother",
        "ssh://host\rother",
        "ssh:\t//host",
    ] {
        let source = format!("remote => {endpoint:?}\nremote::input -> output\n");
        let errors = reports(&source);
        assert!(!errors.is_empty());
        assert!(errors[0].diagnostic.message.contains("control characters"));
    }
}
