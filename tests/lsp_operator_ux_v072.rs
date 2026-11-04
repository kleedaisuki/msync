//! Actual editor edits are accepted by the compiler and select the intended files.

#[path = "support/lsp_client.rs"]
mod lsp_client;

use serde_json::{Value, json};
use std::fs;
use std::path::Path;
use std::process::Command;

fn replace(client: &mut lsp_client::Client, uri: &str, version: i32, text: &str) {
    client.notify(
        "textDocument/didChange",
        json!({"textDocument":{"uri":uri,"version":version},"contentChanges":[{"text":text}]}),
    );
}

fn complete(client: &mut lsp_client::Client, uri: &str, id: u64, source: &str) -> Value {
    let line = source.lines().count().saturating_sub(1);
    let character = source.lines().last().unwrap_or("").encode_utf16().count();
    client.request(
        id,
        "textDocument/completion",
        json!({"textDocument":{"uri":uri},"position":{"line":line,"character":character}}),
    )
}

fn apply(source: &str, item: &Value) -> String {
    let range = &item["textEdit"]["range"];
    let offset = |position: &Value| {
        let line = position["line"].as_u64().unwrap() as usize;
        let character = position["character"].as_u64().unwrap() as usize;
        let start = source
            .split_inclusive('\n')
            .take(line)
            .map(str::len)
            .sum::<usize>();
        let suffix = &source[start..];
        let mut utf16 = 0;
        for (index, ch) in suffix.char_indices() {
            if utf16 == character {
                return start + index;
            }
            utf16 += ch.len_utf16();
        }
        assert_eq!(utf16, character);
        source.len()
    };
    let mut edited = source.to_owned();
    edited.replace_range(
        offset(&range["start"])..offset(&range["end"]),
        item["textEdit"]["newText"].as_str().unwrap(),
    );
    edited
}

