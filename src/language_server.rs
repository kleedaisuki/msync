//! Stdio LSP server sharing the parser, compiler and owned source spans.

mod analysis;
mod documents;
mod providers;

use std::collections::BTreeMap;
use std::time::Duration;

use lsp_server::{Connection, ErrorCode, Message, Notification, Request, Response, ResponseError};
use lsp_types::*;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};

use crate::syntax::{Report, Span};

use documents::{Documents, MAX_DOCUMENT_BYTES, OpenDocument};

/// Serve one editor connection. Standard output is exclusively LSP framing.
pub fn run_stdio() -> Result<(), String> {
    let (connection, threads) = Connection::stdio();
    let result = serve(&connection);
    drop(connection);
    // An invalid handshake can leave the reader awaiting client input.
    // The dedicated server process exits; do not wait indefinitely on stdin.
    result?;
    threads
        .join()
        .map_err(|error| format!("language-server transport failed: {error}"))
}

fn serve(connection: &Connection) -> Result<(), String> {
    let (id, parameters) = connection
        .initialize_start()
        .map_err(|error| error.to_string())?;
    let initialize: InitializeParams = serde_json::from_value(parameters)
        .map_err(|error| format!("invalid language-server initialization: {error}"))?;
    let capabilities = json!({
        "positionEncoding": "utf-16",
        "textDocumentSync": {"openClose": true, "change": 2, "save": {"includeText": false}},
        "completionProvider": {"triggerCharacters": ["$", ":", "|"]},
        "hoverProvider": true, "definitionProvider": true, "documentSymbolProvider": true,
        "documentFormattingProvider": true,
        "semanticTokensProvider": {"legend": {"tokenTypes": providers::TOKEN_TYPES, "tokenModifiers": ["declaration"]}, "full": true}
    });
    connection.initialize_finish(id, json!({"capabilities": capabilities, "serverInfo": {"name": "msync", "version": env!("CARGO_PKG_VERSION")}})).map_err(|error| error.to_string())?;
    let mut documents = Documents {
        open: BTreeMap::new(),
        root: root_path(&initialize)?,
    };
    let analyzer = analysis::Analyzer::new()?;
    loop {
        let message = match connection.receiver.try_recv() {
            Ok(message) => message,
            Err(_) => {
                publish_completed(connection, &documents, &analyzer)?;
                match connection.receiver.recv_timeout(Duration::from_millis(60)) {
                    Ok(message) => message,
                    Err(error) if error.is_timeout() => {
                        publish_dirty(connection, &mut documents, &analyzer)?;
                        continue;
                    }
                    Err(_) => return Ok(()),
                }
            }
        };
        match message {
            Message::Request(request) => {
                if connection
                    .handle_shutdown(&request)
                    .map_err(|error| error.to_string())?
                {
                    return Ok(());
                }
                respond(connection, &mut documents, request)?;
            }
            Message::Notification(notification) if notification.method == "exit" => {
                return Err("language server exited without a shutdown request".into());
            }
            Message::Notification(notification) => {
                notify(connection, &mut documents, &analyzer, notification)?
            }
            Message::Response(_) => {}
        }
    }
}

#[allow(deprecated)]
fn root_path(initialize: &InitializeParams) -> Result<std::path::PathBuf, String> {
    let uri = initialize
        .workspace_folders
        .as_ref()
        .and_then(|folders| folders.first())
        .map(|folder| &folder.uri)
        .or(initialize.root_uri.as_ref());
    if let Some(path) = uri
        .and_then(|uri| url::Url::parse(uri.as_str()).ok())
        .and_then(|uri| uri.to_file_path().ok())
    {
        return Ok(path);
    }
    std::env::current_dir()
        .map_err(|error| format!("cannot establish language-server working directory: {error}"))
}

fn respond(
    connection: &Connection,
    documents: &mut Documents,
    request: Request,
) -> Result<(), String> {
    let result = request_result(documents, &request.method, request.params);
    let response = match result {
        Ok(result) => Response::new_ok(request.id, result),
        Err(error) => Response::new_err(request.id, error.code, error.message),
    };
    send(connection, Message::Response(response))
}

