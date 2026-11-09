using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Msync.Cli;

[TomlSerializable(typeof(TomlTable))]
internal partial class ConfigurationTomlContext : TomlSerializerContext
{
    public ConfigurationTomlContext() { }
    public ConfigurationTomlContext(TomlSerializerOptions options) : base(options) { }
}