#[test]
fn completion_edits_copy_actual_hidden_spaced_and_shadowed_files_without_remote_admission() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("lsp-operators-")
        .tempdir_in(temporary)
        .unwrap();
    fs::create_dir(workspace.path().join(".codex")).unwrap();
    for (name, content) in [
        ("codex.prompt", "correct prompt"),
        ("two words.prompt", "spaced prompt"),
        ("source", "actual source file"),
        ("other", "wrong alias target"),
        ("pwd", "literal pwd file"),
        (".codex/codex.prompt", "hidden prompt"),
    ] {
        fs::write(workspace.path().join(name), content).unwrap();
    }
    let mut client = lsp_client::Client::start(workspace.path());
    let initialized = client.request(1, "initialize", json!({"processId":null,"capabilities":{}}));
    assert!(
        initialized["capabilities"]["completionProvider"]["triggerCharacters"]
            .as_array()
            .unwrap()
            .contains(&json!("/"))
    );
    client.notify("initialized", json!({}));
    let uri = url::Url::from_file_path(workspace.path().join("journey.msync"))
        .unwrap()
        .to_string();
    client.notify(
        "textDocument/didOpen",
        json!({"textDocument":{"uri":uri,"languageId":"msync","version":1,"text":""}}),
    );
    let mut version = 1;
    let mut id = 2;
    for (prefix, label, expected) in [
        ("selected := $local::", "codex.prompt", "correct prompt"),
        (
            "selected := $ local :: \"two",
            "two words.prompt",
            "spaced prompt",
        ),
        (
            "source := other\nselected := $local::",
            "source",
            "actual source file",
        ),
        ("selected := $pwd::", "pwd", "literal pwd file"),
        (
            "selected := $local::.codex/",
            "codex.prompt",
            "hidden prompt",
        ),
        (
            "selected := $local::~/.codex/",
            "codex.prompt",
            "hidden prompt",
        ),
        (
            "base := .codex\nselected := base/",
            "codex.prompt",
            "hidden prompt",
        ),
        ("selected := \".codex\"/", "codex.prompt", "hidden prompt"),
    ] {
        version += 1;
        replace(&mut client, &uri, version, prefix);
        let items = complete(&mut client, &uri, id, prefix);
        id += 1;
        let item = items
            .as_array()
            .unwrap()
            .iter()
            .find(|item| item["label"] == label && item["kind"] == 17)
            .unwrap_or_else(|| panic!("missing {label} for {prefix:?}: {items}"));
        assert_eq!(item["kind"], 17, "file completion kind: {item}");
        if label == "codex.prompt" {
            assert_eq!(
                item["textEdit"]["newText"], "codex.prompt",
                "ordinary filename must remain a Path operand"
            );
        } else if label == "two words.prompt" {
            assert_eq!(item["textEdit"]["newText"], "\"two words.prompt\"");
        }
        let edited = apply(prefix, item);
        let script = format!("{edited}\nselected -> result\n");
        fs::write(workspace.path().join("run.msync"), script).unwrap();
        let output = Command::new(env!("CARGO_BIN_EXE_msync"))
            .args(["run", "run.msync"])
            .current_dir(workspace.path())
            .env("HOME", workspace.path())
            .env("USERPROFILE", workspace.path())
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{edited}: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(
            fs::read_to_string(workspace.path().join("result")).unwrap(),
            expected,
            "{edited}"
        );
    }
    for prefix in [
        "macro copy(src, dest):\n    src -> dest\ncopy($local::",
        "known := $std::fs::is_regular_file($local::",
        "known = exists $local::",
    ] {
        version += 1;
        replace(&mut client, &uri, version, prefix);
        let items = complete(&mut client, &uri, id, prefix);
        id += 1;
        let item = items
            .as_array()
            .unwrap()
            .iter()
            .find(|item| item["label"] == "codex.prompt")
            .unwrap();
        assert!(apply(prefix, item).ends_with("$local::codex.prompt"));
    }
    for prefix in [
        "scope config:\n    base := .codex\n    selected := base/",
        "scope false:\n    base := .codex\n    selected := base/",
        "base := .codex\nscope config:\n    selected := base/",
        "base := .\nscope config:\n    base := .codex\n    scope nested:\n        selected := base/",
    ] {
        version += 1;
        replace(&mut client, &uri, version, prefix);
        let items = complete(&mut client, &uri, id, prefix);
        id += 1;
        assert!(
            items
                .as_array()
                .unwrap()
                .iter()
                .any(|item| item["label"] == "codex.prompt"),
            "nested completion {prefix:?}: {items}"
        );
    }
    for (prefix, members) in [
        ("selected := $pwd::", vec!["$pwd::pwd"]),
        ("selected := $file::", vec!["$file::this", "$file::parent"]),
        (
            "alias := $file\nselected := alias::",
            vec!["alias::this", "alias::parent"],
        ),
        ("alias := $pwd\nselected := alias::", vec!["alias::pwd"]),
    ] {
        version += 1;
        replace(&mut client, &uri, version, prefix);
        let items = complete(&mut client, &uri, id, prefix);
        id += 1;
        for member in members {
            assert!(
                items
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|item| item["label"] == member),
                "{prefix}: {items}"
            );
        }
        assert!(
            !items
                .as_array()
                .unwrap()
                .iter()
                .any(|item| item["label"] == "scope"),
            "unrelated keyword in scope suffix: {items}"
        );
    }
    let remote = "file => ssh://completion-must-not-authenticate.invalid\nselected := $file::";
    version += 1;
    replace(&mut client, &uri, version, remote);
    let items = complete(&mut client, &uri, id, remote);
    assert!(
        !items
            .as_array()
            .unwrap()
            .iter()
            .any(|item| item["kind"] == 17
                || item["kind"] == 19
                || item["label"] == "$file::this"
                || item["label"] == "$file::parent"),
        "remote/lexical shadow fabricated filesystem or builtin members: {items}"
    );
    client.finish();
}

#[test]
fn hover_uses_compiler_types_and_semantic_tokens_keep_operators_separate() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("lsp-types-")
        .tempdir_in(temporary)
        .unwrap();
    let mut client = lsp_client::Client::start(workspace.path());
    client.request(1, "initialize", json!({"processId":null,"capabilities":{}}));
    client.notify("initialized", json!({}));
    let uri = url::Url::from_file_path(workspace.path().join("types.msync"))
        .unwrap()
        .to_string();
    let source = "endpoint := $local\nscope text:\n    pattern := \".*\"\n    valid := true\npattern := text::pattern\nvalid := text::valid\njoined := \".codex\"/codex.prompt\n";
    client.notify(
        "textDocument/didOpen",
        json!({"textDocument":{"uri":uri,"languageId":"msync","version":1,"text":source}}),
    );
    for (id, line, expected) in [
        (2, 0, "Scope endpoint"),
        (3, 4, "String pattern"),
        (4, 5, "Bool valid"),
        (5, 6, "Path joined"),
    ] {
        let hover = client.request(
            id,
            "textDocument/hover",
            json!({"textDocument":{"uri":uri},"position":{"line":line,"character":1}}),
        );
        assert!(
            hover["contents"]["value"]
                .as_str()
                .unwrap()
                .contains(expected),
            "{hover}"
        );
    }
    let tokens = client.request(
        6,
        "textDocument/semanticTokens/full",
        json!({"textDocument":{"uri":uri}}),
    );
    let mut line = 0;
    let mut column = 0;
    let mut operators = Vec::new();
    for token in tokens["data"].as_array().unwrap().as_chunks::<5>().0 {
        let delta = token[0].as_u64().unwrap() as usize;
        line += delta;
        column = if delta == 0 { column } else { 0 } + token[1].as_u64().unwrap() as usize;
        let len = token[2].as_u64().unwrap() as usize;
        if token[3] == 1 {
            operators.push(&source.lines().nth(line).unwrap()[column..column + len]);
        }
    }
    assert!(operators.contains(&"$"));
    assert_eq!(
        operators
            .iter()
            .filter(|operator| **operator == "::")
            .count(),
        2
    );
    assert!(operators.contains(&"/"));
    client.finish();
}