fn request_result(
    documents: &mut Documents,
    method: &str,
    parameters: Value,
) -> Result<Value, ResponseError> {
    match method {
        "textDocument/completion" => {
            let parameters: CompletionParams = decode(parameters)?;
            let location = parameters.text_document_position;
            let document = opened(documents, &location.text_document.uri)?;
            value(CompletionResponse::Array(providers::completion(
                document,
                location.position,
            )))
        }
        "textDocument/hover" => {
            let parameters: HoverParams = decode(parameters)?;
            let location = parameters.text_document_position_params;
            let document = opened(documents, &location.text_document.uri)?;
            value(providers::hover(document, location.position))
        }
        "textDocument/definition" => {
            let parameters: GotoDefinitionParams = decode(parameters)?;
            let location = parameters.text_document_position_params;
            let document = opened(documents, &location.text_document.uri)?;
            value(providers::definition(
                document,
                &location.text_document.uri,
                location.position,
            ))
        }
        "textDocument/documentSymbol" => {
            let parameters: DocumentSymbolParams = decode(parameters)?;
            value(providers::symbols(opened(
                documents,
                &parameters.text_document.uri,
            )?))
        }
        "textDocument/semanticTokens/full" => {
            let parameters: SemanticTokensParams = decode(parameters)?;
            value(providers::semantic_tokens(opened(
                documents,
                &parameters.text_document.uri,
            )?))
        }
        "textDocument/formatting" => {
            let parameters: DocumentFormattingParams = decode(parameters)?;
            let document = opened(documents, &parameters.text_document.uri)?;
            let formatted = crate::language::format(&document.text.source).map_err(|reports| {
                error(
                    ErrorCode::InvalidParams,
                    reports
                        .first()
                        .map(|report| report.diagnostic.message.clone())
                        .unwrap_or_default(),
                )
            })?;
            let edits = if formatted == document.text.source {
                Vec::new()
            } else {
                vec![TextEdit::new(
                    Range::new(Position::new(0, 0), document.text.end()),
                    formatted,
                )]
            };
            value(edits)
        }
        _ => Err(error(
            ErrorCode::MethodNotFound,
            format!("unsupported language-server method '{method}'"),
        )),
    }
}

fn opened<'a>(
    documents: &'a mut Documents,
    uri: &Uri,
) -> Result<&'a mut OpenDocument, ResponseError> {
    let document = documents
        .open
        .get_mut(uri)
        .ok_or_else(|| error(ErrorCode::InvalidParams, "document is not open"))?;
    if document.text.source.len() > MAX_DOCUMENT_BYTES {
        return Err(error(
            ErrorCode::InvalidParams,
            "document exceeds the 8 MiB analysis limit",
        ));
    }
    Ok(document)
}

fn notify(
    connection: &Connection,
    documents: &mut Documents,
    analyzer: &analysis::Analyzer,
    notification: Notification,
) -> Result<(), String> {
    let result = notification_result(
        connection,
        documents,
        analyzer,
        &notification.method,
        notification.params,
    );
    if let Err(error) = result {
        let message = format!("{}: {error}", notification.method);
        send(
            connection,
            Message::Notification(Notification::new(
                "window/logMessage".into(),
                json!({"type": 2, "message": message}),
            )),
        )?;
    }
    Ok(())
}

