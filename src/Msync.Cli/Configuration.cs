using System.Globalization;
using System.Text;
using Msync.Core;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Msync.Cli;

internal sealed record Setting(object Value, string Origin);

internal sealed class Configuration
{
    private readonly Dictionary<string, Setting> settings = Defaults();
    public int Jobs => checked((int)Unsigned("execution.jobs"));
    public int BufferSize => checked((int)Unsigned("execution.buffer_size"));
    public ulong MemoryLimit => Unsigned("execution.memory_limit");
    public ulong? SpoolLimit => settings["execution.spool_limit"].Value is "unlimited" ? null : Unsigned("execution.spool_limit");
    public string Syntax => Text("language.syntax");
    public string? StateDirectory => OptionalText("execution.state_dir");
    public bool TelemetryEnabled => settings["telemetry.enabled"].Value is true;
    public string TelemetryBackend => Text("telemetry.backend");
    public string? TelemetryPath => OptionalText("telemetry.path");
    internal ulong TelemetryUnsigned(string name) => Unsigned($"telemetry.{name}");
    internal bool TelemetryBoolean(string name) => settings[$"telemetry.{name}"].Value is bool value
        ? value : throw Error($"telemetry.{name}", "must be a Boolean");

    public static Configuration Load(string? path, bool disabled, IReadOnlyDictionary<string, object>? overrides = null)
    {
        if (path is not null && disabled) throw new ArgumentException("--config and --no-config cannot be combined.");
        Configuration configuration = new();
        string? selected = disabled ? null : path ?? Discover();
        if (selected is not null) configuration.Read(Path.GetFullPath(selected));
        if (Environment.GetEnvironmentVariable("MSYNC_STATE_DIR") is { Length: > 0 } state)
            configuration.Set("execution.state_dir", Path.GetFullPath(state), "environment MSYNC_STATE_DIR");
        if (overrides is not null)
            foreach (var (key, value) in overrides) configuration.Set(key, value, "command line");
        return configuration;
    }

    public void Validate()
    {
        foreach (string key in new[] { "execution.jobs", "execution.max_streams", "execution.buffer_size", "execution.memory_limit" })
            if (Unsigned(key) == 0) throw Error(key, "must be positive");
        if (Unsigned("execution.jobs") > int.MaxValue) throw Error("execution.jobs", "exceeds the platform's task-count representation");
        if (Unsigned("execution.buffer_size") > int.MaxValue) throw Error("execution.buffer_size", "exceeds the platform's buffer representation");
        if (MemoryLimit < (ulong)BufferSize) throw Error("execution.memory_limit", "must admit at least one execution.buffer_size buffer");
        _ = SpoolLimit;
        RequireChoice("ui.color", "auto", "always", "never");
        RequireChoice("language.syntax", "0.8", "0.8.0", "0.7", "0.7.3", "0.6", "0.5");
        if (settings["telemetry.enabled"].Value is not bool) throw Error("telemetry.enabled", "must be a Boolean");
        _ = Unsigned("progress.delay_ms");
        _ = Unsigned("progress.interval_ms");
    }

    public void Dump(TextWriter writer)
    {
        string? section = null;
        foreach (var (key, setting) in settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string group = key[..key.IndexOf('.')];
            if (section != group)
            {
                if (section is not null) writer.WriteLine();
                writer.WriteLine($"[{group}]");
                section = group;
            }
            writer.WriteLine($"# source: {setting.Origin}");
            writer.WriteLine($"{key[(key.IndexOf('.') + 1)..]} = {Literal(setting.Value)}");
        }
    }

    private void Read(string path)
    {
        TomlTable table;
        TomlMetadataStore metadata = new();
        try
        {
            TomlSerializerOptions options = new() { SourceName = path, MetadataStore = metadata };
            table = TomlSerializer.Deserialize(File.ReadAllText(path), new ConfigurationTomlContext(options).TomlTable)
                ?? throw new MsyncException($"{path}: configuration must be a TOML table");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TomlException)
        {
            throw new MsyncException($"Cannot load configuration '{path}': {e.Message}", inner: e);
        }
        ReadTable(table, "", path, metadata);
    }

    private void ReadTable(TomlTable table, string prefix, string path, TomlMetadataStore metadata)
    {
        foreach (var (name, value) in table)
        {
            string key = prefix.Length == 0 ? name : $"{prefix}.{name}";
            string origin = $"configuration {path}";
            if (metadata.TryGetProperties(table, out var properties) && properties is not null && properties.TryGetProperty(name, out var property) && property is not null)
                origin += $":{property.Span.Start.Line + 1}:{property.Span.Start.Column + 1}";
            if (value is TomlTable child)
            {
                if (!KnownSection(key)) throw new MsyncException($"{origin}: unknown configuration table '{key}'");
                ReadTable(child, key, path, metadata);
                continue;
            }
            if (!KnownKey(key)) throw new MsyncException($"{origin}: unknown configuration key '{key}'");
            object scalar = value ?? throw new MsyncException($"{origin}: '{key}' has no value");
            if (scalar is not (string or long or bool)) throw new MsyncException($"{origin}: '{key}' requires a scalar String, integer or Boolean");
            if (IsPathKey(key)) scalar = Path.GetFullPath(scalar as string ?? throw new MsyncException($"{origin}: '{key}' must be a String"), Path.GetDirectoryName(path)!);
            Set(key, scalar, origin);
        }
    }

