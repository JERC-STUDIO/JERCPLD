using JERCPLD.VersionMap.Content;

internal static class ContentFallbackTests
{
    public static void Run()
    {
        var map = ContentFallbackMap.V2225ToV2193;
        Assert(map.SourceProtocol == 2225, "fallback 源协议号错误");
        Assert(map.TargetProtocol == 2193, "fallback 目标协议号错误");

        Assert(map.MapBlockType("minecraft:disc_press") == "minecraft:jukebox", "disc_press 映射错误");
        Assert(map.MapBlockType("minecraft:ice_crystal") == "minecraft:amethyst_cluster", "ice_crystal 映射错误");
        Assert(map.MapBlockType("minecraft:icicle") == "minecraft:pointed_dripstone", "icicle 映射错误");
        Assert(map.MapBlockType("minecraft:stone") == "minecraft:stone", "未知方块不应改写");

        Assert(map.MapBiome("minecraft:ice_caves") == "minecraft:dripstone_caves", "冰洞群系映射错误");
        Assert(map.MapEntity("minecraft:frostbite") == "minecraft:zombie", "frostbite 映射错误");
        Assert(map.MapEntity("minecraft:ice_ball") == "minecraft:snowball", "ice_ball 映射错误");
        Assert(map.MapEntity("minecraft:cow") == "minecraft:cow", "未知实体不应改写");
        Assert(map.MapMobEffect("minecraft:freezing") == "38", "freezing 效果映射错误");

        Console.WriteLine("PASS 内容注册表降级映射（2225 -> 2193）");
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
