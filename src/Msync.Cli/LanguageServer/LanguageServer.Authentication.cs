using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private abstract record AuthEvent : ServerEvent;
    private sealed record PromptAuthentication(SshPrompt Prompt, TaskCompletionSource<string?> Completion, CancellationToken Cancellation) : AuthEvent;
    private sealed record CancelAuthentication(string Id) : AuthEvent;
    private sealed record PendingAuthentication(SshPrompt Prompt, TaskCompletionSource<string?> Completion);
    private readonly Dictionary<string, PendingAuthentication> authentication = new(StringComparer.Ordinal);
    private bool sshPromptSupported;

    private void InitializeAuthentication(JsonElement parameters) => sshPromptSupported =
        parameters.TryGetProperty("initializationOptions", out var options) && options.ValueKind == JsonValueKind.Object
        && options.TryGetProperty("sshPrompt", out var supported) && supported.ValueKind == JsonValueKind.True;

    private async Task<string?> PromptForSshAsync(SshPrompt prompt, CancellationToken cancellationToken)
    {
        TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!events.Writer.TryWrite(new PromptAuthentication(prompt, completion, cancellationToken))) return null;
        try { return await completion.Task.WaitAsync(cancellationToken); }
        finally
        {
            if (cancellationToken.IsCancellationRequested) events.Writer.TryWrite(new CancelAuthentication(prompt.Id));
        }
    }

    private void HandleAuthentication(AuthEvent item)
    {
        if (item is CancelAuthentication cancel)
        {
            if (authentication.Remove("msync-auth-" + cancel.Id, out var pending))
            {
                CancelAuthenticationUi(pending.Prompt.Id);
                pending.Completion.TrySetResult(null);
            }
            return;
        }
        var request = (PromptAuthentication)item;
        if (shuttingDown || request.Cancellation.IsCancellationRequested) { request.Completion.TrySetResult(null); return; }
        if (!sshPromptSupported)
        {
            transport.Write(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "window/showMessage",
                ["params"] = new JsonObject
                {
                    ["type"] = 1,
                    ["message"] = $"SSH authentication is required for {request.Prompt.Endpoint}, but this editor cannot answer native SSH prompts. Use the current msync extension, or configure native SSH keys/agent authentication. Native prompt: {request.Prompt.Prompt}"
                }
            });
            request.Completion.TrySetResult(null);
            return;
        }
        string id = "msync-auth-" + request.Prompt.Id;
        authentication[id] = new(request.Prompt, request.Completion);
        transport.Write(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "msync/sshPrompt",
            ["params"] = new JsonObject
            {
                ["id"] = request.Prompt.Id,
                ["endpoint"] = request.Prompt.Endpoint,
                ["prompt"] = request.Prompt.Prompt,
                ["kind"] = request.Prompt.Kind
            }
        });
    }

    private bool HandleAuthenticationResponse(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var identifier) || identifier.ValueKind != JsonValueKind.String
            || !authentication.Remove(identifier.GetString()!, out var pending)) return false;
        string? answer = message.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        pending.Completion.TrySetResult(answer);
        return true;
    }

    private void StopAuthentication()
    {
        foreach (var pending in authentication.Values)
        {
            CancelAuthenticationUi(pending.Prompt.Id);
            pending.Completion.TrySetResult(null);
        }
        authentication.Clear();
    }

    private void CancelAuthenticationUi(string id) => transport.Write(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "msync/sshPromptCancel",
        ["params"] = new JsonObject { ["id"] = id }
    });
}