#[test]
fn completion_within_literal_strings_preserves_full_paths_and_existing_delimiters() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("lsp-literal-paths-")
        .tempdir_in(temporary)
        .unwrap();
    fs::create_dir(workspace.path().join("path dir")).unwrap();
    fs::write(
        workspace.path().join("path dir/codex.prompt"),
        "literal completion bytes",
    )
    .unwrap();
    #[cfg(unix)]
    for name in ["tail\\", "quoted\"file.prompt", "base\\leaf"] {
        fs::write(workspace.path().join(name), "literal completion bytes").unwrap();
    }
    let mut client = lsp_client::Client::start(workspace.path());
    client.request(1, "initialize", json!({"processId":null,"capabilities":{}}));
    client.notify("initialized", json!({}));
    let uri = url::Url::from_file_path(workspace.path().join("literal.msync"))
        .unwrap()
        .to_string();
    client.notify(
        "textDocument/didOpen",
        json!({"textDocument":{"uri":uri,"languageId":"msync","version":1,"text":""}}),
    );
    let cases = [
        (
            "selected := $local::\"path dir/co",
            "codex.prompt",
            "\"path dir/codex.prompt\"",
        ),
        (
            "selected := \"path dir/co",
            "codex.prompt",
            "\"path dir/codex.prompt\"",
        ),
        #[cfg(unix)]
        ("selected := $local::\"tail\\", "tail\\", "\"tail\\\""),
        #[cfg(unix)]
        (
            "selected := $local::\"quoted\"\"",
            "quoted\"file.prompt",
            "\"quoted\"\"file.prompt\"",
        ),
        #[cfg(unix)]
        (
            "base := other\nselected := $local::",
            "base\\leaf",
            "\"base\\leaf\"",
        ),
    ];
    for (index, (prefix, label, expected)) in cases.into_iter().enumerate() {
        replace(&mut client, &uri, index as i32 + 2, prefix);
        let items = complete(&mut client, &uri, index as u64 + 2, prefix);
        let item = items
            .as_array()
            .unwrap()
            .iter()
            .find(|item| item["label"] == label && item["kind"] == 17)
            .unwrap_or_else(|| panic!("{prefix}: {items}"));
        assert_eq!(item["textEdit"]["newText"], expected);
        let edited = apply(prefix, item);
        let script = format!("{edited}\n$local::selected -> result\n");
        fs::write(workspace.path().join("run.msync"), script).unwrap();
        let output = Command::new(env!("CARGO_BIN_EXE_msync"))
            .args(["run", "run.msync"])
            .current_dir(workspace.path())
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{edited}: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(
            fs::read_to_string(workspace.path().join("result")).unwrap(),
            "literal completion bytes"
        );
    }
    let source = "selected := $local::\"path dir/codex\"";
    replace(&mut client, &uri, 20, source);
    let cursor = source.find("codex").unwrap() + 2;
    let items = client.request(
        20,
        "textDocument/completion",
        json!({"textDocument":{"uri":uri},"position":{"line":0,"character":cursor}}),
    );
    let item = items
        .as_array()
        .unwrap()
        .iter()
        .find(|item| item["label"] == "codex.prompt" && item["kind"] == 17)
        .unwrap();
    assert_eq!(
        apply(source, item),
        "selected := $local::\"path dir/codex.prompt\""
    );
    client.finish();
}

