//! Real editor protocol journey through namespaces and embedded library sources.

#[path = "support/lsp_client.rs"]
mod lsp_client;

use serde_json::json;
use std::fs;
use std::path::Path;

#[test]
fn stdio_navigation_opens_actual_stdlib_source_and_lexical_members() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("lsp-v070-")
        .tempdir_in(temporary)
        .unwrap();
    let mut client = lsp_client::Client::start(workspace.path());
    let uri = url::Url::from_file_path(workspace.path().join("journey.msync"))
        .unwrap()
        .to_string();
    let initialized = client.request(
        1,
        "initialize",
        json!({"processId": null, "capabilities": {}}),
    );
    assert!(initialized["capabilities"]["signatureHelpProvider"].is_object());
    client.notify("initialized", json!({}));
    let source = "fs => $std::fs\nscope hello:\n    codex := ~/.codex\nnamed := hello::codex\nready = fs::is_directory(named)\nvalues := [$local, $wsl]\n";
    client.notify(
        "textDocument/didOpen",
        json!({"textDocument":{"uri":uri,"languageId":"msync","version":1,"text":source}}),
    );
    let diagnostics = client.receive(|message| {
        message["method"] == "textDocument/publishDiagnostics" && message["params"]["uri"] == uri
    });
    assert_eq!(diagnostics["params"]["diagnostics"], json!([]));
    let definition = client.request(
        2,
        "textDocument/definition",
        json!({"textDocument":{"uri":uri},"position":{"line":4,"character":15}}),
    );
    assert!(
        definition["uri"]
            .as_str()
            .unwrap()
            .starts_with("msync-stdlib:///")
    );
    let library = client.request(
        3,
        "msync/stdlibSource",
        json!({"uri":definition["uri"].as_str().unwrap().replace(":///", ":/")}),
    );
    let library = library.as_str().unwrap();
    let line = definition["range"]["start"]["line"].as_u64().unwrap() as usize;
    let declaration = library.lines().nth(line).unwrap();
    assert!(declaration.contains("native is_directory(path) -> Bool = \"fs.is_directory\""));
    let member = client.request(
        4,
        "textDocument/definition",
        json!({"textDocument":{"uri":uri},"position":{"line":3,"character":17}}),
    );
    assert_eq!(member["uri"], uri);
    assert_eq!(member["range"]["start"], json!({"line":2,"character":4}));
    let completion = client.request(
        5,
        "textDocument/completion",
        json!({"textDocument":{"uri":uri},"position":{"line":4,"character":16}}),
    );
    assert!(
        completion
            .as_array()
            .unwrap()
            .iter()
            .any(|item| item["label"] == "is_directory")
    );
    let hover = client.request(
        6,
        "textDocument/hover",
        json!({"textDocument":{"uri":uri},"position":{"line":4,"character":15}}),
    );
    assert!(
        hover["contents"]["value"]
            .as_str()
            .unwrap()
            .contains("native is_directory(path) -> Bool")
    );
    let signature = client.request(
        7,
        "textDocument/signatureHelp",
        json!({"textDocument":{"uri":uri},"position":{"line":4,"character":29}}),
    );
    assert!(
        signature["signatures"][0]["label"]
            .as_str()
            .unwrap()
            .contains("is_directory(path)")
    );
    assert_eq!(signature["activeParameter"], 0);
    client.finish();
}
