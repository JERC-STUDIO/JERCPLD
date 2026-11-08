using JERCPLD.Core;

namespace JERCPLD.Desktop.Models;

/// <summary>版本下拉框的一项，负责把协议号、来源、等价版本拼成可读文本。</summary>
public sealed class VersionOption
{
    public VersionOption(ProtocolVersion version)
    {
        Version = version;

        var source = version.Source switch
        {
            ProtocolVersionSource.MojangOfficial => "官方",
            ProtocolVersionSource.Community => "社区",
            _ => "未核实",
        };

        var equivalents = KnownProtocolVersions.FindWireEquivalent(version);
        var equivalentText = equivalents.Count > 1
            ? $"  ≡ {string.Join(" / ", equivalents.Select(item => item.MinecraftVersion))}"
            : string.Empty;

        Display = $"{version.MinecraftVersion}  协议 {version.Number}  [{source}]{equivalentText}";
    }

    public ProtocolVersion Version { get; }

    public string Display { get; }

    public override string ToString() => Display;
}
