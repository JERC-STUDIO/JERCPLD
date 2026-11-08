namespace JERCPLD.Core;

/// <summary>协议号的数据来源，用于在 UI 和文档中如实标注可信度。</summary>
public enum ProtocolVersionSource
{
    /// <summary>未核实，禁止用于生产翻译。</summary>
    Unverified = 0,

    /// <summary>来自社区维护的版本数据（PrismarineJS minecraft-data 等），需实测复核。</summary>
    Community = 1,

    /// <summary>来自 Mojang 官方协议文档（mojang.github.io/bedrock-protocol-docs）的发布记录。</summary>
    MojangOfficial = 2,
}

/// <summary>
/// Minecraft 基岩版协议版本。
/// <para>
/// 协议号必须与客户端/BDS 登录时上报的 Client Network Version 完全一致，否则会被踢出。
/// 同一个协议号可能对应多个发布版本（如 1.26.60 / 1.26.61 / 1.26.62 都是 2225），
/// 这些版本之间线格式完全相同，属于「同字节不同号」的等价版本。
/// </para>
/// </summary>
public readonly record struct ProtocolVersion(
    int Number,
    string MinecraftVersion,
    ProtocolVersionSource Source = ProtocolVersionSource.Unverified,
    string? SourceNote = null) : IComparable<ProtocolVersion>
{
    public bool IsKnown => Number > 0;

    /// <summary>是否与另一版本使用完全相同的线上协议（协议号相同即线格式相同）。</summary>
    public bool IsWireEquivalentTo(ProtocolVersion other) => Number == other.Number;

    public int CompareTo(ProtocolVersion other) => Number.CompareTo(other.Number);

    public override string ToString() => IsKnown ? $"{MinecraftVersion} ({Number})" : $"unknown ({Number})";
}

/// <summary>
/// 已知版本的集中登记表。新增版本时首先在这里登记，然后在 VersionMap 中注册适配器。
/// </summary>
public static class KnownProtocolVersions
{
    // 来源说明（2026-11-08 核实）：
    //  - Mojang 官方协议文档 changelog 的每个发布条目都带 Released 日期与 Protocol 号：
    //    https://mojang.github.io/bedrock-protocol-docs/changelog/
    //  - 1.26.30 官方文档未提供该版本页面，协议号取自社区数据：
    //    https://raw.githubusercontent.com/PrismarineJS/minecraft-data/master/data/bedrock/common/protocolVersions.json
    //    标记为 Community，必须在真实 BDS 上实测复核后才能用于生产。
    public static readonly ProtocolVersion V1_20_80 = new(671, "1.20.80", ProtocolVersionSource.Community);
    public static readonly ProtocolVersion V1_21_0 = new(685, "1.21.0", ProtocolVersionSource.Community);
    public static readonly ProtocolVersion V1_21_2 = new(686, "1.21.2", ProtocolVersionSource.Community);
    public static readonly ProtocolVersion V1_21_20 = new(712, "1.21.20", ProtocolVersionSource.Community);

    public static readonly ProtocolVersion V1_26_30 = new(
        1001, "1.26.30", ProtocolVersionSource.Community,
        "官方文档未提供该版本页；协议号来自 PrismarineJS minecraft-data，需实测复核");

    public static readonly ProtocolVersion V1_26_40 = new(
        2168, "1.26.40", ProtocolVersionSource.MojangOfficial, "官方 changelog：2026-08-21，protocol 2168");

    public static readonly ProtocolVersion V1_26_43 = new(
        2168, "1.26.43", ProtocolVersionSource.MojangOfficial, "官方 changelog：2026-08-21，protocol 2168");

    public static readonly ProtocolVersion V1_26_44 = new(
        2168, "1.26.44", ProtocolVersionSource.MojangOfficial, "官方 changelog：2026-08-21，protocol 2168");

    public static readonly ProtocolVersion V1_26_45 = new(
        2169, "1.26.45", ProtocolVersionSource.MojangOfficial, "官方 changelog：2026-08-21，protocol 2169");

    public static readonly ProtocolVersion V1_26_50 = new(
        2193, "1.26.50", ProtocolVersionSource.MojangOfficial, "官方 changelog：2026-09-15，protocol 2193");

    public static readonly ProtocolVersion V1_26_51 = new(
        2193, "1.26.51", ProtocolVersionSource.MojangOfficial, "官方 changelog：2026-09-16，protocol 2193");

    public static readonly ProtocolVersion V1_26_60 = new(
        2225, "1.26.60", ProtocolVersionSource.MojangOfficial, "官方 changelog：2026-10-27，protocol 2225");

    public static readonly ProtocolVersion V1_26_61 = new(
        2225, "1.26.61", ProtocolVersionSource.MojangOfficial, "官方 changelog：2026-10-29，protocol 2225");

    public static readonly ProtocolVersion V1_26_62 = new(
        2225, "1.26.62", ProtocolVersionSource.MojangOfficial, "官方 changelog：2026-11-05，protocol 2225");

    public static IReadOnlyList<ProtocolVersion> All { get; } = new[]
    {
        V1_20_80,
        V1_21_0,
        V1_21_2,
        V1_21_20,
        V1_26_30,
        V1_26_40,
        V1_26_43,
        V1_26_44,
        V1_26_45,
        V1_26_50,
        V1_26_51,
        V1_26_60,
        V1_26_61,
        V1_26_62,
    };

    /// <summary>UI 版本下拉框覆盖的范围：26.30 – 26.60（含该区间内的补丁版本）。</summary>
    public static IReadOnlyList<ProtocolVersion> SupportedRange { get; } = All
        .Where(version => version.MinecraftVersion.StartsWith("1.26.", StringComparison.Ordinal))
        .OrderBy(version => version.MinecraftVersion, StringComparer.Ordinal)
        .ToArray();

    public static ProtocolVersion? FindByNumber(int number)
    {
        foreach (var version in All)
        {
            if (version.Number == number)
            {
                return version;
            }
        }

        return null;
    }

    public static ProtocolVersion? FindByMinecraftVersion(string minecraftVersion)
    {
        foreach (var version in All)
        {
            if (string.Equals(version.MinecraftVersion, minecraftVersion, StringComparison.OrdinalIgnoreCase))
            {
                return version;
            }
        }

        return null;
    }

    /// <summary>
    /// 找出与指定版本线格式完全相同的其它发布版本（「同字节不同号」等价版本）。
    /// </summary>
    public static IReadOnlyList<ProtocolVersion> FindWireEquivalent(ProtocolVersion version)
        => All.Where(candidate => candidate.Number == version.Number).ToArray();
}