#[test]
fn protocol_semantics_keep_uri_receivers_atomic_and_never_complete_a_local_host_directory() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("lsp-protocols-")
        .tempdir_in(temporary)
        .unwrap();
    fs::create_dir(workspace.path().join("forge")).unwrap();
    fs::write(
        workspace.path().join("forge/local-only-sentinel"),
        "not a remote resource",
    )
    .unwrap();
    let mut client = lsp_client::Client::start(workspace.path());
    let initialized = client.request(1, "initialize", json!({"processId":null,"capabilities":{}}));
    let legend = &initialized["capabilities"]["semanticTokensProvider"]["legend"]["tokenTypes"];
    assert_eq!(legend[10], "protocol");
    assert_eq!(legend[11], "endpoint");
    client.notify("initialized", json!({}));
    let uri = url::Url::from_file_path(workspace.path().join("protocols.msync"))
        .unwrap()
        .to_string();
    client.notify(
        "textDocument/didOpen",
        json!({"textDocument":{"uri":uri,"languageId":"msync","version":1,"text":""}}),
    );
    for (index, source) in [
        "selected := ssh://forge /",
        "selected := (ssh://forge) /",
        "selected := (ssh://forge)::",
        "remote := ssh://forge\nselected := remote::",
        "selected := $local::ssh://forge",
        "selected := ssh://",
        "selected := http://forge/path /",
        "selected := tcp://[::1]:9000 /",
    ]
    .into_iter()
    .enumerate()
    {
        replace(&mut client, &uri, index as i32 + 2, source);
        let items = complete(&mut client, &uri, index as u64 + 2, source);
        assert!(
            !items
                .as_array()
                .unwrap()
                .iter()
                .any(|item| item["kind"] == 17 || item["kind"] == 19),
            "URI receiver must not become a local Path operand: {source}: {items}"
        );
    }
    let named = "forge := other\nssh := other\nvalue := ssh://forge";
    replace(&mut client, &uri, 11, named);
    for (id, column) in [(30, 9), (31, 15)] {
        let completion = client.request(
            id,
            "textDocument/completion",
            json!({"textDocument":{"uri":uri},"position":{"line":2,"character":column}}),
        );
        assert_eq!(
            completion,
            json!([]),
            "URI text must not offer same-named lexical bindings"
        );
        let definition = client.request(
            id + 10,
            "textDocument/definition",
            json!({"textDocument":{"uri":uri},"position":{"line":2,"character":column}}),
        );
        assert!(
            definition.is_null(),
            "URI atom must not navigate to lexical ssh/forge: {definition}"
        );
        let hover = client.request(
            id + 20,
            "textDocument/hover",
            json!({"textDocument":{"uri":uri},"position":{"line":2,"character":column}}),
        );
        assert!(
            hover.is_null(),
            "URI text must not inherit lexical keyword/value hover: {hover}"
        );
    }
    let keyword_host = "value := http://true";
    replace(&mut client, &uri, 12, keyword_host);
    let hover = client.request(
        60,
        "textDocument/hover",
        json!({"textDocument":{"uri":uri},"position":{"line":0,"character":16}}),
    );
    assert!(
        hover.is_null(),
        "an address named true is not a Bool literal: {hover}"
    );
    let source = "server := ssh://user@[2001:db8::1]:2222\nweb := https://site.test/path\ntext := \"ssh://forge\"\n";
    replace(&mut client, &uri, 20, source);
    let tokens = client.request(
        20,
        "textDocument/semanticTokens/full",
        json!({"textDocument":{"uri":uri}}),
    );
    let mut line = 0;
    let mut column = 0;
    let mut semantic = Vec::new();
    for token in tokens["data"].as_array().unwrap().as_chunks::<5>().0 {
        let delta = token[0].as_u64().unwrap() as usize;
        line += delta;
        column = if delta == 0 { column } else { 0 } + token[1].as_u64().unwrap() as usize;
        let len = token[2].as_u64().unwrap() as usize;
        semantic.push((
            token[3].as_u64().unwrap(),
            &source.lines().nth(line).unwrap()[column..column + len],
        ));
    }
    for expected in [
        (10, "ssh"),
        (10, "https"),
        (1, "://"),
        (11, "user@[2001:db8::1]:2222"),
        (11, "site.test/path"),
        (2, "\"ssh://forge\""),
    ] {
        assert!(
            semantic.contains(&expected),
            "missing {expected:?}: {semantic:?}"
        );
    }
    assert_eq!(semantic.iter().filter(|(kind, _)| *kind == 10).count(), 2);
    assert_eq!(
        semantic.iter().filter(|(_, text)| *text == "://").count(),
        2
    );
    client.finish();
}
