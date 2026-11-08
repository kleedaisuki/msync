using System.Text.Json.Serialization;

namespace Msync.Cli;

[JsonSerializable(typeof(int[]))]
internal partial class LanguageServerJson : JsonSerializerContext;