fn notification_result(
    connection: &Connection,
    documents: &mut Documents,
    analyzer: &analysis::Analyzer,
    method: &str,
    parameters: Value,
) -> Result<(), String> {
    match method {
        "textDocument/didOpen" => {
            let parameters: DidOpenTextDocumentParams =
                serde_json::from_value(parameters).map_err(|error| error.to_string())?;
            let item = parameters.text_document;
            documents
                .open
                .insert(item.uri.clone(), OpenDocument::new(item.text, item.version));
            publish(connection, documents, analyzer, &item.uri)
        }
        "textDocument/didChange" => {
            let parameters: DidChangeTextDocumentParams =
                serde_json::from_value(parameters).map_err(|error| error.to_string())?;
            let document = documents
                .open
                .get_mut(&parameters.text_document.uri)
                .ok_or("changed document is not open")?;
            document
                .apply(parameters.text_document.version, parameters.content_changes)
                .map(|_| ())
        }
        "textDocument/didSave" => {
            let parameters: DidSaveTextDocumentParams =
                serde_json::from_value(parameters).map_err(|error| error.to_string())?;
            publish(
                connection,
                documents,
                analyzer,
                &parameters.text_document.uri,
            )
        }
        "textDocument/didClose" => {
            let parameters: DidCloseTextDocumentParams =
                serde_json::from_value(parameters).map_err(|error| error.to_string())?;
            documents.open.remove(&parameters.text_document.uri);
            analyzer.forget(&parameters.text_document.uri);
            send(
                connection,
                Message::Notification(Notification::new(
                    "textDocument/publishDiagnostics".into(),
                    PublishDiagnosticsParams::new(parameters.text_document.uri, Vec::new(), None),
                )),
            )
        }
        _ => Ok(()),
    }
}

fn publish_dirty(
    connection: &Connection,
    documents: &mut Documents,
    analyzer: &analysis::Analyzer,
) -> Result<(), String> {
    let uris: Vec<_> = documents
        .open
        .iter()
        .filter(|(_, document)| document.dirty)
        .map(|(uri, _)| uri.clone())
        .collect();
    for uri in uris {
        publish(connection, documents, analyzer, &uri)?;
    }
    Ok(())
}

fn publish(
    connection: &Connection,
    documents: &mut Documents,
    analyzer: &analysis::Analyzer,
    uri: &Uri,
) -> Result<(), String> {
    let context = documents.context(uri);
    let Some(document) = documents.open.get_mut(uri) else {
        return Ok(());
    };
    let reports = if document.text.source.len() > MAX_DOCUMENT_BYTES {
        vec![Report::new(
            "document exceeds the 8 MiB analysis limit",
            Span::default(),
        )]
    } else {
        document.syntax().reports.clone()
    };
    document.dirty = false;
    if reports.is_empty() {
        let ast = document.syntax().document.clone();
        return analyzer.submit(analysis::Job {
            uri: uri.clone(),
            version: document.version,
            generation: document.generation,
            source: document.text.source.clone(),
            document: ast,
            context,
        });
    }
    analyzer.forget(uri);
    let diagnostics = reports
        .into_iter()
        .map(|report| analysis::diagnostic(&document.text, uri, report))
        .collect();
    send(
        connection,
        Message::Notification(Notification::new(
            "textDocument/publishDiagnostics".into(),
            PublishDiagnosticsParams::new(uri.clone(), diagnostics, Some(document.version)),
        )),
    )
}

fn publish_completed(
    connection: &Connection,
    documents: &Documents,
    analyzer: &analysis::Analyzer,
) -> Result<(), String> {
    for completed in analyzer.completed.try_iter() {
        let Some(document) = documents.open.get(&completed.uri) else {
            continue;
        };
        if document.generation != completed.generation {
            continue;
        }
        send(
            connection,
            Message::Notification(Notification::new(
                "textDocument/publishDiagnostics".into(),
                PublishDiagnosticsParams::new(
                    completed.uri,
                    completed.diagnostics,
                    Some(completed.version),
                ),
            )),
        )?;
    }
    Ok(())
}

fn decode<T: DeserializeOwned>(parameters: Value) -> Result<T, ResponseError> {
    serde_json::from_value(parameters)
        .map_err(|cause| error(ErrorCode::InvalidParams, cause.to_string()))
}
fn value<T: serde::Serialize>(value: T) -> Result<Value, ResponseError> {
    serde_json::to_value(value).map_err(|cause| error(ErrorCode::InternalError, cause.to_string()))
}
fn error(code: ErrorCode, message: impl Into<String>) -> ResponseError {
    ResponseError {
        code: code as i32,
        message: message.into(),
        data: None,
    }
}
fn send(connection: &Connection, message: Message) -> Result<(), String> {
    connection
        .sender
        .send(message)
        .map_err(|error| format!("language-server client disconnected: {error}"))
}
