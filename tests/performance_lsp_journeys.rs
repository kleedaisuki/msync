//! Independent actual-process acceptance for the editor-to-language-server journey.

#[path = "support/lsp_client.rs"]
mod lsp_client;

use std::fs;
use std::path::Path;

use serde_json::{Value, json};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("performance-lsp-")
        .tempdir_in(root)
        .unwrap()
}

fn diagnostics(client: &mut lsp_client::Client, uri: &str, version: i64) -> Value {
    client.receive(|message| {
        message["method"] == "textDocument/publishDiagnostics"
            && message["params"]["uri"] == uri
            && message["params"]["version"] == version
    })["params"]["diagnostics"]
        .clone()
}

fn offset(text: &str, position: &Value) -> usize {
    let line = position["line"].as_u64().unwrap() as usize;
    let column = position["character"].as_u64().unwrap() as usize;
    let start = text
        .split_inclusive('\n')
        .take(line)
        .map(str::len)
        .sum::<usize>();
    let mut utf16 = 0;
    for (byte, character) in text[start..].char_indices() {
        if utf16 == column {
            return start + byte;
        }
        assert_ne!(character, '\n', "LSP edit extends past the requested line");
        utf16 += character.len_utf16();
        assert!(utf16 <= column, "LSP edit splits a UTF-16 surrogate pair");
    }
    assert_eq!(utf16, column);
    text.len()
}

fn apply_edits(text: &str, edits: &Value) -> String {
    if edits.is_null() {
        return text.to_owned();
    }
    let mut edits = edits
        .as_array()
        .unwrap()
        .iter()
        .map(|edit| {
            (
                offset(text, &edit["range"]["start"]),
                offset(text, &edit["range"]["end"]),
                edit["newText"].as_str().unwrap(),
            )
        })
        .collect::<Vec<_>>();
    edits.sort_by_key(|(start, end, _)| (*start, *end));
    let mut output = text.to_owned();
    for (start, end, replacement) in edits.into_iter().rev() {
        output.replace_range(start..end, replacement);
    }
    output
}

