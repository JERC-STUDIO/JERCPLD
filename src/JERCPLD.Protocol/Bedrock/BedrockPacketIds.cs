namespace JERCPLD.Protocol.Bedrock;

/// <summary>
/// Bedrock 游戏包的数字 ID（varint 编码）。
/// TODO(bedrock): 仅登记骨架阶段会用到的少数包；完整列表需按目标版本从 BDS 逆向结果或社区协议表核对。
/// 不同版本之间这些 ID 可能变化，这正是版本映射层要处理的内容。
/// TODO(bedrock): ServerToClientHandshake / ClientToServerHandshake 的密钥协商与 AES 流加密尚未实现。
/// TODO(bedrock): Login 包中的身份链、JWT、皮肤/客户端数据字段尚未建模。
/// </summary>
public static class BedrockPacketIds
{
    public const int Login = 0x01;
    public const int PlayStatus = 0x02;
    public const int ServerToClientHandshake = 0x03;
    public const int ClientToServerHandshake = 0x04;
    public const int Disconnect = 0x05;
    public const int ResourcePacksInfo = 0x06;
    public const int ResourcePackStack = 0x07;
    public const int ResourcePackClientResponse = 0x08;
    public const int Text = 0x09;
    public const int SetTime = 0x0A;
    public const int StartGame = 0x0B;
    public const int AddPlayer = 0x0C;
    public const int LevelChunk = 58;
    public const int SubChunk = 174;
    public const int SetActorData = 39;
    public const int InventorySlot = 50;
    public const int RequestChunkRadius = 0x45;
    public const int NetworkSettings = 0x8F;
    public const int RequestNetworkSettings = 0xC1;

    public static string NameOf(int packetId)
    {
        return packetId switch
        {
            Login => nameof(Login),
            PlayStatus => nameof(PlayStatus),
            ServerToClientHandshake => nameof(ServerToClientHandshake),
            ClientToServerHandshake => nameof(ClientToServerHandshake),
            Disconnect => nameof(Disconnect),
            ResourcePacksInfo => nameof(ResourcePacksInfo),
            ResourcePackStack => nameof(ResourcePackStack),
            ResourcePackClientResponse => nameof(ResourcePackClientResponse),
            Text => nameof(Text),
            SetTime => nameof(SetTime),
            StartGame => nameof(StartGame),
            AddPlayer => nameof(AddPlayer),
            LevelChunk => nameof(LevelChunk),
            SubChunk => nameof(SubChunk),
            SetActorData => nameof(SetActorData),
            InventorySlot => nameof(InventorySlot),
            RequestChunkRadius => nameof(RequestChunkRadius),
            NetworkSettings => nameof(NetworkSettings),
            RequestNetworkSettings => nameof(RequestNetworkSettings),
            _ => $"Unknown(0x{packetId:X})",
        };
    }
}
