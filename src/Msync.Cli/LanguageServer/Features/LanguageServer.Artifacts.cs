using System.Text.Json;
using System.Text.Json.Nodes;

namespace Msync.Cli;

internal sealed partial class LanguageServer
{
    private void BeginArtifact(JsonNode id, JsonElement parameters)
    {
        string uri = parameters.GetProperty("uri").GetString()
            ?? throw new ArgumentException("The artifact source URI is required.");
        string data = parameters.GetProperty("data").GetString()
            ?? throw new ArgumentException("The artifact byte payload is required.");
        if (shuttingDown)
        {
            Respond(id, code: -32800, message: "The language server is shutting down.");
            return;
        }
        CancelRequests(RequestKind.Artifact, uri);
        BeginRequest(id, uri, RequestKind.Artifact, null, work => Task.FromResult<JsonNode?>(
            ArtifactDisassembly.Read(Convert.FromBase64String(data), uri, work.Cancellation.Token).ToJson()));
    }
}