#[test]
fn stdio_editor_roundtrip_uses_utf16_diagnostics_tokens_incremental_edits_and_formatting() {
    let root = workspace();
    let uri = url::Url::from_file_path(root.path().join("editor.msync"))
        .unwrap()
        .to_string();
    let mut client = lsp_client::Client::start(root.path());
    let initialized = client.request(
        1,
        "initialize",
        json!({
            "processId": null,
            "rootUri": url::Url::from_directory_path(root.path()).unwrap().to_string(),
            "capabilities": {"general": {"positionEncodings": ["utf-16"]}}
        }),
    );
    let capabilities = &initialized["capabilities"];
    assert_eq!(capabilities["positionEncoding"], "utf-16");
    assert!(
        capabilities["documentFormattingProvider"]
            .as_bool()
            .unwrap()
    );
    let token_types = capabilities["semanticTokensProvider"]["legend"]["tokenTypes"]
        .as_array()
        .unwrap()
        .clone();
    client.notify("initialized", json!({}));
    let invalid = "flag = \"🎇\" == true\n";
    client.notify(
        "textDocument/didOpen",
        json!({"textDocument": {"uri": uri, "languageId": "msync", "version": 1, "text": invalid}}),
    );
    let errors = diagnostics(&mut client, &uri, 1);
    let error = errors
        .as_array()
        .unwrap()
        .iter()
        .find(|error| error["severity"] == 1)
        .expect("mismatched string/bool comparison must publish an error");
    let operator_byte = invalid.find("==").unwrap();
    assert_eq!(error["range"]["start"]["line"], 0);
    assert_eq!(
        error["range"]["start"]["character"],
        invalid[..operator_byte].encode_utf16().count()
    );

    let tokens = client.request(
        2,
        "textDocument/semanticTokens/full",
        json!({"textDocument": {"uri": uri}}),
    );
    let data = tokens["data"].as_array().unwrap();
    assert_eq!(data.len() % 5, 0);
    let mut line = 0_u64;
    let mut column = 0_u64;
    let mut saw_astral_literal = false;
    for token in data.as_chunks::<5>().0 {
        let delta_line = token[0].as_u64().unwrap();
        line += delta_line;
        column = if delta_line == 0 { column } else { 0 } + token[1].as_u64().unwrap();
        let token_type = &token_types[token[3].as_u64().unwrap() as usize];
        if line == 0 && column == 7 && token_type == "string" {
            assert_eq!(
                token[2], 4,
                "quotes plus astral character occupy four UTF-16 units"
            );
            saw_astral_literal = true;
        }
    }
    assert!(
        saw_astral_literal,
        "missing Unicode string semantic token: {tokens}"
    );

    let true_start = invalid[..invalid.find("true").unwrap()]
        .encode_utf16()
        .count();
    client.notify(
        "textDocument/didChange",
        json!({
            "textDocument": {"uri": uri, "version": 2},
            "contentChanges": [{"range": {"start": {"line": 0, "character": true_start}, "end": {"line": 0, "character": true_start + 4}}, "text": "\"🎆\""}]
        }),
    );
    assert!(
        diagnostics(&mut client, &uri, 2)
            .as_array()
            .unwrap()
            .is_empty()
    );

    let unformatted = "flag=\"🎇\"==\"🎆\"\nif flag:\n    \"ok\"->output\n";
    client.notify(
        "textDocument/didChange",
        json!({"textDocument": {"uri": uri, "version": 3}, "contentChanges": [{"text": unformatted}]}),
    );
    assert!(
        diagnostics(&mut client, &uri, 3)
            .as_array()
            .unwrap()
            .is_empty()
    );
    let edits = client.request(
        3,
        "textDocument/formatting",
        json!({"textDocument": {"uri": uri}, "options": {"tabSize": 4, "insertSpaces": true}}),
    );
    let formatted = apply_edits(unformatted, &edits);
    assert_eq!(
        formatted,
        "flag = (\"🎇\" == \"🎆\")\nif flag:\n    \"ok\" -> output\n"
    );
    client.notify(
        "textDocument/didChange",
        json!({"textDocument": {"uri": uri, "version": 4}, "contentChanges": [{"text": formatted}]}),
    );
    assert!(
        diagnostics(&mut client, &uri, 4)
            .as_array()
            .unwrap()
            .is_empty()
    );
    let edits = client.request(
        4,
        "textDocument/formatting",
        json!({"textDocument": {"uri": uri}, "options": {"tabSize": 4, "insertSpaces": true}}),
    );
    assert_eq!(apply_edits(&formatted, &edits), formatted);

    let completion = client.request(
        5,
        "textDocument/completion",
        json!({"textDocument": {"uri": uri}, "position": {"line": 1, "character": 4}}),
    );
    let items = completion
        .as_array()
        .or_else(|| completion["items"].as_array())
        .expect("completion must return items for a declared binding");
    assert!(
        items.iter().any(|item| item["label"] == "flag"),
        "{completion}"
    );
    let definition = client.request(
        6,
        "textDocument/definition",
        json!({"textDocument": {"uri": uri}, "position": {"line": 1, "character": 4}}),
    );
    let location = definition.as_array().map_or(&definition, |locations| {
        locations.first().expect("missing flag definition")
    });
    let target_uri = location.get("uri").unwrap_or(&location["targetUri"]);
    let target_range = location
        .get("range")
        .unwrap_or(&location["targetSelectionRange"]);
    assert_eq!(target_uri, &uri);
    assert_eq!(target_range["start"]["line"], 0);
    assert_eq!(target_range["start"]["character"], 0);
    let hover = client.request(
        7,
        "textDocument/hover",
        json!({"textDocument": {"uri": uri}, "position": {"line": 1, "character": 4}}),
    );
    assert!(
        !hover["contents"].is_null(),
        "missing hover for binding: {hover}"
    );
    let symbols = client.request(
        8,
        "textDocument/documentSymbol",
        json!({"textDocument": {"uri": uri}}),
    );
    assert!(
        symbols
            .as_array()
            .unwrap()
            .iter()
            .any(|symbol| symbol["name"] == "flag"),
        "{symbols}"
    );
    client.notify(
        "textDocument/didClose",
        json!({"textDocument": {"uri": uri}}),
    );
    let cleared = client.receive(|message| {
        message["method"] == "textDocument/publishDiagnostics"
            && message["params"]["uri"] == uri
            && message["params"]["diagnostics"]
                .as_array()
                .is_some_and(Vec::is_empty)
    });
    assert!(
        cleared["params"]["diagnostics"]
            .as_array()
            .unwrap()
            .is_empty()
    );
    client.finish();
    assert!(
        !root.path().join("output").exists(),
        "language server executed the document"
    );
}

#[test]
fn stdio_include_diagnostics_keep_child_uri_utf16_provenance_and_clear_after_repair() {
    let root = workspace();
    let child = root.path().join("child.msync");
    fs::write(&child, "# 🎇\nflag = \"🎇\" == true\n").unwrap();
    let uri = url::Url::from_file_path(root.path().join("main.msync"))
        .unwrap()
        .to_string();
    let child_uri = url::Url::from_file_path(&child).unwrap().to_string();
    let mut client = lsp_client::Client::start(root.path());
    client.request(
        1,
        "initialize",
        json!({"processId": null, "rootUri": url::Url::from_directory_path(root.path()).unwrap().to_string(), "capabilities": {}}),
    );
    client.notify("initialized", json!({}));
    let source = "include \"child.msync\"\n";
    client.notify(
        "textDocument/didOpen",
        json!({"textDocument": {"uri": uri, "languageId": "msync", "version": 1, "text": source}}),
    );
    let errors = diagnostics(&mut client, &uri, 1);
    let related = errors
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|error| error["relatedInformation"].as_array().into_iter().flatten())
        .find(|related| related["location"]["uri"] == child_uri)
        .expect("included diagnostic lost its child's source URI");
    assert_eq!(related["location"]["range"]["start"]["line"], 1);
    assert_eq!(related["location"]["range"]["start"]["character"], 12);
    assert_eq!(related["location"]["range"]["end"]["character"], 14);
    fs::write(&child, "# 🎇\nflag = \"🎇\" == \"🎆\"\n").unwrap();
    client.notify(
        "textDocument/didChange",
        json!({"textDocument": {"uri": uri, "version": 2}, "contentChanges": [{"text": source}]}),
    );
    assert!(
        diagnostics(&mut client, &uri, 2)
            .as_array()
            .unwrap()
            .is_empty()
    );
    client.finish();
}
