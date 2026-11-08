using JERCPLD.VersionMap.Content;

internal static class BlockStateTableTests
{
    public static void Run()
    {
        var root = FindRepoRoot();
        var table2193 = BlockStateTable.Load(Path.Combine(root, "data", "generated", "block_states.2193.json"));
        var table2225 = BlockStateTable.Load(Path.Combine(root, "data", "generated", "block_states.2225.json"));

        Assert(table2193.Protocol == 2193, "2193 表协议号错误");
        Assert(table2225.Protocol == 2225, "2225 表协议号错误");
        Assert(table2193.StateCount > 20000, $"2193 状态数异常: {table2193.StateCount}");
        Assert(table2225.StateCount > table2193.StateCount, "2225 状态数应多于 2193");

        var mapper = new BlockStateMapper(table2225, table2193, ContentFallbackMap.V2225ToV2193);
        var report = mapper.MapAll();
        Console.WriteLine(
            $"2193/2225 states: {table2193.StateCount}/{table2225.StateCount}; "
            + $"exact={report.Counts.GetValueOrDefault(BlockStateMapKind.Exact)} "
            + $"fallback={report.Counts.GetValueOrDefault(BlockStateMapKind.FallbackName)} "
            + $"filtered={report.Counts.GetValueOrDefault(BlockStateMapKind.FilteredState)} "
            + $"default={report.Counts.GetValueOrDefault(BlockStateMapKind.DefaultState)} "
            + $"unmapped={report.Counts.GetValueOrDefault(BlockStateMapKind.Unmapped)} "
            + $"exactRate={report.ExactRate:P2}");

        Assert(report.ExactRate > 0.9, $"精确映射率过低: {report.ExactRate:P2}");
        Assert(report.Counts.GetValueOrDefault(BlockStateMapKind.DefaultState) == 0, "不应存在丢信息的 default 映射");
        Assert(report.Counts.GetValueOrDefault(BlockStateMapKind.Unmapped) == 0, "不应存在未映射状态");

        // 普通方块必须精确映射，且哈希在两个版本一致。
        var stone = table2225.GetByName("minecraft:stone").Single();
        var stoneMapped = mapper.Map(stone.Hash);
        Assert(stoneMapped.Kind == BlockStateMapKind.Exact, $"stone 应为精确映射，实际 {stoneMapped.Kind}");
        Assert(stoneMapped.TargetHash == stone.Hash, "stone 跨版本哈希应一致");

        // 2225 新增方块应通过 fallback 映射到 2193 的替代方块。
        var discPress = table2225.GetByName("minecraft:disc_press").FirstOrDefault();
        Assert(discPress is not null, "2225 表缺少 disc_press");
        var discMapped = mapper.Map(discPress!.Hash);
        Assert(discMapped.Kind != BlockStateMapKind.Unmapped, "disc_press 未映射");
        Assert(discMapped.TargetName == "minecraft:jukebox", $"disc_press 应映射到 jukebox，实际 {discMapped.TargetName}");

        // 旧版没有的连接位应被过滤，而不是落成 default 丢状态。
        var redstone = table2225.GetByName("minecraft:redstone_wire")
            .First(entry => entry.States.TryGetValue("redstone_signal", out var signal) && (int)signal == 7);
        var redstoneMapped = mapper.Map(redstone.Hash);
        Assert(redstoneMapped.Kind == BlockStateMapKind.FilteredState, $"redstone_wire 应为过滤映射，实际 {redstoneMapped.Kind}");
        Assert(table2193.TryGet(redstoneMapped.TargetHash, out var redstoneTarget), "redstone_wire 目标状态不存在");
        Assert(redstoneTarget.States.TryGetValue("redstone_signal", out var mappedSignal) && (int)mappedSignal == 7,
            "redstone_signal 应保留");

        // icicle 的厚度/悬挂状态应保留到 pointed_dripstone。
        var icicle = table2225.GetByName("minecraft:icicle")
            .First(entry => entry.States.TryGetValue("dripstone_thickness", out var thickness) && (string)thickness == "frustum"
                            && entry.States.TryGetValue("hanging", out var hanging) && (bool)hanging);
        var icicleMapped = mapper.Map(icicle.Hash);
        Assert(icicleMapped.Kind == BlockStateMapKind.FilteredState, $"icicle 应为过滤映射，实际 {icicleMapped.Kind}");
        Assert(table2193.TryGet(icicleMapped.TargetHash, out var icicleTarget), "icicle 目标状态不存在");
        Assert((string)icicleTarget.States["dripstone_thickness"] == "frustum", "icicle 厚度状态丢失");
        Assert((bool)icicleTarget.States["hanging"], "icicle 悬挂状态丢失");

        Console.WriteLine("PASS 方块状态表加载与 2225 -> 2193 映射");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JERCPLD.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到 JERCPLD.sln");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            Console.Error.WriteLine("FAIL " + message);
            Environment.Exit(1);
        }
    }
}