    private static string? Discover()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string baseDirectory = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".config");
        string canonical = Path.Combine(baseDirectory, "msync", "config.toml");
        if (Present(canonical)) return canonical;
        string fallback = Path.Combine(home, ".msync", "config.toml");
        return Present(fallback) ? fallback : null;
    }

    private static bool Present(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private void Set(string key, object value, string origin)
    {
        if (!KnownKey(key)) throw new ArgumentException($"Unknown configuration override '{key}'.");
        settings[key] = new(value, origin);
    }

    private ulong Unsigned(string key)
    {
        object value = settings[key].Value;
        if (value is ulong unsigned) return unsigned;
        if (value is int integer && integer >= 0) return (ulong)integer;
        if (value is long number && number >= 0) return (ulong)number;
        if (value is not string text) throw Error(key, "must be nonnegative integer bytes or a byte-size String");
        foreach (var (suffix, factor) in new[] { ("KiB", 1024UL), ("MiB", 1024UL * 1024), ("GiB", 1024UL * 1024 * 1024), ("B", 1UL), ("", 1UL) })
        {
            if (!text.EndsWith(suffix, StringComparison.Ordinal)) continue;
            string digits = suffix.Length == 0 ? text : text[..^suffix.Length].TrimEnd();
            if (ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong count) && count <= ulong.MaxValue / factor) return count * factor;
        }
        throw Error(key, "must be nonnegative bytes, optionally suffixed KiB, MiB or GiB");
    }

    private string Text(string key) => settings[key].Value is string text ? text : throw Error(key, "must be a String");
    private string? OptionalText(string key) => settings.TryGetValue(key, out Setting? value) ? value.Value as string : null;
    private MsyncException Error(string key, string message) => new($"{settings[key].Origin}: configuration '{key}' {message}");
    private void RequireChoice(string key, params string[] choices)
    {
        if (!choices.Contains(Text(key), StringComparer.Ordinal)) throw Error(key, $"must be {string.Join(", ", choices)}");
    }

    private static bool IsPathKey(string key) => key is "execution.state_dir" or "telemetry.path";
    private static bool KnownSection(string key) => key is "execution" or "language" or "ui" or "progress" or "telemetry";
    private bool KnownKey(string key) => settings.ContainsKey(key) || IsPathKey(key);

    private static Dictionary<string, Setting> Defaults()
    {
        Dictionary<string, object> values = new(StringComparer.Ordinal)
        {
            ["execution.jobs"] = 4L, ["execution.max_streams"] = 16L,
            ["execution.buffer_size"] = 65536L, ["execution.memory_limit"] = 67108864L,
            ["execution.spool_limit"] = "unlimited", ["language.syntax"] = "0.8", ["ui.color"] = "auto",
            ["progress.delay_ms"] = 200L, ["progress.interval_ms"] = 250L,
            ["telemetry.enabled"] = false, ["telemetry.backend"] = "sqlite",
            ["telemetry.queue_capacity"] = 4096L, ["telemetry.batch_size"] = 128L,
            ["telemetry.flush_interval_ms"] = 100L, ["telemetry.busy_timeout_ms"] = 1000L,
            ["telemetry.synchronous_full"] = false, ["telemetry.sample_per_million"] = 1000000L,
            ["telemetry.max_sessions"] = long.MaxValue, ["telemetry.max_records_per_session"] = long.MaxValue,
            ["telemetry.max_database_bytes"] = long.MaxValue
        };
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length != 0) values["execution.state_dir"] = Path.Combine(home, ".msync", "state");
        return values.ToDictionary(pair => pair.Key, pair => new Setting(pair.Value, "default"), StringComparer.Ordinal);
    }

    private static string Literal(object value) => value switch
    {
        bool boolean => boolean ? "true" : "false",
        string text => Quote(text),
        ulong number when number > long.MaxValue => Quote(number.ToString(CultureInfo.InvariantCulture)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
    };

    private static string Quote(string text)
    {
        StringBuilder result = new("\"");
        foreach (char c in text)
            result.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", < ' ' => $"\\u{(int)c:x4}", _ => c.ToString() });
        return result.Append('"').ToString();
    }
}

[TomlSerializable(typeof(TomlTable))]
internal partial class ConfigurationTomlContext : TomlSerializerContext
{
    public ConfigurationTomlContext() { }
    public ConfigurationTomlContext(TomlSerializerOptions options) : base(options) { }
}
