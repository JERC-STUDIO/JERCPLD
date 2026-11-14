#define LL_MEMORY_OPERATORS
#include "ll/api/memory/MemoryOperators.h"

#include "ll/api/mod/NativeMod.h"
#include "ll/api/mod/RegisterHelper.h"

#include "ll/api/memory/Hook.h"
#include "ll/api/memory/Memory.h"
#include "ll/api/memory/Symbol.h"
#include "mc/deps/core/utility/optional_ref.h"
#include "mc/network/NetEventCallback.h"
#include "mc/network/CompressedNetworkPeer.h"
#include "mc/network/EncryptedNetworkPeer.h"
#include "mc/network/NetworkConnection.h"
#include "mc/network/NetworkPeer.h"
#include "mc/network/NetworkIdentifier.h"
#include "mc/network/NetworkSystem.h"
#include "mc/deps/raknet/RakNetGUID.h"
#include "mc/deps/raknet/SystemAddress.h"
#include "mc/deps/nether_net/NetworkID.h"
#include "mc/network/ServerNetworkHandler.h"
#include "mc/network/packet/LoginPacket.h"
#include "mc/network/packet/RequestNetworkSettingsPacket.h"

#include "JercpldPtl1001.h"

// LeviLamina 26.51.5 源码包缺少 ll/api/service/TargetedBedrock.h，
// 直接声明已导出的 getServerNetworkHandler / getRakPeer。
namespace RakNet {
class RakPeer;
}
class NetworkSystem;
namespace ll::service::inline bedrock {
[[nodiscard]] optional_ref<ServerNetworkHandler> getServerNetworkHandler(bool isClientSide = false);
[[nodiscard]] optional_ref<RakNet::RakPeer>      getRakPeer();
[[nodiscard]] optional_ref<NetworkSystem>        getNetworkSystem(bool isClientSide = false);
}

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <shared_mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <nlohmann/json.hpp>

#include "mc/deps/raknet/RakPeer.h"
#include "mc/deps/raknet/SocketDescriptor.h"
#include "mc/deps/raknet/SystemAddress.h"

// ABI shims: BDS 不导出这些非纯虚基类函数/默认构造；为本地 NetworkPeer 子类提供定义。
// 只影响本 DLL 内的 vtable/构造，不改变 BDS 自身对象。
NetworkPeer::~NetworkPeer() = default;
void NetworkPeer::update() {}
void NetworkPeer::flush(std::function<void()>&&) {}
bool NetworkPeer::isLocal() const { return false; }
bool NetworkPeer::isEncrypted() const { return false; }
bool NetworkPeer::isLan() const { return false; }
NetherNet::NetworkID::NetworkID() = default;

namespace jercpld {

constexpr char const* kModName  = "JERCPLD";
constexpr char const* kVersion  = "0.0.3-test.1";

struct CaptureConfig {
    bool             enabled      = false;
    std::string      outputDir    = "diagnostics";
    int              maxFileBytes = 8 * 1024 * 1024;
    int              maxFiles     = 4;
    std::vector<int> whitelist;
};

struct RaknetSpikeConfig {
    bool enabled        = false;
    int  port           = 19234;
    int  maxConnections = 8;
};

struct Config {
    bool               translationEnabled = true;
    std::string        serverVersion      = "1.26.51";
    int                serverProtocol     = 2193;
    int                acceptMinProtocol  = 2168; // 1.26.40（PTL 已覆盖 2168-2169 段，默认值与之对齐）
    int                acceptMaxProtocol  = 2225; // 1.26.62
    CaptureConfig      capture;
    RaknetSpikeConfig  raknetSpike;
    bool               symProbeEnabled = false;
    bool               connProbeEnabled = false;
    // 翻译性能计数：默认关闭；开启后热路径只做 relaxed 原子累加，30s 汇总一次。
    bool               perfStatsEnabled = false;
};

namespace {

constexpr int kServerProtocolValue = 2193;

// BDS 1.26.51 的处理器符号（由 ServerNetworkHandler.h 的声明生成）。
constexpr char const* kLoginHandlerSymbol =
    "?$handle@ServerNetworkHandler@@QEAAXAEBVNetworkIdentifier@@V?$shared_ptr@VLoginPacket@@@std@@@Z";
constexpr char const* kRequestNetworkSettingsHandlerSymbol =
    "?$handle@ServerNetworkHandler@@QEAAXAEBVNetworkIdentifier@@AEBVRequestNetworkSettingsPacket@@@Z";
constexpr char const* kReceivePacketSymbol =
    "?receivePacket@NetworkConnection@@QEAA?AW4DataStatus@NetworkPeer@@AEAV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@AEBV?$shared_ptr@V?$time_point@Usteady_clock@chrono@std@@V?$duration@_JU?$ratio@$00$0DLJKMKAA@@std@@@23@@chrono@std@@@5@@Z";
constexpr char const* kEncryptedSendPacketSymbol =
    "?$sendPacket@EncryptedNetworkPeer@@QEAAXAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@W4Reliability@NetworkPeer@@W4Compressibility@@@Z";
constexpr char const* kCompressedSendPacketSymbol =
    "?$sendPacket@CompressedNetworkPeer@@QEAAXAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@W4Reliability@NetworkPeer@@W4Compressibility@@@Z";
constexpr char const* kBatchedSendPacketSymbol =
    "?$sendPacket@BatchedNetworkPeer@@QEAAXAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@W4Reliability@NetworkPeer@@W4Compressibility@@@Z";
constexpr char const* kOnDisconnectSymbol =
    "?$onDisconnect@ServerNetworkHandler@@QEAAXAEBVNetworkIdentifier@@W4DisconnectFailReason@Connection@@W4DisconnectionStage@4@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@3_N3@Z";

std::atomic_bool gTranslationEnabled{true};
std::atomic_bool gCaptureEnabled{false};
std::string      gCaptureDir;
int              gCaptureMaxFileBytes = 8 * 1024 * 1024;
int              gCaptureMaxFiles     = 4;
std::vector<int> gCaptureWhitelist;
// c2s_raw.log 原始转储默认关闭（跟随 capture.enabled），并设总量上限，避免长期运行写满磁盘。
std::atomic_bool      gRawDumpEnabled{false};
std::atomic<uint64_t> gRawDumpBytes{0};
constexpr uint64_t    kRawDumpMaxBytes = 16ull * 1024 * 1024;
std::mutex       gCaptureMutex;
std::unordered_map<void const*, uint32_t> gCaptureSessions;
uint32_t         gCaptureNextSession = 1;
std::shared_mutex gTranslatedPeersMutex;
// peer -> 客户端原始协议号（2225 / 2169 / 1001 ...）+ 该连接的性能统计槽位。
// 翻译必须按连接隔离，不能用“是否被翻译过”的布尔量代替版本，否则 2169 会被套用 2225 的改写。
struct TransPeerInfo {
    int         protocol   = 0;
    int         connSlot   = -1;
    // 抓包归并键：同一条连接的上/下行写进同一个 session 文件。
    // 出站钩子拿到的是 peer 指针，需要用 NetworkConnection 指针做键。
    void const* captureKey = nullptr;
};
std::unordered_map<void const*, TransPeerInfo> gTranslatedPeers;

// ---- 按连接累计的翻译耗时：连接建立/断开各输出一行 [CONN] ----
constexpr int kConnSlotCount = 64;
struct ConnStats {
    std::atomic<uint32_t> used{0};
    std::atomic<uint32_t> id{0};
    std::atomic<int>      protocol{0};
    std::atomic<uint64_t> startMs{0};
    std::atomic<uint64_t> totalNs{0};
    std::atomic<uint64_t> packets{0};
    std::atomic<uint64_t> inBytes{0};
    std::atomic<uint64_t> outBytes{0};
};
ConnStats              gConnStats[kConnSlotCount];
std::atomic<uint32_t>  gConnSeq{0};
std::unordered_map<uint64_t, int> gConnSlotByIdentifier; // 与 gTranslatedPeers 共用锁

// 144 能力开关：block_action 真正翻译上线后改成 true，[CONN] 行会自动更新。
constexpr bool kAuthInputBlockActionTranslated = true;
constexpr bool kAuthInputDeprecatedFilterOn    = true;
constexpr int  kAuthInputMinWidth              = 1;
constexpr int  kAuthInputMaxWidth              = 16;

uint64_t steadyNowMs() {
    return static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now().time_since_epoch()
        )
            .count()
    );
}

std::string ptlRouteSummary(int from, int to);
// 出站 s2c 翻译的实际落点：nethernet 下没有 BatchedNetworkPeer，
// 必须在 CompressedNetworkPeer::sendPacket（压缩前的裸包）上挂钩。
void registerCompressedPeerHook(void* compressedPeer);

std::string authInputModeSummary(int clientProtocol) {
    if (clientProtocol == 2168 || clientProtocol == 2169) {
        return fmt::format(
            "input_data=长度前缀列表(确定式),block_action={},deprecated过滤={}",
            kAuthInputBlockActionTranslated ? "已翻译" : "fallback合成",
            kAuthInputDeprecatedFilterOn ? "on" : "off"
        );
    }
    if (clientProtocol == 2225) return "input_data=2193原样(数组),block_action=原样,deprecated过滤=n/a";
    return "input_data=未知(未翻译),block_action=未知";
}
std::atomic_int  gAcceptMinProtocol{2193};
std::atomic_int  gAcceptMaxProtocol{2225};
// 本实例所运行的 BDS 协议号。由 config 的 serverProtocol 决定，是版本链的锚点：
// 2193 = 正向（老服务端 + 新客户端），2225 = 反向（新服务端 + 老客户端）。
std::atomic_int  gServerProtocol{kServerProtocolValue};
// 在线会话计数：login 包 +1，onDisconnect -1。用于 reload 的安全门禁。
std::atomic_int  gActiveSessions{0};
std::string      gConfigPath;
std::filesystem::file_time_type gConfigMtime{};
std::chrono::steady_clock::time_point gLastCaptureConfigCheck{};
void*            gOriginalLoginHandler{};
void*            gOriginalRequestNetworkSettingsHandler{};
void*            gOriginalReceivePacket{};
void*            gOriginalEncryptedSendPacket{};
void*            gOriginalCompressedSendPacket{};
void*            gOriginalBatchedSendPacket{};
void*            gOriginalOnDisconnect{};
void*            gLoginTarget{};
void*            gRequestNetworkSettingsTarget{};
void*            gReceivePacketTarget{};
void*            gEncryptedSendTarget{};
void*            gCompressedSendTarget{};
void*            gBatchedSendTarget{};
void*            gOnDisconnectTarget{};
// CompressedNetworkPeer::sendPacket 在 LeviLamina 符号表里没有条目（comp_send=0），
// 改为运行时从实例 vtable 取地址后挂钩；这是 nethernet 出站唯一的明文落点。
void*            gCompressedVtableTarget{};
void*            gOriginalCompressedVtableSend{};
bool             gHooksInstalled = false;

template <class... Args>
void modLog(fmt::format_string<Args...> fmt, Args&&... args) {
    if (auto mod = ll::mod::NativeMod::current()) {
        mod->getLogger().info(fmt, std::forward<Args>(args)...);
    }
}

std::string nowStamp() {
    using namespace std::chrono;
    auto        now = system_clock::now();
    auto        ms  = duration_cast<milliseconds>(now.time_since_epoch()) % 1000;
    std::time_t t   = system_clock::to_time_t(now);
    std::tm     tm{};
    localtime_s(&tm, &t);
    char buf[32]{};
    sprintf_s(
        buf,
        sizeof(buf),
        "%02d:%02d:%02d.%03d",
        tm.tm_hour,
        tm.tm_min,
        tm.tm_sec,
        static_cast<int>(ms.count())
    );
    return buf;
}

// 协议号到客户端版本的可读映射，仅用于日志/横幅，避免文档与代码里的版本区间漂移。
std::string protocolToVersionLabel(int protocol) {
    switch (protocol) {
    case 1001:
        return "1.26.30";
    case 2168:
        return "1.26.40-1.26.44";
    case 2169:
        return "1.26.45";
    case 2193:
        return "1.26.50-1.26.51";
    case 2225:
        return "1.26.60-1.26.62";
    default:
        return fmt::format("unknown({})", protocol);
    }
}

// 热路径不写文件；仅用于握手阶段诊断。
template <class... Args>
void hookLog(fmt::format_string<Args...> fmt, Args&&... args) {
    static std::mutex mutex;
    std::lock_guard   lock(mutex);
    std::ofstream     out("plugins/JERCPLD/hook.log", std::ios::app);
    out << nowStamp() << " " << fmt::format(fmt, std::forward<Args>(args)...) << '\n';
}

bool needsProtocolRewrite(int from) {
    return from != gServerProtocol.load(std::memory_order_relaxed)
        && from >= gAcceptMinProtocol.load(std::memory_order_relaxed)
        && from <= gAcceptMaxProtocol.load(std::memory_order_relaxed);
}

using LoginHandlerFn = void (*)(NetEventCallback*, NetworkIdentifier const&, std::shared_ptr<LoginPacket>);
using RequestNetworkSettingsHandlerFn =
    void (*)(NetEventCallback*, NetworkIdentifier const&, RequestNetworkSettingsPacket const&);

void loginHandlerDetour(NetEventCallback* self, NetworkIdentifier const& source, std::shared_ptr<LoginPacket> packet) {
    if (packet) {
        int  from       = packet->mClientNetworkVersion;
        bool hasRequest = packet->mConnectionRequest != nullptr;
        hookLog("[JERCPLD] login handler hit: protocol={} hasConnectionRequest={}", from, hasRequest);
        // 任意已登记范围内的非 2193 客户端都改写成服务端协议号。
        if (needsProtocolRewrite(from) && gTranslationEnabled.load(std::memory_order_relaxed)) {
            packet->mClientNetworkVersion = kServerProtocolValue;
            modLog("[JERCPLD] login protocol rewritten: {} -> {}", from, kServerProtocolValue);
            hookLog("[JERCPLD] login protocol rewritten: {} -> {}", from, kServerProtocolValue);
        }
    }
    auto origin = reinterpret_cast<LoginHandlerFn>(gOriginalLoginHandler);
    if (origin) {
        origin(self, source, std::move(packet));
    }
}

void requestNetworkSettingsHandlerDetour(
    NetEventCallback*                    self,
    NetworkIdentifier const&             source,
    RequestNetworkSettingsPacket const&  packet
) {
    auto& mutablePacket = const_cast<RequestNetworkSettingsPacket&>(packet);
    int   from          = mutablePacket.mClientNetworkVersion;
    hookLog("[JERCPLD] request_network_settings handler hit: protocol={}", from);
    if (needsProtocolRewrite(from) && gTranslationEnabled.load(std::memory_order_relaxed)) {
        mutablePacket.mClientNetworkVersion = kServerProtocolValue;
        modLog("[JERCPLD] request_network_settings protocol rewritten: {} -> {}", from, kServerProtocolValue);
        hookLog("[JERCPLD] request_network_settings protocol rewritten: {} -> {}", from, kServerProtocolValue);
    }
    auto origin = reinterpret_cast<RequestNetworkSettingsHandlerFn>(gOriginalRequestNetworkSettingsHandler);
    if (origin) {
        origin(self, source, packet);
    }
}

uint32_t readVarUInt(std::string const& data, size_t& offset) {
    uint32_t value = 0;
    int      shift = 0;
    while (offset < data.size() && shift <= 28) {
        uint8_t byte = static_cast<uint8_t>(data[offset++]);
        value |= static_cast<uint32_t>(byte & 0x7F) << shift;
        if ((byte & 0x80) == 0) break;
        shift += 7;
    }
    return value;
}

char const* packetName(uint32_t id) {
    switch (id) {
    case 1: return "Login";
    case 2: return "PlayStatus";
    case 5: return "Disconnect";
    case 10: return "SetTime";
    case 11: return "StartGame";
    case 12: return "AddPlayer";
    case 13: return "AddActor";
    case 39: return "SetActorData";
    case 44: return "Animate";
    case 49: return "InventoryContent";
    case 50: return "InventorySlot";
    case 58: return "FullChunkData";
    case 60: return "SetDifficulty";
    case 63: return "PlayerList";
    case 70: return "ChunkRadiusUpdated";
    case 76: return "AvailableCommands";
    case 93: return "PlayerSkin";
    case 122: return "BiomeDefinitionList";
    case 143: return "NetworkSettings";
    case 145: return "CreativeContent";
    case 148: return "ItemStackResponse";
    case 193: return "RequestNetworkSettings";
    default: return "unknown";
    }
}

std::string toHex(std::string const& data) {
    static char const* digits = "0123456789abcdef";
    std::string        out;
    out.reserve(data.size() * 2);
    for (unsigned char c : data) {
        out.push_back(digits[c >> 4]);
        out.push_back(digits[c & 0x0F]);
    }
    return out;
}

void maybeReloadCapture() {
    static std::atomic_uint counter{0};
    if ((counter.fetch_add(1, std::memory_order_relaxed) & 511U) != 0) return;
    auto now = std::chrono::steady_clock::now();
    if (now - gLastCaptureConfigCheck < std::chrono::seconds(2)) return;
    gLastCaptureConfigCheck = now;
    if (gConfigPath.empty()) return;
    std::error_code ec;
    auto            mtime = std::filesystem::last_write_time(gConfigPath, ec);
    if (ec || mtime == gConfigMtime) return;
    gConfigMtime = mtime;
    try {
        std::ifstream  in(gConfigPath);
        nlohmann::json json;
        in >> json;
        if (!json.contains("capture")) return;
        auto const& capture = json["capture"];
        std::lock_guard lock(gCaptureMutex);
        gCaptureEnabled.store(capture.value("enabled", false), std::memory_order_relaxed);
        gRawDumpEnabled.store(gCaptureEnabled.load(std::memory_order_relaxed), std::memory_order_relaxed);
        gCaptureDir          = capture.value("outputDir", std::string("diagnostics"));
        gCaptureMaxFileBytes = capture.value("maxFileBytes", 8 * 1024 * 1024);
        gCaptureMaxFiles     = capture.value("maxFiles", 4);
        gCaptureWhitelist    = capture.value("packetWhitelist", std::vector<int>{});
        hookLog(
            "[JERCPLD] capture config reloaded: enabled={} dir={} whitelist={}",
            gCaptureEnabled.load(std::memory_order_relaxed) ? "ON" : "OFF",
            gCaptureDir,
            gCaptureWhitelist.size()
        );
    } catch (std::exception const& e) {
        hookLog("[JERCPLD] capture config reload failed: {}", e.what());
    }
}

void capturePacket(char const* direction, void const* sessionKey, std::string const& data) {
    if (!gCaptureEnabled.load(std::memory_order_relaxed)) {
        maybeReloadCapture();
        if (!gCaptureEnabled.load(std::memory_order_relaxed)) return;
    }
    size_t   offset = 0;
    uint32_t id     = readVarUInt(data, offset);
    if (!gCaptureWhitelist.empty()
        && std::find(gCaptureWhitelist.begin(), gCaptureWhitelist.end(), static_cast<int>(id)) == gCaptureWhitelist.end()) {
        return;
    }
    std::lock_guard lock(gCaptureMutex);
    uint32_t        sessionId = 0;
    auto            it        = gCaptureSessions.find(sessionKey);
    if (it == gCaptureSessions.end()) {
        sessionId = gCaptureNextSession++;
        gCaptureSessions.emplace(sessionKey, sessionId);
    } else {
        sessionId = it->second;
    }
    std::error_code ec;
    std::filesystem::create_directories(gCaptureDir, ec);
    auto base = std::filesystem::path(gCaptureDir) / fmt::format("session-{}-{}.log", sessionId, direction);
    auto size = std::filesystem::file_size(base, ec);
    if (!ec && size >= static_cast<uint64_t>(gCaptureMaxFileBytes)) {
        for (int i = gCaptureMaxFiles - 1; i >= 1; --i) {
            auto from = std::filesystem::path(base.string() + "." + std::to_string(i));
            auto to   = std::filesystem::path(base.string() + "." + std::to_string(i + 1));
            std::filesystem::rename(from, to, ec);
        }
        std::filesystem::rename(base, base.string() + ".1", ec);
    }
    std::ofstream out(base, std::ios::app);
    out << nowStamp() << " " << direction << " id=" << id << " name=" << packetName(id)
        << " len=" << data.size() << '\n';
    out << "HEX " << toHex(data) << '\n';
}

// 读取 Login / RequestNetworkSettings 原始包里的客户端协议号；不是这两个包时返回 0。
int readRawProtocolVersion(std::string const& data) {
    size_t         offset   = 0;
    uint32_t       packetId = readVarUInt(data, offset);
    if (packetId != 1 && packetId != 193) return 0;
    if (offset + 4 > data.size()) return 0;
    return static_cast<int>((static_cast<uint32_t>(static_cast<uint8_t>(data[offset])) << 24)
                          | (static_cast<uint32_t>(static_cast<uint8_t>(data[offset + 1])) << 16)
                          | (static_cast<uint32_t>(static_cast<uint8_t>(data[offset + 2])) << 8)
                          | static_cast<uint32_t>(static_cast<uint8_t>(data[offset + 3])));
}

// 在反序列化之前改写原始包，避免 BDS 按客户端协议号去解析 Login 的令牌。
void rewriteRawProtocol(std::string& data) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    int      version  = readRawProtocolVersion(data);
    if (!needsProtocolRewrite(version)) return;
    int target = gServerProtocol.load(std::memory_order_relaxed);
    // 客户端把协议号写成 4 字节大端。
    data[offset]     = 0;
    data[offset + 1] = 0;
    data[offset + 2] = static_cast<char>((target >> 8) & 0xFF);
    data[offset + 3] = static_cast<char>(target & 0xFF);
    hookLog("[JERCPLD] raw protocol rewrite: packet={} {} -> {}", packetId, version, target);
}

// 2225 客户端上报 client_cache_status=true 后，2193 服务端只下发区块缓存哈希
// （sub_chunk_count=0 + blobs），而两版本的缓存键并不一致，客户端既命不中缓存
// 也不会走 SubChunkRequest，地形永远载入失败。这里强制改写为 false，让 BDS
// 直接下发完整区块数据。
void disableClientCache(std::string& data) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    if (packetId != 129) return;
    if (offset >= data.size()) return;
    if (static_cast<uint8_t>(data[offset]) == 0) return;
    data[offset] = 0;
    hookLog("[JERCPLD] c2s client_cache_status forced to false");
}

// 连接标识 key：不调用未导出的 NetworkIdentifier::getHash，直接取稳定的值字段。
uint64_t connIdentifierKey(NetworkIdentifier const& id) {
    using Type = NetworkIdentifier::Type;
    switch (id.mType) {
    case Type::NetherNet: {
        if (auto p = std::get_if<NetherNet::P2P::NetworkID>(&id.mNetherNetId)) {
            return p->mValue ^ 0x4e65744865724e65ull;
        }
        if (auto r = std::get_if<NetherNet::Realms::NetworkID>(&id.mNetherNetId)) {
            uint64_t v = 0;
            std::memcpy(&v, &r->mValue, sizeof(v));
            return v ^ 0x5265616c6d734944ull;
        }
        return 0;
    }
    case Type::RakNet:
        return id.mGuid.g ^ 0x52416b4e65744775ull;
    default: {
        uint64_t v = 0;
        std::memcpy(&v, &id.mSock, sizeof(v));
        return v ^ (static_cast<uint64_t>(id.mType) << 56);
    }
    }
}

int allocConnSlotLocked(int clientProtocol) {
    int slotIndex = -1;
    for (int i = 0; i < kConnSlotCount; ++i) {
        uint32_t expected = 0;
        if (gConnStats[i].used.compare_exchange_strong(expected, 1, std::memory_order_relaxed)) {
            slotIndex = i;
            break;
        }
    }
    if (slotIndex >= 0) {
        auto& slot = gConnStats[slotIndex];
        slot.id.store(gConnSeq.fetch_add(1, std::memory_order_relaxed) + 1, std::memory_order_relaxed);
        slot.protocol.store(clientProtocol, std::memory_order_relaxed);
        slot.startMs.store(steadyNowMs(), std::memory_order_relaxed);
        slot.totalNs.store(0, std::memory_order_relaxed);
        slot.packets.store(0, std::memory_order_relaxed);
        slot.inBytes.store(0, std::memory_order_relaxed);
        slot.outBytes.store(0, std::memory_order_relaxed);
    }
    return slotIndex;
}

void registerTranslatedPeer(NetworkConnection* connection, int clientProtocol) {
    if (!connection || !needsProtocolRewrite(clientProtocol)) return;
    // 不能以 BatchedNetworkPeer 是否存在为前提：nethernet 传输下没有 batched peer，
    // 旧逻辑在这里直接 return，导致 s2c 翻译与抓包从未生效。
    auto batched    = connection->mBatchedPeer.lock();
    auto compressed = connection->mCompressedPeer.lock();
    auto encrypted  = connection->mEncryptedPeer.lock();
    bool     created   = false;
    int      slotIndex = -1;
    uint32_t connId    = 0;
    {
        std::unique_lock lock(gTranslatedPeersMutex);
        auto             it = gTranslatedPeers.find(connection);
        if (it == gTranslatedPeers.end()) {
            slotIndex = allocConnSlotLocked(clientProtocol);
            created   = true;
            if (slotIndex >= 0) connId = gConnStats[slotIndex].id.load(std::memory_order_relaxed);
            gConnSlotByIdentifier[connIdentifierKey(connection->mId.get())] = slotIndex;
        } else {
            slotIndex = it->second.connSlot;
        }
        TransPeerInfo info{clientProtocol, slotIndex, connection};
        gTranslatedPeers[connection] = info;
        if (batched) gTranslatedPeers[batched.get()] = info;
        if (compressed) gTranslatedPeers[compressed.get()] = info;
        if (encrypted) gTranslatedPeers[encrypted.get()] = info;
    }
    if (compressed) registerCompressedPeerHook(compressed.get());
    if (created) {
        int         server = gServerProtocol.load(std::memory_order_relaxed);
        std::string line   = fmt::format(
            "[JERCPLD][CONN] id={} proto={}->{} {} jump={} perf=起始 peernet: comp={} enc={} batch={}",
            connId,
            clientProtocol,
            server,
            ptlRouteSummary(clientProtocol, server),
            authInputModeSummary(clientProtocol),
            static_cast<void*>(compressed.get()),
            static_cast<void*>(encrypted.get()),
            static_cast<void*>(batched.get())
        );
        modLog("{}", line);
        hookLog("{}", line);
    }
}

int peerProtocolVersion(void const* peer, int* connSlot = nullptr) {
    std::shared_lock lock(gTranslatedPeersMutex);
    auto             it = gTranslatedPeers.find(peer);
    if (it == gTranslatedPeers.end()) {
        if (connSlot) *connSlot = -1;
        return 0;
    }
    if (connSlot) *connSlot = it->second.connSlot;
    return it->second.protocol;
}

// 出站钩子拿到的是 peer 指针，抓包要按连接归并，所以取登记时的 NetworkConnection 指针。
void const* peerCaptureKey(void const* peer) {
    std::shared_lock lock(gTranslatedPeersMutex);
    auto             it = gTranslatedPeers.find(peer);
    if (it == gTranslatedPeers.end() || !it->second.captureKey) return peer;
    return it->second.captureKey;
}

int connectionProtocolWithSlot(NetworkConnection* connection, int* connSlot) {
    if (connSlot) *connSlot = -1;
    if (!connection) return 0;
    std::shared_lock lock(gTranslatedPeersMutex);
    auto             it = gTranslatedPeers.find(connection);
    if (it != gTranslatedPeers.end()) {
        if (connSlot) *connSlot = it->second.connSlot;
        return it->second.protocol;
    }
    return 0;
}

int connectionProtocol(NetworkConnection* connection) {
    return connectionProtocolWithSlot(connection, nullptr);
}

std::atomic_uint gRawC2sSession{0};

bool rawDumpBudget(size_t bytes) {
    if (!gRawDumpEnabled.load(std::memory_order_relaxed)) return false;
    uint64_t prev = gRawDumpBytes.fetch_add(bytes, std::memory_order_relaxed);
    if (prev + bytes > kRawDumpMaxBytes) {
        static std::atomic_bool logged{false};
        if (!logged.exchange(true, std::memory_order_relaxed)) {
            hookLog("[JERCPLD] c2s_raw 达到 {}B 上限，停止继续转储", kRawDumpMaxBytes);
        }
        return false;
    }
    return true;
}

// 翻译前原始 c2s 采样：只记录容器/物品链路，单独文件、单包限长 4096B。
void dumpRawC2sPacket(std::string const& data, int version, bool isLogin) {
    if (isLogin) gRawC2sSession.fetch_add(1, std::memory_order_relaxed);
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    bool     wanted   = packetId == 30 || packetId == 33 || packetId == 36 || packetId == 46
                    || packetId == 47 || packetId == 48 || packetId == 49 || packetId == 50
                    || packetId == 51 || packetId == 52 || packetId == 144 || packetId == 145
                    || packetId == 147 || packetId == 148;
    if (!wanted) return;
    if (!rawDumpBudget(4096)) return;
    static std::mutex                            mutex;
    static std::unordered_map<uint32_t, uint32_t> authCounts;
    static std::unordered_map<uint64_t, uint32_t> otherCounts;
    std::lock_guard                              lock(mutex);
    uint32_t session = gRawC2sSession.load(std::memory_order_relaxed);
    if (packetId == 144) {
        uint32_t& count = authCounts[session];
        if (count >= 64) return; // 每会话最多 64 条 144，避免日志膨胀
        ++count;
    } else if (packetId == 33 || packetId == 36) {
        uint64_t  key   = (static_cast<uint64_t>(packetId) << 32) | session;
        uint32_t& count = otherCounts[key];
        if (count >= 128) return; // 33/36 每会话各最多 128 条
        ++count;
    }
    std::ofstream out("plugins/JERCPLD/c2s_raw.log", std::ios::app);
    out << nowStamp() << " session=" << session << " proto=" << version << " id=" << packetId
        << " len=" << data.size();
    if (packetId == 144) {
        // 记录候选宽度下的 tick，便于用“同 tick 连续帧”对齐。
        auto readVar = [](std::string const& d, size_t& p, uint64_t& v) -> bool {
            v = 0;
            int shift = 0;
            while (p < d.size() && shift <= 63) {
                uint8_t byte = static_cast<uint8_t>(d[p++]);
                v |= static_cast<uint64_t>(byte & 0x7f) << shift;
                if ((byte & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        };
        std::string tickInfo;
        for (int w = 1; w <= 10; ++w) {
            size_t p = offset + 32 + w;
            uint64_t inputMode = 0, playMode = 0, rawInteraction = 0;
            if (!readVar(data, p, inputMode) || inputMode > 4) continue;
            if (!readVar(data, p, playMode) || playMode > 9) continue;
            if (!readVar(data, p, rawInteraction)) continue;
            int32_t interaction = static_cast<int32_t>(
                (rawInteraction >> 1) ^ static_cast<uint32_t>(-static_cast<int32_t>(rawInteraction & 1))
            );
            if (interaction < 0 || interaction > 2) continue;
            if (p + 8 > data.size()) continue;
            p += 8;
            uint64_t tick = 0;
            if (!readVar(data, p, tick)) continue;
            if (p + 12 > data.size()) continue;
            p += 12;
            bool ok = true;
            for (int i = 0; i < 5; ++i) {
                if (p >= data.size()) { ok = false; break; }
                uint8_t has = static_cast<uint8_t>(data[p++]);
                if (has == 1) {
                    if (p >= data.size() || static_cast<uint8_t>(data[p++]) != 0) { ok = false; break; }
                } else if (has != 0) { ok = false; break; }
            }
            if (!ok || p + 28 != data.size()) continue;
            tickInfo += fmt::format(" w{}:{}", w, tick);
        }
        if (!tickInfo.empty()) out << " ticks=" << tickInfo;
    }
    out << " hex=";
    size_t limit = data.size() < 4096 ? data.size() : 4096;
    for (size_t i = 0; i < limit; ++i) {
        char buf[3]{};
        sprintf_s(buf, sizeof(buf), "%02x", static_cast<unsigned char>(data[i]));
        out << buf;
    }
    if (limit < data.size()) out << "...truncated";
    out << '\n';
}

using ReceivePacketFn = NetworkPeer::DataStatus (*)(
    NetworkConnection*,
    std::string&,
    std::shared_ptr<std::chrono::steady_clock::time_point> const&
);

void trimAnimateHand(std::string& data);
bool fixResourcePackClientResponseForOldClients(std::string& data, size_t payloadOffset);
void dispatchInboundTranslation(std::string& data, int clientProtocol, int connSlot);
extern thread_local bool gPtlDrop;

NetworkPeer::DataStatus receivePacketDetour(
    NetworkConnection*                                             self,
    std::string&                                                   buffer,
    std::shared_ptr<std::chrono::steady_clock::time_point> const& timepointPtr
) {
    auto origin = reinterpret_cast<ReceivePacketFn>(gOriginalReceivePacket);
    auto status = origin ? origin(self, buffer, timepointPtr) : NetworkPeer::DataStatus::NoData;
    if (status == NetworkPeer::DataStatus::HasData) {
        int rawVersion = readRawProtocolVersion(buffer);
        bool wasLogin  = false;
        {
            size_t   idOffset = 0;
            uint32_t loginId  = readVarUInt(buffer, idOffset);
            wasLogin          = (loginId == 1);
            if (wasLogin) gActiveSessions.fetch_add(1, std::memory_order_relaxed);
        }
        if (gTranslationEnabled.load(std::memory_order_relaxed)) {
            rewriteRawProtocol(buffer);
            int connSlot = -1;
            int version  = rawVersion != 0 ? rawVersion : connectionProtocolWithSlot(self, &connSlot);
            if (rawVersion != 0) connectionProtocolWithSlot(self, &connSlot);
            dumpRawC2sPacket(buffer, version, wasLogin);
            int serverProto = gServerProtocol.load(std::memory_order_relaxed);
            if (serverProto == kServerProtocolValue) {
            // peer<2168 (协议号 < 2168)：只有 ResourcePackClientResponse 的载荷结构变了，包号不变。
            // 1001: response_status=u8, 无 response_status_name, resourcepackids 总是发送(li16 count)
            // 2193: response_status=varint, 有 response_status_name(string), resourcepackids conditional
            // 注意：pktId=4 是无载荷的 client_to_server_handshake，各版本一致，严禁改写。
            if (version != 0 && version < 2168) {
                size_t   pktOff = 0;
                uint32_t pktId  = readVarUInt(buffer, pktOff);
                if (pktId == 8) {
                    std::string original = buffer;
                    if (fixResourcePackClientResponseForOldClients(buffer, pktOff)) {
                        hookLog(
                            "[JERCPLD] c2s peer={} ResourcePackClientResponse status u8->varint: {} -> {}",
                            version, original.size(), buffer.size()
                        );
                    } else {
                        buffer = std::move(original);
                        hookLog(
                            "[JERCPLD] c2s peer={} ResourcePackClientResponse fix failed: len={}",
                            version, buffer.size()
                        );
                    }
                }
            }
            // 2225 专属的 c2s 改写不能套用到 2169/1001，否则会破坏它们本就与
            // 2193 一致的包结构。
            if (version == 2225) {
                disableClientCache(buffer);
                trimAnimateHand(buffer);
            }
            // 2169/2168/1001 的 c2s 走 PTL 链（2225 已有专属处理，避免重复）。
            if (version != 0 && version != kServerProtocolValue && version != 2225) {
                dispatchInboundTranslation(buffer, version, connSlot);
            }
            } else {
                // 反向链路（2225 服务端 + 2193 客户端）：c2s 镜像补字段。
                dispatchInboundTranslation(buffer, version, connSlot);
            }
            if (rawVersion != 0 && rawVersion != kServerProtocolValue) {
                registerTranslatedPeer(self, rawVersion);
                hookLog("[JERCPLD] translated peer registered: protocol={}", rawVersion);
            }
            if (gPtlDrop) {
                // 会话计数只由 onDisconnect 回收，这里不能再减，否则会双降导致 reload 门禁失效。
                hookLog("[JERCPLD][PTL][c2s] packet dropped");
                return NetworkPeer::DataStatus::NoData;
            }
        }
        capturePacket("c2s", self, buffer);
    }
    return status;
}

using EncryptedSendPacketFn = void (*)(
    EncryptedNetworkPeer*,
    std::string const&,
    NetworkPeer::Reliability,
    Compressibility
);

// AddPlayer 的 passenger_of_block 插在 device_id 之前，不是包尾。
// 通过定位 device_id 字符串（尾部可打印 ASCII + 可选 device_os）来插入 0x00。
bool insertAddPlayerPassengerField(std::string& data) {
    size_t const end  = data.size();
    size_t       best = SIZE_MAX;
    size_t       begin = end > 128 ? end - 128 : 0;
    for (size_t o = begin; o < end; ++o) {
        size_t   p   = o;
        uint32_t len = readVarUInt(data, p);
        if (len < 16 || len > 64 || p + len > end) continue;
        bool printable = true;
        for (size_t i = 0; i < len; ++i) {
            unsigned char c = static_cast<unsigned char>(data[p + i]);
            if (c < 0x20 || c > 0x7E) {
                printable = false;
                break;
            }
        }
        if (!printable) continue;
        size_t rest = end - (p + len);
        if (rest > 8) continue;
        if (rest > 0) {
            size_t   q  = p + len;
            uint32_t os = readVarUInt(data, q);
            (void)os;
            if (q != end && rest <= 2) continue;
        }
        if (best == SIZE_MAX || o > best) best = o;
    }
    if (best == SIZE_MAX) return false;
    data.insert(best, 1, '\0');
    return true;
}

// StartGame 的 editor_level_migration_version(u8) 位于第 18 个字段，
// 需要跳过前 17 个字段后在中间插入，不能追加到包尾。
struct PacketReader {
    std::string const& data;
    size_t             pos;

    bool skip(size_t n) {
        if (pos + n > data.size()) return false;
        pos += n;
        return true;
    }
    bool var() {
        int shift = 0;
        while (pos < data.size() && shift <= 63) {
            uint8_t b = static_cast<uint8_t>(data[pos++]);
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
    bool str() {
        size_t   p   = pos;
        uint32_t len = readVarUInt(data, p);
        if (len > data.size() - p) return false;
        pos = p + len;
        return true;
    }
};

bool insertStartGameEditorField(std::string& data, size_t payloadOffset) {
    PacketReader r{data, payloadOffset};
    if (!r.var()) return false;  // entity_id
    if (!r.var()) return false;  // runtime_entity_id
    if (!r.var()) return false;  // player_gamemode
    if (!r.skip(12)) return false; // player_position
    if (!r.skip(8)) return false;  // rotation
    if (!r.skip(8)) return false;  // seed
    if (!r.skip(2)) return false;  // biome_type
    if (!r.str()) return false;    // biome_name
    if (!r.var()) return false;    // dimension
    if (!r.var()) return false;    // generator
    if (!r.var()) return false;    // world_gamemode
    if (!r.skip(1)) return false;  // hardcore
    if (!r.var()) return false;    // difficulty
    if (!r.var()) return false;    // spawn_position x
    if (!r.var()) return false;    // spawn_position y
    if (!r.var()) return false;    // spawn_position z
    if (!r.skip(1)) return false;  // achievements_disabled
    if (!r.var()) return false;    // editor_world_type
    if (!r.skip(2)) return false;  // created_in_editor + exported_from_editor
    data.insert(r.pos, 1, '\0');
    return true;
}

// 2225 PlayerList：
//   - 记录级在 xbox_user_id 之后新增 play_fab_id(string)
//   - Skin 内部删除了原有的 play_fab_id(string)
// 两者必须同时迁移，否则 2225 客户端会把 skin 字节读成 build_platform 枚举。
struct PacketCursor {
    std::string const& data;
    size_t             pos;
    bool               ok = true;

    PacketCursor(std::string const& d, size_t p) : data(d), pos(p) {}

    uint64_t var() {
        uint64_t value = 0;
        int      shift = 0;
        while (pos < data.size() && shift <= 63) {
            uint8_t byte = static_cast<uint8_t>(data[pos++]);
            value |= static_cast<uint64_t>(byte & 0x7F) << shift;
            if ((byte & 0x80) == 0) return value;
            shift += 7;
        }
        ok = false;
        return 0;
    }
    void skip(size_t n) {
        if (pos + n > data.size()) {
            pos = data.size();
            ok  = false;
        } else {
            pos += n;
        }
    }
    bool str(size_t& begin, size_t& end) {
        begin       = pos;
        uint64_t len = var();
        if (!ok) return false;
        skip(static_cast<size_t>(len));
        end = pos;
        return ok;
    }
    bool skinImage() {
        skip(8); // width + height
        uint64_t len = var();
        if (!ok) return false;
        skip(static_cast<size_t>(len));
        return ok;
    }
    uint8_t u8() {
        if (pos >= data.size()) {
            ok = false;
            return 0;
        }
        return static_cast<uint8_t>(data[pos++]);
    }
    // 固定 4 字节小端 int32（li32）。Experiments 数组 countType = li32（bedrock-protocol
    // Experiments = array{countType:li32}），不是 varint —— 用 varint 读会偏 3 字节。
    uint32_t li32() {
        if (pos + 4 > data.size()) {
            ok = false;
            return 0;
        }
        uint32_t u = static_cast<uint32_t>(static_cast<uint8_t>(data[pos]))
                  | (static_cast<uint32_t>(static_cast<uint8_t>(data[pos + 1])) << 8)
                  | (static_cast<uint32_t>(static_cast<uint8_t>(data[pos + 2])) << 16)
                  | (static_cast<uint32_t>(static_cast<uint8_t>(data[pos + 3])) << 24);
        pos += 4;
        return u;
    }
};

// 网络 NBT（小端）跳过。只用于定位 StartGame 尾部的 checksum。
bool skipNbtPayload(PacketCursor& c, uint8_t type);

bool skipNetworkNbt(PacketCursor& c) {
    uint8_t type = c.u8();
    if (!c.ok) return false;
    if (type == 0) return true;
    size_t begin = 0, end = 0;
    if (!c.str(begin, end)) return false; // 根名称
    return skipNbtPayload(c, type);
}

// 网络版 NBT（littleVarint，prismarine-nbt 语义，bedrock-protocol 经它编译）：
//  - TAG_String 长度 = varint（普通）
//  - TAG_List / ByteArray / IntArray / LongArray 的计数 = zigzag32 varint
//    （注意 0x02 解码后是 1 —— 按普通 varint 读会多数一倍）
//  - TAG_Int 值 = zigzag32 varint（变长）；TAG_Long 值 = zigzag64 varint（变长）
//  - TAG_Short/Float/Double/Byte 定长小端
static bool readZigzagCount(PacketCursor& c, uint64_t& out) {
    uint64_t n = c.var();
    if (!c.ok) return false;
    int64_t v = static_cast<int64_t>(n >> 1) ^ -(static_cast<int64_t>(n & 1));
    if (v < 0 || v > 1000000) return false; // 负值/超界视为畸形
    out = static_cast<uint64_t>(v);
    return true;
}

bool skipNbtPayload(PacketCursor& c, uint8_t type) {
    switch (type) {
    case 0: return true;
    case 1: c.skip(1); return c.ok;
    case 2: c.skip(2); return c.ok;
    case 3:
    case 4: { // TAG_Int / TAG_Long：网络 NBT 中为 zigzag varint（变长）
        (void)c.var();
        return c.ok;
    }
    case 5: c.skip(4); return c.ok;
    case 6: c.skip(8); return c.ok;
    case 7: { // TAG_Byte_Array：zigzag 计数 + N 字节
        uint64_t n = 0;
        if (!readZigzagCount(c, n)) return false;
        c.skip(static_cast<size_t>(n));
        return c.ok;
    }
    case 8: { // TAG_String：varint 长度 + N 字节
        uint64_t len = c.var();
        if (!c.ok) return false;
        c.skip(static_cast<size_t>(len));
        return c.ok;
    }
    case 9: { // TAG_List：元素类型 + zigzag 计数
        uint8_t elementType = c.u8();
        if (!c.ok) return false;
        uint64_t count = 0;
        if (!readZigzagCount(c, count)) return false;
        if (elementType == 0) return count == 0;
        for (uint64_t i = 0; i < count; ++i) {
            if (!skipNbtPayload(c, elementType)) return false;
        }
        return true;
    }
    case 10: {
        for (;;) {
            uint8_t childType = c.u8();
            if (!c.ok) return false;
            if (childType == 0) return true;
            size_t begin = 0, end = 0;
            if (!c.str(begin, end)) return false;
            if (!skipNbtPayload(c, childType)) return false;
        }
    }
    case 11: { // TAG_Int_Array：zigzag 计数 + N × zigzag32
        uint64_t n = 0;
        if (!readZigzagCount(c, n)) return false;
        for (uint64_t i = 0; i < n; ++i) {
            (void)c.var();
            if (!c.ok) return false;
        }
        return true;
    }
    case 12: { // TAG_Long_Array：zigzag 计数 + N × zigzag64
        uint64_t n = 0;
        if (!readZigzagCount(c, n)) return false;
        for (uint64_t i = 0; i < n; ++i) {
            (void)c.var();
            if (!c.ok) return false;
        }
        return true;
    }
    default: return false;
    }
}

// 2225 客户端会用自身方块表重算 block_pallette_checksum；2193 服务端算出的
// 值必然不同（且本服有 1295 条自定义方块），客户端会以 -63 block_mismatch
// 拒绝进入。PocketMine 文档明确：该字段填 0 即可让客户端跳过调色板校验。
bool zeroBlockPaletteChecksum(std::string& data) {
    size_t marker = data.rfind("<nethernet>");
    if (marker == std::string::npos) marker = data.rfind("<raknet>");
    if (marker == std::string::npos || marker == 0) return false;

    // correlation id 长度前缀（LeviLamina 生成的值 < 128 字节）
    uint8_t lengthByte = static_cast<uint8_t>(data[marker - 1]);
    if ((lengthByte & 0x80) != 0) return false;
    size_t strEnd = marker + lengthByte;
    if (strEnd > data.size()) return false;

    PacketCursor cursor{data, strEnd};
    cursor.skip(1); // server_authoritative_inventory
    size_t begin = 0, end = 0;
    if (!cursor.str(begin, end)) return false; // engine
    if (!skipNetworkNbt(cursor)) return false;  // property_data
    if (cursor.pos + 8 > data.size()) return false;
    for (size_t i = 0; i < 8; ++i) data[cursor.pos + i] = '\0';
    return true;
}

// 2225 的 animate 在末尾多一个 hand(u8)。2193 服务端读到多余字节会判定
// malformed（violation packetId 44）并直接以 kicked_for_exploit 踢人。
void trimAnimateHand(std::string& data) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    if (packetId != 44) return;

    PacketCursor cursor{data, offset};
    size_t       actionPos = cursor.pos;
    (void)actionPos;
    cursor.u8(); // action_id（2225/2193 枚举实测一致，不做映射）
    if (!cursor.ok) return;

    cursor.var();  // runtime_entity_id
    cursor.skip(4); // data (lf32)
    uint8_t hasSwingSource = cursor.u8();
    if (cursor.ok && hasSwingSource == 1) {
        size_t begin = 0, end = 0;
        cursor.str(begin, end);
    }
    if (!cursor.ok) return;
    if (cursor.pos + 1 == data.size()) {
        data.pop_back();
        hookLog("[JERCPLD] c2s animate hand trimmed: newLen={}", data.size());
    }
}

// 说明：官方 changelog 对 GameType / Animate Action / Interact Action 等枚举列出了
// “from → to”的重编号，但 BDS 1.26.51 实测与 minecraft-data 1.26.51 均表明这些
// 枚举在 2193 已经使用新值（例：interact action 4 在 2193 被接受，改成 2 反而
// 触发 invalid enum 踢人）。因此这些枚举不做映射，只保留 animate 的 hand 字段处理。

bool translatePlayerList(std::string& data, size_t offset) {
    std::string out;
    out.reserve(data.size() + 64);
    out.append(data, 0, offset);

    PacketCursor cursor{data, offset};
    uint64_t     count = cursor.var();
    if (!cursor.ok || count > 1024) return false;
    out.append(data, offset, cursor.pos - offset);

    for (uint64_t i = 0; i < count; ++i) {
        size_t   recordStart = cursor.pos;
        uint64_t type        = cursor.var();
        if (!cursor.ok) return false;
        cursor.skip(1); // legacy_type
        if (!cursor.ok) return false;

        if (type == 0) { // remove: 仅 uuid
            cursor.skip(16);
            if (!cursor.ok) return false;
            out.append(data, recordStart, cursor.pos - recordStart);
            continue;
        }
        if (type != 1) return false;

        cursor.skip(16); // uuid
        cursor.var();    // entity_unique_id (zigzag64)
        if (!cursor.ok) return false;

        size_t begin = 0, end = 0;
        if (!cursor.str(begin, end)) return false; // username
        if (!cursor.str(begin, end)) return false; // xbox_user_id
        size_t xboxEnd = cursor.pos;

        if (!cursor.str(begin, end)) return false; // platform_chat_id
        cursor.skip(4); // build_platform
        if (!cursor.ok) return false;
        size_t skinStart = cursor.pos;

        if (!cursor.str(begin, end)) return false; // skin_id
        size_t skinIdEnd = cursor.pos;
        size_t fabBegin = 0, fabEnd = 0;
        if (!cursor.str(fabBegin, fabEnd)) return false; // play_fab_id（2193 在 Skin 内）
        size_t fabEndPos = cursor.pos;

        if (!cursor.str(begin, end)) return false; // skin_resource_pack
        if (!cursor.skinImage()) return false;     // skin_data
        uint64_t animationCount = cursor.var();
        if (!cursor.ok || animationCount > 1024) return false;
        for (uint64_t a = 0; a < animationCount; ++a) {
            if (!cursor.skinImage()) return false;
            cursor.var();   // animation_type
            cursor.skip(4); // animation_frames
            cursor.var();   // expression_type
            if (!cursor.ok) return false;
        }
        if (!cursor.skinImage()) return false; // cape_data
        if (!cursor.str(begin, end)) return false; // geometry_data
        if (!cursor.str(begin, end)) return false; // geometry_data_version
        if (!cursor.str(begin, end)) return false; // animation_data
        if (!cursor.str(begin, end)) return false; // cape_id
        if (!cursor.str(begin, end)) return false; // full_skin_id
        cursor.skip(1); // arm_size
        cursor.skip(4); // skin_color
        uint64_t pieceCount = cursor.var();
        if (!cursor.ok || pieceCount > 4096) return false;
        for (uint64_t p = 0; p < pieceCount; ++p) {
            if (!cursor.str(begin, end)) return false; // piece_id
            cursor.skip(4);  // piece_type
            cursor.skip(16); // pack_id
            cursor.skip(1);  // is_default_piece
            if (!cursor.str(begin, end)) return false; // product_id
        }
        uint64_t tintCount = cursor.var();
        if (!cursor.ok || tintCount > 4096) return false;
        for (uint64_t t = 0; t < tintCount; ++t) {
            if (!cursor.str(begin, end)) return false; // piece_type
            cursor.skip(16); // colors[4]
        }
        cursor.skip(5); // premium + persona + cape_on_classic + primary_user + overriding_player_appearance
        if (!cursor.str(begin, end)) return false; // trusted
        if (!cursor.str(begin, end)) return false; // profile_hash
        size_t skinEnd = cursor.pos;

        cursor.skip(7); // is_teacher + is_host + is_subclient + player_color
        if (!cursor.ok) return false;
        size_t recordEnd = cursor.pos;

        out.append(data, recordStart, xboxEnd - recordStart);
        out.append(data, fabBegin, fabEnd - fabBegin);          // 记录级 play_fab_id
        out.append(data, xboxEnd, skinStart - xboxEnd);         // platform_chat_id + build_platform
        out.append(data, skinStart, skinIdEnd - skinStart);     // skin_id
        out.append(data, fabEndPos, skinEnd - fabEndPos);       // 去掉 play_fab_id 的 Skin
        out.append(data, skinEnd, recordEnd - skinEnd);         // 尾部 7 字节
    }
    if (cursor.pos != data.size()) out.append(data, cursor.pos, data.size() - cursor.pos);
    data = std::move(out);
    return true;
}

// 2193 的 LevelChunk 常带着 sub_chunk_count=0 + 客户端缓存 blob，表示“客户端应自行
// 从缓存取子区块”。2225 客户端不再走这条老缓存路径，收到后会把它当成空区块，
// 既不请求子区块也不显示地形。这里按 Mojang 文档改成 skeleton 标记
// (0xFFFFFFFF)，客户端随后会发 SubChunkRequest，由 BDS 返回真实地形。
bool promoteLevelChunkToSkeleton(std::string& data, size_t payloadOffset) {
    size_t o = payloadOffset;
    readVarUInt(data, o); // x
    if (o > data.size()) return false;
    readVarUInt(data, o); // z
    if (o > data.size()) return false;
    readVarUInt(data, o); // dimension
    if (o > data.size()) return false;

    size_t   countBegin    = o;
    uint32_t subChunkCount = readVarUInt(data, o);
    if (o > data.size()) return false;
    if (subChunkCount != 0) return false;

    size_t optionBegin = o;
    if (o >= data.size() || static_cast<uint8_t>(data[o]) == 0) return false;
    ++o; // optional flag = 1
    readVarUInt(data, o); // request subchunk limit
    size_t optionEnd = o;
    if (o >= data.size()) return false;

    if (static_cast<uint8_t>(data[o]) != 1) return false; // cache_enabled
    ++o;
    if (o >= data.size()) return false;
    uint32_t blobCount = readVarUInt(data, o);
    if (o > data.size() || blobCount > 64) return false;
    if (o + static_cast<size_t>(blobCount) * 8 > data.size()) return false;
    o += static_cast<size_t>(blobCount) * 8;
    size_t payloadBegin = o;

    std::string out;
    out.reserve(data.size() + 8);
    out.append(data, 0, countBegin);
    out.append("\xFF\xFF\xFF\xFF\x0F", 5); // sub_chunk_count = 0xFFFFFFFF
    out.append(data, optionBegin, optionEnd - optionBegin);
    out.push_back('\0'); // cache_enabled = false
    out.push_back('\0'); // blobs count = 0
    out.append(data, payloadBegin, data.size() - payloadBegin);
    out.push_back('\0'); // client_biome_update = false
    data = std::move(out);
    return true;
}

// option<T> 字段：1 字节存在标志，存在时跳过固定宽度 n。
bool skipOptionField(PacketCursor& cursor, size_t n) {
    uint8_t flag = cursor.u8();
    if (!cursor.ok) return false;
    if (flag == 1) cursor.skip(n);
    return cursor.ok;
}

// 2193 的 CameraPresets 在每条记录末尾新增：
//   apply_inherited_starting_rotation(bool) + starting_rotation(option vec2f)
// 2169/2168/1001 客户端没有这两个字段，直接收到 2193 布局会在解析 name/parent
// 字符串时错位（自检报 Missing characters in string, 339 vs 393）。逐条裁掉。
bool stripCameraPresetTrailingFields(std::string& data, size_t payloadOffset) {
    PacketCursor cursor{data, payloadOffset};
    uint64_t     count = cursor.var();
    if (!cursor.ok || count > 4096) return false;

    std::string out;
    out.reserve(data.size());
    out.append(data, 0, cursor.pos); // packet id + presets count

    for (uint64_t i = 0; i < count; ++i) {
        size_t recordBegin = cursor.pos;
        size_t begin = 0, end = 0;
        if (!cursor.str(begin, end)) return false; // name
        if (!cursor.str(begin, end)) return false; // parent
        for (int k = 0; k < 3; ++k) {              // position(Vec3fopts)
            if (!skipOptionField(cursor, 4)) return false;
        }
        for (int k = 0; k < 2; ++k) {              // rotation(Vec2fopts)
            if (!skipOptionField(cursor, 4)) return false;
        }
        if (!skipOptionField(cursor, 4)) return false;  // rotation_speed
        if (!skipOptionField(cursor, 1)) return false;  // snap_to_target
        if (!skipOptionField(cursor, 8)) return false;  // horizontal_rotation_limit
        if (!skipOptionField(cursor, 8)) return false;  // vertical_rotation_limit
        if (!skipOptionField(cursor, 1)) return false;  // continue_targeting
        if (!skipOptionField(cursor, 4)) return false;  // tracking_radius
        if (!skipOptionField(cursor, 8)) return false;  // offset
        if (!skipOptionField(cursor, 12)) return false; // entity_offset
        if (!skipOptionField(cursor, 4)) return false;  // radius
        if (!skipOptionField(cursor, 4)) return false;  // yaw_limit_min
        if (!skipOptionField(cursor, 4)) return false;  // yaw_limit_max
        if (!skipOptionField(cursor, 1)) return false;  // audio_listener
        if (!skipOptionField(cursor, 1)) return false;  // player_effects
        uint8_t hasAimAssist = cursor.u8();             // aim_assist option<container>
        if (!cursor.ok) return false;
        if (hasAimAssist == 1) {
            uint8_t hasPresetId = cursor.u8();          // preset_id option<string>
            if (!cursor.ok) return false;
            if (hasPresetId == 1 && !cursor.str(begin, end)) return false;
            if (!skipOptionField(cursor, 4)) return false; // target_mode
            if (!skipOptionField(cursor, 8)) return false; // angle
            if (!skipOptionField(cursor, 4)) return false; // distance
        }
        if (!skipOptionField(cursor, 1)) return false;  // control_scheme
        size_t legacyEnd = cursor.pos;
        cursor.skip(1);                                 // apply_inherited_starting_rotation
        if (!skipOptionField(cursor, 8)) return false;  // starting_rotation
        if (!cursor.ok) return false;
        out.append(data, recordBegin, legacyEnd - recordBegin);
    }
    if (cursor.pos < data.size()) out.append(data, cursor.pos, data.size() - cursor.pos);
    data = std::move(out);
    return true;
}

// 2193 的 TexturePackInfos list count 用 varint 编码，1001(1.26.30) 用 li16。
// 两者的条目结构完全一致，只需要把 count 重编码。countType 差异发生在 1001→2168 之间。
// 2168 及以上已经是 varint 了，所以只对 peer < 2168 生效。
bool fixResourcePacksInfoCountType(std::string& data, size_t payloadOffset) {
    // ResourcePacksInfo 头部：
    //   must_accept(bool=1) + has_addons(bool=1) + has_scripts(bool=1) + disable_vibrant_visuals(bool=1)
    //   world_template: uuid(16) + version(string)
    //   texture_packs: TexturePackInfos (countType 在 1001=li16, 2193=varint)
    PacketCursor cursor{data, payloadOffset};
    cursor.skip(4);  // 4 x bool
    cursor.skip(16); // world_template.uuid
    size_t begin = 0, end = 0;
    cursor.str(begin, end); // world_template.version
    if (!cursor.ok) return false;

    size_t   varintBegin = cursor.pos;
    uint64_t countValue  = cursor.var();
    if (!cursor.ok) return false;
    size_t   varintEnd   = cursor.pos;
    size_t   varintLen   = varintEnd - varintBegin;

    if (countValue > 0x7FFF) return false; // li16 最大 32767，实际 texture pack 数量不会超过

    // 构造新 payload：原包开头 + li16(count) + TexturePackInfos 条目 + 原包尾部
    std::string out;
    out.reserve(data.size() + 1);
    out.append(data, 0, varintBegin);
    uint16_t li16 = static_cast<uint16_t>(countValue);
    out.push_back(static_cast<char>(li16 & 0xFF));
    out.push_back(static_cast<char>((li16 >> 8) & 0xFF));
    out.append(data, varintEnd, data.size() - varintEnd);
    data = std::move(out);
    return true;
}

// 1001 → 2193：ResourcePackClientResponse (packet 8) 的结构在 1001→2168 之间变了：
// 1. response_status 从 u8 改成 varint，且枚举值变了（1001 的 "none"=0 在 2193 里不存在）
// 2. 新增 response_status_name(string) 字段
// 3. resourcepackids 变成 conditional（仅 send_packs 时发送）
bool fixResourcePackClientResponseForOldClients(std::string& data, size_t payloadOffset) {
    PacketCursor cursor{data, payloadOffset};
    uint8_t oldStatusU8 = 0;
    if (cursor.pos >= data.size()) return false;
    oldStatusU8 = static_cast<uint8_t>(data[cursor.pos++]);
    if (!cursor.ok) return false;

    // 枚举重映射：1001 → 2193
    // 1001: 0=none 1=refused 2=send_packs 3=have_all_packs 4=completed
    // 2193: 0=refused 1=send_packs 2=have_all_packs 3=completed
    uint64_t newStatusVar = oldStatusU8;
    if (oldStatusU8 == 0) newStatusVar = 2; // none → have_all_packs
    else if (oldStatusU8 == 1) newStatusVar = 0; // refused → refused
    else if (oldStatusU8 == 2) newStatusVar = 1; // send_packs → send_packs
    else if (oldStatusU8 == 3) newStatusVar = 2; // have_all_packs → have_all_packs
    else if (oldStatusU8 == 4) newStatusVar = 3; // completed → completed

    bool keepIds = (newStatusVar == 1); // 只有 send_packs 时保留 resourcepackids

    // 构造新 payload
    std::string out;
    out.reserve(data.size() + 8);
    // 1. 保留 packet_id(varint)：从 data 开头到 payloadOffset
    out.append(data, 0, payloadOffset);
    // 2. 写入新的 response_status(varint)
    uint64_t v = newStatusVar;
    while (v >= 0x80) {
        out.push_back(static_cast<char>((v & 0x7F) | 0x80));
        v >>= 7;
    }
    out.push_back(static_cast<char>(v));
    // 3. 插入 response_status_name：必须与 2168+ 客户端实际发送的值一致，
    //    BDS 会按该值处理资源包阶段（空串会被判为异常包直接踢人）。
    static constexpr char kStatusName[] = "resourcepackstackfinished";
    out.push_back(static_cast<char>(sizeof(kStatusName) - 1));
    out.append(kStatusName, sizeof(kStatusName) - 1);
    // 4. 如果需要 pack IDs，追加原剩余部分；否则跳过
    if (keepIds) {
        out.append(data, cursor.pos, data.size() - cursor.pos);
    }
    data = std::move(out);
    return true;
}

// 前向解析 2193 布局的 StartGame 的 LevelSettings 到达 gamerules (Rule Data) 位置。
// 成功时 cursor.pos == Rule Data 起点（Rules List 的 array 长度 varint）。
// 同时记录各关键字段位置用于诊断。
struct LevelSettingsDiagnostic {
    bool ok = false;
    size_t posAfterExportedEditor = 0; // editor 字段 (field 10) 之后
    size_t posAfterDayCycleStop    = 0; // field 11 之后
    size_t posEduOffer             = 0; // field 12 Education Edition Offer 起点
    size_t posAfterEduOffer        = 0; // field 12 之后
    size_t posAfterCommandsEnabled = 0; // field 22 之后
    size_t posAfterTexturePacksReq = 0; // field 23 之后（gamerules 前一个字段结束）
    size_t posGamerules            = 0; // field 24 Rule Data / Rule List 起点
    size_t posGamerulesContent     = 0; // Rules List 的 array 长度 varint 读完之后
    size_t posAfterGamerules       = 0; // gamerules 整体结束（到 Experiments 之前）
    size_t posExperiments          = 0; // Experiments field 起点
};

// 从 LevelSettings 起点开始，按 2193 布局前向解析到 gamerules 为止。
// cursor 必须位于 LevelSettings 起点（即 StartGame payload + 前 5 个顶层字段解析完之后）。
bool parseLevelSettingsToGamerules(PacketCursor& cursor, LevelSettingsDiagnostic& diag) {
    using std::string;
    size_t begin = 0, end = 0;

    // field 0: Seed (uint64, 8 bytes)
    cursor.skip(8);
    if (!cursor.ok) return false;

    // field 1: SpawnSettings
    //   Spawn Biome Type (int16, 2 bytes)
    cursor.skip(2);
    //   User Defined Biome Name (string)
    if (!cursor.str(begin, end)) return false;
    //   Dimension (varint32)
    cursor.var();
    if (!cursor.ok) return false;

    // field 2: Generator Type (varint32)
    cursor.var();
    if (!cursor.ok) return false;

    // field 3: Game Type (varint32)
    cursor.var();
    if (!cursor.ok) return false;

    // field 4: Is Hardcore (bool, 1 byte)
    cursor.skip(1);
    if (!cursor.ok) return false;

    // field 5: Game Difficulty (varint32)
    cursor.var();
    if (!cursor.ok) return false;

    // field 6: Default Spawn Block Position (3 × varint32)
    cursor.var(); cursor.var(); cursor.var();
    if (!cursor.ok) return false;

    // field 7: Achievements Disabled (bool, 1 byte)
    cursor.skip(1);
    if (!cursor.ok) return false;

    // field 8: Editor World Type (varint32)
    cursor.var();
    if (!cursor.ok) return false;

    // field 9: Is Created In Editor (bool, 1 byte)
    cursor.skip(1);
    if (!cursor.ok) return false;

    // field 10: Is Exported From Editor (bool, 1 byte)
    cursor.skip(1);
    if (!cursor.ok) return false;
    diag.posAfterExportedEditor = cursor.pos;

    // field 11: Day Cycle Stop Time (varint32)
    cursor.var();
    if (!cursor.ok) return false;
    diag.posAfterDayCycleStop = cursor.pos;

    // field 12: Education Edition Offer (varuint32)
    diag.posEduOffer = cursor.pos;
    cursor.var();
    if (!cursor.ok) return false;
    diag.posAfterEduOffer = cursor.pos;

    // field 13: Education Features Enabled (bool, 1 byte)
    cursor.skip(1);
    if (!cursor.ok) return false;

    // field 14: Education Product ID (string)
    if (!cursor.str(begin, end)) return false;

    // field 15: Rain Level (float, 4 bytes)
    cursor.skip(4);
    if (!cursor.ok) return false;

    // field 16: Lightning Level (float, 4 bytes)
    cursor.skip(4);
    if (!cursor.ok) return false;

    // field 17: Has Confirmed Platform Locked Content (bool, 1 byte)
    cursor.skip(1);
    if (!cursor.ok) return false;

    // field 18: Multiplayer Game Intent (bool, 1 byte)
    cursor.skip(1);
    if (!cursor.ok) return false;

    // field 19: LAN Broadcast Intent (bool, 1 byte)
    cursor.skip(1);
    if (!cursor.ok) return false;

    // field 20: Xbox Live Broadcast Setting (varint32)
    cursor.var();
    if (!cursor.ok) return false;

    // field 21: Platform Broadcast Setting (varint32)
    cursor.var();
    if (!cursor.ok) return false;

    // field 22: Commands Enabled (bool, 1 byte)
    cursor.skip(1);
    if (!cursor.ok) return false;
    diag.posAfterCommandsEnabled = cursor.pos;

    // field 23: Texture Packs Required (bool, 1 byte)
    cursor.skip(1);
    if (!cursor.ok) return false;
    diag.posAfterTexturePacksReq = cursor.pos;

    // field 24: Rule Data (GameRulesChangedPacketData) = Rules List (array<GameRule>)
    diag.posGamerules = cursor.pos;
    uint64_t rulesCount = cursor.var();
    if (!cursor.ok) return false;
    diag.posGamerulesContent = cursor.pos;

    // 逐条解析 GameRule 以定位 gamerules 结束位置
    for (uint64_t i = 0; i < rulesCount && cursor.ok; ++i) {
        // Rule Name (string)
        if (!cursor.str(begin, end)) return false;
        // Rule Can Be Modified (bool, 1 byte)
        cursor.skip(1);
        if (!cursor.ok) return false;
        // Rule Value: oneOf<null=0, boolean=1, int32=2, float=3>
        uint64_t variant = cursor.var();
        if (!cursor.ok) return false;
        switch (variant) {
        case 0: break; // null
        case 1: cursor.skip(1); break;   // boolean
        case 2: cursor.skip(4); break;   // int32 (固定 4 bytes LE)
        case 3: cursor.skip(4); break;   // float (固定 4 bytes LE)
        default:
            hookLog("[JERCPLD] gamerules: unknown variant={} at rule {}", variant, i);
            return false;
        }
        if (!cursor.ok) return false;
    }
    diag.posAfterGamerules = cursor.pos;
    diag.posExperiments = cursor.pos; // Experiments 紧跟 gamerules
    diag.ok = true;
    return true;
}

// ---- varint 编解码辅助 ----

// 从 data[pos] 起读取 unsigned varint（同 PacketCursor::var()，返回 uint64）
static uint64_t readVarAt(std::string const& data, size_t& pos) {
    uint64_t value = 0;
    int      shift = 0;
    while (pos < data.size() && shift <= 63) {
        uint8_t byte = static_cast<uint8_t>(data[pos++]);
        value |= static_cast<uint64_t>(byte & 0x7F) << shift;
        if ((byte & 0x80) == 0) return value;
        shift += 7;
    }
    return value;
}
static void writeVar(std::string& out, uint64_t value) {
    while (value >= 0x80) {
        out.push_back(static_cast<char>((value & 0x7F) | 0x80));
        value >>= 7;
    }
    out.push_back(static_cast<char>(value));
}

// 2193 -> 1001: gamerules 重编码：GameRule value variant=2(int32) 从 li32 固定 4 字节（2193 GameRuleI32）
// 改成 varint LEB128（1001 GameRuleVarint）。
// 输入: gamerules 段起点（array count varint 的起点），段结束位置。
// 输出: newGamerules 重建内容；返回 true 表示成功。
static bool reencodeGamerulesInt32Li32ToVarint(
    std::string const& data,
    size_t             startPos,
    size_t             endPos,
    std::string&       out
) {
    out.clear();
    out.reserve(endPos - startPos);

    size_t pos = startPos;
    uint64_t count = readVarAt(data, pos); // array size varint
    if (count > 65536) return false;       // sanity
    writeVar(out, count);                  // 透传 array count

    size_t begin = 0, end = 0;
    for (uint64_t i = 0; i < count && pos < endPos; ++i) {
        // Rule Name: string (varuint len + bytes)
        uint64_t nameLen = readVarAt(data, pos);
        if (nameLen > 16384 || pos + nameLen > endPos) return false;
        writeVar(out, nameLen);
        out.append(data, pos, static_cast<size_t>(nameLen));
        pos += static_cast<size_t>(nameLen);

        // Rule Can Be Modified: bool (1 byte)
        if (pos + 1 > endPos) return false;
        out.push_back(data[pos++]);

        // Rule Value variant: null=0, boolean=1, int32=2, float=3
        uint64_t variant = readVarAt(data, pos);
        writeVar(out, variant);
        if (variant == 0) {
            // null: no value bytes
        } else if (variant == 1) {
            // boolean: 1 byte
            if (pos + 1 > endPos) return false;
            out.push_back(data[pos++]);
        } else if (variant == 2) {
            // int32: 2193 = li32 固定 4 字节（GameRuleI32） → 1001 = varint LEB128（GameRuleVarint）
            if (pos + 4 > endPos) return false;
            uint32_t u = static_cast<uint32_t>(static_cast<uint8_t>(data[pos]))
                       | (static_cast<uint32_t>(static_cast<uint8_t>(data[pos + 1])) << 8)
                       | (static_cast<uint32_t>(static_cast<uint8_t>(data[pos + 2])) << 16)
                       | (static_cast<uint32_t>(static_cast<uint8_t>(data[pos + 3])) << 24);
            pos += 4;
            writeVar(out, u);
        } else if (variant == 3) {
            // float: 4 bytes, 直接透传
            if (pos + 4 > endPos) return false;
            out.append(data, pos, 4);
            pos += 4;
        } else {
            // unknown variant —— 不认识就不能安全重编码
            return false;
        }
    }
    if (pos != endPos) {
        hookLog(
            "[JERCPLD] gamerules reencode: parse ended at {} vs expected {}, trailing bytes will be preserved",
            pos, endPos
        );
        // 允许有 trailing（理论上不该有，但保险起见）
    }
    return true;
}

// BlockProperties: varint 计数 + N × (name:string + state:网络 NBT)
bool skipBlockProperties(PacketCursor& c) {
    uint64_t count = c.var();
    if (!c.ok || count > 100000) return false;
    for (uint64_t i = 0; i < count; ++i) {
        size_t begin = 0, end = 0;
        if (!c.str(begin, end)) return false;
        if (!skipNetworkNbt(c)) return false;
    }
    return true;
}

// 2193 -> 1001/2168-: 重编码 GameRule 里 int32 值（li32 固定 4 字节 -> varint LEB128），
// 并在 server_controlled_sound 之后、has_server_join_info 之前插入 is_chat_logging(bool)。
// 插入位置依据 minecraft-data 1.26.30（1001 解析基准）与 zeroBlockPaletteChecksum 的
// 尾段解析交叉一致；注意 is_chat_logging 不在 Commands Enabled 附近。
// 实际改动仅限 peer < 2168（即只有 1001 需要）。
bool rewriteStartGameForOldClients(std::string& data, size_t payloadOffset, int peerProtocol) {
    // 2168 及以上已和 2193 结构一致，无需改写
    if (peerProtocol >= 2168) return true;

    PacketCursor cursor{data, payloadOffset};

    // ---- 解析 StartGame 顶层字段到 LevelSettings ----
    // field 0: Entity ID (varint64)
    cursor.var();
    if (!cursor.ok) return false;
    // field 1: Runtime ID (varuint64)
    cursor.var();
    if (!cursor.ok) return false;
    // field 2: Game Type (varint32)
    cursor.var();
    if (!cursor.ok) return false;
    // field 3: Position (3 × float32)
    cursor.skip(12);
    if (!cursor.ok) return false;
    // field 4: Rotation (2 × float32)
    cursor.skip(8);
    if (!cursor.ok) return false;
    // field 5: Settings = LevelSettings 起点
    size_t levelSettingsStart = cursor.pos;

    LevelSettingsDiagnostic diag;
    if (!parseLevelSettingsToGamerules(cursor, diag)) {
        hookLog("[JERCPLD] StartGame LevelSettings parse FAILED for peer={}, aborted", peerProtocol);
        return false;
    }
    hookLog(
        "[JERCPLD] StartGame LS parse OK: peer={} gamerules@{} rulesCount={} end@{} experiments@{} total={}",
        peerProtocol,
        diag.posGamerules,
        (data[diag.posGamerules] & 0x80) ? "<multi-byte-varint>" : std::to_string(static_cast<int>(data[diag.posGamerules] & 0x7F)),
        diag.posAfterGamerules,
        diag.posExperiments,
        data.size()
    );

    // ---- 诊断：dump 前 5 条 GameRule 的原始字节 ----
    {
        PacketCursor dumpCursor{data, diag.posGamerules};
        uint64_t dumpCount = dumpCursor.var(); // array size varint
        size_t sampleLen = std::min(dumpCount, uint64_t{5});
        size_t scanStart = dumpCursor.pos;
        size_t scanEnd   = dumpCursor.pos;
        size_t begin = 0, end = 0;
        for (uint64_t i = 0; i < sampleLen && dumpCursor.ok; ++i) {
            if (!dumpCursor.str(begin, end)) break;
            scanEnd = dumpCursor.pos;
            dumpCursor.skip(1); // can_be_modified
            if (!dumpCursor.ok) break;
            uint64_t variant = dumpCursor.var();
            if (!dumpCursor.ok) break;
            switch (variant) {
            case 0: break;
            case 1: dumpCursor.skip(1); break;
            case 2: dumpCursor.skip(4); break;
            case 3: dumpCursor.skip(4); break;
            default: break;
            }
            if (!dumpCursor.ok) break;
            scanEnd = dumpCursor.pos;
        }
        hookLog(
            "[JERCPLD] gamerules sample ({} rules, {} bytes): {}",
            sampleLen,
            scanEnd - scanStart,
            toHex(data.substr(scanStart, std::min(scanEnd - scanStart, size_t{128})))
        );
    }

    // ---- 构造新的 LevelSettings ----
    // 策略：gamerules 段重编码（li32 -> varint），is_chat_logging 在顶层尾段
    // （server_controlled_sound 之后）插入，详见下方定位与组装代码。

    // ---- 重建 gamerules ----
    size_t gamerulesArrayStart  = diag.posGamerules;       // array size varint 起点
    size_t gamerulesContentStart = diag.posGamerulesContent;
    size_t gamerulesEnd         = diag.posAfterGamerules;

    std::string newGamerules;
    bool gamerulesChanged = false;
    if (peerProtocol < 2168) {
        // 1001: 2193 的 int32 GameRule 值是 li32 固定 4 字节，需改成 varint LEB128
        if (reencodeGamerulesInt32Li32ToVarint(data, gamerulesArrayStart, gamerulesEnd, newGamerules)) {
            gamerulesChanged = (newGamerules.size() != (gamerulesEnd - gamerulesArrayStart));
            hookLog(
                "[JERCPLD] gamerules reencoded for peer<2168: {} -> {} bytes (changed={})",
                gamerulesEnd - gamerulesArrayStart, newGamerules.size(), gamerulesChanged ? "YES" : "NO"
            );
        } else {
            // 重编码失败则原样拷贝，让 BDS 的默认行为兜底
            hookLog("[JERCPLD] gamerules reencode FAILED, falling back to raw passthrough");
            newGamerules.append(data, gamerulesArrayStart, gamerulesEnd - gamerulesArrayStart);
        }
    } else {
        newGamerules.append(data, gamerulesArrayStart, gamerulesEnd - gamerulesArrayStart);
    }

    // ---- 整体重建 LevelSettings ----
    // data 结构：[0..levelSettingsStart) 顶层前 5 字段 | LevelSettings 内容 | [LevelSettings 末尾..) 顶层后半段
    // LevelSettings 内部：
    //   [0..posAfterCommandsEnabled) | CommandsEnabled 之后 | [posGamerules..gamerulesEnd) | [gamerulesEnd..)
    // 我们要做的：
    //   [0..posAfterCommandsEnabled) | +1 byte is_chat_logging(false) | 原 CommandsEnabled 之后到 gamerules 起点 | newGamerules | gamerulesEnd 之后
    //
    // 注意：newGamerules 如果长度变了，后面的偏移全部要重新算。

    std::string rebuiltLS;
    rebuiltLS.reserve(gamerulesEnd - levelSettingsStart + 4);

    // LevelSettings 头部到 posAfterCommandsEnabled 不变
    rebuiltLS.append(data, levelSettingsStart, diag.posAfterCommandsEnabled - levelSettingsStart);

    // posAfterCommandsEnabled 到 gamerulesArrayStart 不变
    rebuiltLS.append(data, diag.posAfterCommandsEnabled, diag.posGamerules - diag.posAfterCommandsEnabled);

    // gamerules 段（可能重编码过）
    rebuiltLS.append(newGamerules);

    // gamerulesEnd 到 LevelSettings 末尾不变（这部分包含 Experiments + 后续所有 LevelSettings 字段）
    // 但我需要知道 LevelSettings 的末尾在哪里！
    // 方案：从 gamerulesEnd 继续前向解析 LevelSettings 剩余字段直到末尾（StartGame 顶层 field 6 Level ID 起点之前）。

    // 让 cursor 回到 gamerulesEnd，继续解析
    PacketCursor tailCursor{data, diag.posAfterGamerules};

    // ---- LevelSettings 续：从 Experiments 开始 ----
    // field 25: Experiments
    //   Toggles: array<ExperimentToggle> (无 varint size 前缀)
    //     ExperimentToggle: Name(string) + Enabled(bool)
    //   ExperimentsEverToggled: bool
    {
        size_t scanStart = tailCursor.pos;
        size_t expToggleCount = 0;
        // 协议说明：Toggles 数组无 size 前缀，直到读到非 ExperimentToggle 内容为止。
        // 实际解析：Experiments = array<Experiment>{countType: li32}（bedrock-protocol schema，
        // 与 varint 计数的 array 不同），计数固定 4 字节小端。
        uint32_t expCount = tailCursor.li32();
        if (!tailCursor.ok) {
            hookLog("[JERCPLD] LevelSettings parse: Experiments count read FAILED");
            return false;
        }
        for (uint64_t i = 0; i < expCount && tailCursor.ok; ++i) {
            size_t b = 0, e = 0;
            if (!tailCursor.str(b, e)) return false; // Name
            tailCursor.skip(1); // Enabled(bool)
        }
        // ExperimentsEverToggled (bool, 1 byte)
        tailCursor.skip(1);
        if (!tailCursor.ok) {
            hookLog("[JERCPLD] LevelSettings parse: ExperimentsEverToggled FAILED");
            return false;
        }
        size_t scanEnd = tailCursor.pos;
        hookLog(
            "[JERCPLD] Experiments: count={} tail_bytes={} next_byte={:02x}",
            expCount,
            scanEnd - scanStart,
            scanEnd < data.size() ? static_cast<unsigned char>(data[scanEnd]) : 0
        );
    }

    // field 26: Has Bonus Chest Enabled (bool)
    tailCursor.skip(1);
    // field 27: Start With Map Enabled (bool)
    tailCursor.skip(1);
    // field 28: Player Permissions (int8)
    tailCursor.skip(1);
    // field 29: Server Chunk Tick Range (int32, 4 bytes)
    tailCursor.skip(4);
    // field 30: Has Locked Behavior Pack (bool)
    tailCursor.skip(1);
    // field 31: Has Locked Resource Pack (bool)
    tailCursor.skip(1);
    // field 32: Is From Locked Template (bool)
    tailCursor.skip(1);
    // field 33: Use Msa Gamertags Only (bool)
    tailCursor.skip(1);
    // field 34: Is From World Template (bool)
    tailCursor.skip(1);
    // field 35: Is World Template Option Locked (bool)
    tailCursor.skip(1);
    // field 36: Only Spawn V1 Villagers (bool)
    tailCursor.skip(1);
    // field 37: Persona Disabled (bool)
    tailCursor.skip(1);
    // field 38: Custom Skins Disabled (bool)
    tailCursor.skip(1);
    // field 39: Emote Chat Muted (bool)
    tailCursor.skip(1);
    // field 40: Base Game Version (string)
    {
        size_t b = 0, e = 0;
        if (!tailCursor.str(b, e)) {
            hookLog("[JERCPLD] LevelSettings parse: Base Game Version FAILED");
            return false;
        }
    }
    // field 41: Limited World Width (int32, 4 bytes)
    tailCursor.skip(4);
    // field 42: Limited World Depth (int32, 4 bytes)
    tailCursor.skip(4);
    // field 43: Nether Type (bool)
    tailCursor.skip(1);
    // field 44: Edu Shared Uri Resource
    //   Button Name (string) + Link Uri (string)
    {
        size_t b = 0, e = 0;
        if (!tailCursor.str(b, e)) {
            hookLog("[JERCPLD] LevelSettings parse: Edu Shared Uri Button FAILED");
            return false;
        }
        if (!tailCursor.str(b, e)) {
            hookLog("[JERCPLD] LevelSettings parse: Edu Shared Uri Link FAILED");
            return false;
        }
    }
    // field 45: Experimental Gameplay Override (bool，1.26.51/1.26.30 schema 均为普通 bool，非 optional)
    tailCursor.skip(1);
    // field 46: Chat Restriction Level (uint8, 1 byte)
    tailCursor.skip(1);
    // field 47: Disable Player Interactions (bool, 1 byte)
    tailCursor.skip(1);
    // field 48: Server Editor Connection Policy (varint32)
    tailCursor.var();
    // field 49: Allow Anonymous Block Drops In Editor Worlds (bool, 1 byte)
    tailCursor.skip(1);

    size_t levelSettingsEnd = tailCursor.pos;
    hookLog(
        "[JERCPLD] LevelSettings parse OK: totalLS={} newLS={} change={} bytes",
        levelSettingsEnd - levelSettingsStart,
        rebuiltLS.size(),
        rebuiltLS.size() - (levelSettingsEnd - levelSettingsStart)
    );

    if (!tailCursor.ok) {
        hookLog("[JERCPLD] LevelSettings tail parse FAILED");
        return false;
    }

    // LevelSettings 尾部剩余直接追加
    rebuiltLS.append(data, diag.posAfterGamerules, levelSettingsEnd - diag.posAfterGamerules);

    // ---- 定位 is_chat_logging 插入点（StartGame 顶层尾段） ----
    // minecraft-data 1.26.30 字段序（与 1.26.51 完全一致）：
    //   level_id(str) world_name(str) premium_world_template_id(str) is_trial(u8)
    //   rewind_history_size(zigzag32) server_authoritative_block_breaking(u8)
    //   current_tick(li64) enchantment_seed(zigzag32) block_properties(array<str+nbt>)
    //   multiplayer_correlation_id(str) server_authoritative_inventory(u8) engine(str)
    //   property_data(nbt) block_pallette_checksum(lu64) world_template_id(uuid)
    //   client_side_generation(u8) block_network_ids_are_hashes(u8) server_controlled_sound(u8)
    //   -> is_chat_logging(bool) 插入点在 server_controlled_sound 之后
    size_t chatLogInsertPos = levelSettingsEnd;
    {
        PacketCursor topCursor{data, levelSettingsEnd};
        size_t b = 0, e = 0;
        if (!topCursor.str(b, e)) return false;               // level_id
        if (!topCursor.str(b, e)) return false;               // world_name
        if (!topCursor.str(b, e)) return false;               // premium_world_template_id
        topCursor.skip(1);                                    // is_trial
        topCursor.var();                                      // rewind_history_size (zigzag32)
        topCursor.skip(1);                                    // server_authoritative_block_breaking
        topCursor.skip(8);                                    // current_tick (li64)
        topCursor.var();                                      // enchantment_seed (zigzag32)
        if (!skipBlockProperties(topCursor)) return false;    // block_properties
        if (!topCursor.str(b, e)) return false;               // multiplayer_correlation_id
        topCursor.skip(1);                                    // server_authoritative_inventory
        if (!topCursor.str(b, e)) return false;               // engine
        if (!skipNetworkNbt(topCursor)) return false;         // property_data
        topCursor.skip(8);                                    // block_pallette_checksum (lu64)
        topCursor.skip(16);                                   // world_template_id (uuid)
        topCursor.skip(1);                                    // client_side_generation
        topCursor.skip(1);                                    // block_network_ids_are_hashes
        topCursor.skip(1);                                    // server_controlled_sound
        if (!topCursor.ok) {
            hookLog("[JERCPLD] StartGame: chat_logging insert point locate FAILED at {}", topCursor.pos);
            return false;
        }
        chatLogInsertPos = topCursor.pos;
    }
    hookLog(
        "[JERCPLD] StartGame: is_chat_logging insert at {} (levelSettingsEnd={})",
        chatLogInsertPos, levelSettingsEnd
    );

    // ---- 组装新包 ----
    std::string result;
    result.reserve(data.size() + 8);
    result.append(data, 0, levelSettingsStart);
    result.append(rebuiltLS);
    result.append(data, levelSettingsEnd, chatLogInsertPos - levelSettingsEnd);
    result.push_back('\0');                  // is_chat_logging(false)，仅 1001 需要
    result.append(data, chatLogInsertPos);   // has_server_join_info 起不变
    hookLog(
        "[JERCPLD] StartGame rebuilt: old={} new={} diff={} peer={}",
        data.size(),
        result.size(),
        result.size() - data.size(),
        peerProtocol
    );
    data = std::move(result);
    return true;
}

// 2193 -> 2225：补齐新增的尾部字段（仅限字段确实位于末尾的包）。
// 说明：本函数内含 peer<2168（26.30/26.40/26.45）的历史处理分支，
// 非官方维护、仅作社区示例保留；官方支持范围已收敛为 2193..2225。
void translateOutboundPacket(std::string& data, int peerProtocol) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    if (packetId == 198) { // CameraPresets: 仅比 2193 老的客户端需要裁字段
        if (peerProtocol < kServerProtocolValue) {
            std::string original = data;
            if (stripCameraPresetTrailingFields(data, offset)) {
                hookLog(
                    "[JERCPLD] outbound camera_presets stripped: {} -> {}",
                    original.size(),
                    data.size()
                );
            } else {
                data = std::move(original);
                hookLog("[JERCPLD] outbound camera_presets strip failed: len={}", data.size());
            }
        }
        return;
    }
    if (packetId == 6) { // ResourcePacksInfo: TexturePackInfos countType 1001=li16 vs 2193=varint
        if (peerProtocol < 2168) {
            std::string original = data;
            if (fixResourcePacksInfoCountType(data, offset)) {
                hookLog(
                    "[JERCPLD] outbound resource_packs_info countType fixed (varint->li16): {} -> {}",
                    original.size(),
                    data.size()
                );
            } else {
                data = std::move(original);
                hookLog("[JERCPLD] outbound resource_packs_info countType fix failed: len={}", data.size());
            }
        }
        return;
    }
    // peer < 2168: StartGame 需要重写 LevelSettings（插入 is_chat_logging + 可选 gamerules 重编码）
    if (peerProtocol < 2168 && packetId == 11) {
        std::string original = data;
        if (rewriteStartGameForOldClients(data, offset, peerProtocol)) {
            hookLog(
                "[JERCPLD] outbound StartGame rewritten for peer<2168: {} -> {} (diff={})",
                original.size(),
                data.size(),
                data.size() - original.size()
            );
        } else {
            data = std::move(original);
            hookLog("[JERCPLD] outbound StartGame rewrite FAILED for peer<2168, original preserved");
        }
        return;
    }
    // peer < 2168: 实体 / 物品类包的线格式差异。
    // 依据 CloudburstMC/Protocol 3.0 的实际实现（不是 minecraft-data 的 schema）：
    //   Item:     1001 在 has_stack_id 为真时多一个 variantType(varint)；air 两边都是 8 字节
    //   metadata: 2168 每个条目多 1 字节 legacy_type（值为 type 的副本）
    //   teleport: 2168 是 option（带 bool 前缀），1001 是 switch（无前缀）
    if (peerProtocol < 2168) {
        switch (packetId) {
        case 12: { // add_player
            std::string original = data;
            if (jercpld1001::fixAddPlayer(data)) {
                hookLog("[JERCPLD] outbound add_player 2168->1001: {} -> {}", original.size(), data.size());
            } else {
                data = std::move(original);
                hookLog("[JERCPLD] outbound add_player fix failed, preserved: len={}", data.size());
            }
            return;
        }
        case 13: { // add_entity
            std::string original = data;
            if (jercpld1001::fixAddEntity(data)) {
                hookLog("[JERCPLD] outbound add_entity 2168->1001: {} -> {}", original.size(), data.size());
            } else {
                data = std::move(original);
                hookLog("[JERCPLD] outbound add_entity fix failed, preserved: len={}", data.size());
            }
            return;
        }
        case 15: { // add_item_entity
            std::string original = data;
            if (jercpld1001::fixAddItemEntity(data)) {
                hookLog("[JERCPLD] outbound add_item_entity 2168->1001: {} -> {}", original.size(), data.size());
            } else {
                data = std::move(original);
                hookLog("[JERCPLD] outbound add_item_entity fix failed, preserved: len={}", data.size());
            }
            return;
        }
        case 19: { // move_player
            std::string original = data;
            if (jercpld1001::fixMovePlayer(data)) {
                hookLog("[JERCPLD] outbound move_player teleport prefix stripped: {} -> {}", original.size(), data.size());
            } else {
                data = std::move(original);
                hookLog("[JERCPLD] outbound move_player fix failed, preserved: len={}", data.size());
            }
            return;
        }
        case 31: { // mob_equipment
            std::string original = data;
            if (jercpld1001::fixMobEquipment(data)) {
                hookLog("[JERCPLD] outbound mob_equipment 2168->1001: {} -> {}", original.size(), data.size());
            } else {
                data = std::move(original);
                hookLog("[JERCPLD] outbound mob_equipment fix failed, preserved: len={}", data.size());
            }
            return;
        }
        case 32: { // mob_armor_equipment
            std::string original = data;
            if (jercpld1001::fixMobArmorEquipment(data)) {
                hookLog("[JERCPLD] outbound mob_armor_equipment 2168->1001: {} -> {}", original.size(), data.size());
            } else {
                data = std::move(original);
                hookLog("[JERCPLD] outbound mob_armor_equipment fix failed, preserved: len={}", data.size());
            }
            return;
        }
        }
    }
    if (peerProtocol != 2225) return; // 其余 s2c 结构改写仅 2225 需要
    switch (packetId) {
    case 11: // StartGame: editor 字段中间插入 + checksum 清 0
        if (insertStartGameEditorField(data, offset)) {
            bool checksumZeroed = zeroBlockPaletteChecksum(data);
            hookLog(
                "[JERCPLD] outbound insert field: packet=11 newLen={} checksumZero={}",
                data.size(),
                checksumZeroed
            );
        } else {
            hookLog("[JERCPLD] outbound insert failed: packet=11 len={}", data.size());
        }
        break;
    case 12: // AddPlayer: passenger_of_block 在 device_id 之前
        if (insertAddPlayerPassengerField(data)) {
            hookLog("[JERCPLD] outbound insert field: packet=12 newLen={}", data.size());
        } else {
            size_t start = data.size() > 80 ? data.size() - 80 : 0;
            hookLog(
                "[JERCPLD] outbound insert failed: packet=12 len={} tail={}",
                data.size(),
                toHex(data.substr(start))
            );
        }
        break;
    case 44: // Animate: 2225 末尾新增 hand(u8)
    case 13: // AddActor: 2225 末尾新增 passenger_of_block(option)
        data.push_back('\0');
        hookLog("[JERCPLD] outbound append field: packet={} newLen={}", packetId, data.size());
        break;
    case 58: { // LevelChunk: skeleton 标记 + 末尾 IsClientBiomeUpdate(bool)
        if (promoteLevelChunkToSkeleton(data, offset)) {
            hookLog("[JERCPLD] outbound chunk skeleton: newLen={}", data.size());
        } else {
            data.push_back('\0');
        }
        break;
    }
    case 63: { // PlayerList: 记录级新增 play_fab_id，Skin 删除 play_fab_id
        std::string original = data;
        if (translatePlayerList(data, offset)) {
            hookLog("[JERCPLD] outbound player_list rewritten: {} -> {}", original.size(), data.size());
        } else {
            data = std::move(original);
            hookLog("[JERCPLD] outbound player_list rewrite failed: len={}", data.size());
        }
        break;
    }
    default:
        break;
    }
}

// ============================================================================
// 反向步骤：2225 服务端 -> 2193 客户端
// 本机没有 26.60 的 LeviLamina，反向链路尚未与真实 2225 服务端跑通，
// 仅对 2193 抓包做「正向变换后反向还原」的往返验证。未验证项显式标记 TODO。
// ============================================================================

// StartGame：删除 2225 中间字段 editor_level_migration_version(u8)。
bool removeStartGameEditorField(std::string& data, size_t payloadOffset) {
    PacketReader r{data, payloadOffset};
    if (!r.var()) return false;     // entity_id
    if (!r.var()) return false;     // runtime_entity_id
    if (!r.var()) return false;     // player_gamemode
    if (!r.skip(12)) return false;  // player_position
    if (!r.skip(8)) return false;   // rotation
    if (!r.skip(8)) return false;   // seed
    if (!r.skip(2)) return false;   // biome_type
    if (!r.str()) return false;     // biome_name
    if (!r.var()) return false;     // dimension
    if (!r.var()) return false;     // generator
    if (!r.var()) return false;     // world_gamemode
    if (!r.skip(1)) return false;   // hardcore
    if (!r.var()) return false;     // difficulty
    if (!r.var()) return false;     // spawn x
    if (!r.var()) return false;     // spawn y
    if (!r.var()) return false;     // spawn z
    if (!r.skip(1)) return false;   // achievements_disabled
    if (!r.var()) return false;     // editor_world_type
    if (!r.skip(2)) return false;   // created_in_editor + exported_from_editor
    if (r.pos >= data.size()) return false;
    data.erase(r.pos, 1);
    return true;
}

// Animate：删除 2225 末尾 hand(u8)。
bool removeAnimateHandField(std::string& data, size_t payloadOffset) {
    PacketCursor c{data, payloadOffset};
    c.u8();         // action_id
    c.var();        // runtime_entity_id
    c.skip(4);      // data
    uint8_t hasSwing = c.u8();
    if (!c.ok) return false;
    if (hasSwing == 1) {
        size_t b = 0, e = 0;
        if (!c.str(b, e)) return false;
    }
    if (c.pos + 1 != data.size()) return false;
    data.pop_back();
    return true;
}

// LevelChunk：删除 2225 末尾 client_biome_update(bool)。
// 注意 cache_metadata 的数组长度字段无论 cache_enabled 与否都存在。
bool removeLevelChunkTrailingBool(std::string& data, size_t payloadOffset) {
    PacketCursor c{data, payloadOffset};
    c.var(); // x
    c.var(); // z
    c.var(); // dimension
    c.var(); // sub_chunk_count
    if (!c.ok || c.pos >= data.size()) return false;
    if (c.u8() != 0) { // optional client request subchunk limit
        c.var();
        if (!c.ok) return false;
    }
    if (!c.ok) return false;
    c.u8(); // cache_enabled
    uint64_t blobCount = c.var();
    if (!c.ok || blobCount > 4096) return false;
    c.skip(static_cast<size_t>(blobCount) * 8);
    if (!c.ok) return false;
    size_t b = 0, e = 0;
    if (!c.str(b, e)) return false; // payload
    if (c.pos + 1 != data.size()) return false;
    data.pop_back();
    return true;
}

// 反向未实现项：AddPlayer(12) 的 passenger_of_block、AddActor(13) 尾字段、
// PlayerList(63) 的 play_fab_id 回填。需 2225 真实抓包后再写，避免盲改。
void warnReverseTodoOnce(int packetId) {
    static std::atomic<unsigned> seen{0};
    unsigned bit = 1u << (packetId & 7u);
    if (seen.fetch_or(bit, std::memory_order_relaxed) & bit) return;
    hookLog("[JERCPLD] reverse TODO: packet={} 镜像改写未实现（需 2225 服务端抓包）", packetId);
}

void translateOutbound2225To2193(std::string& data) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    switch (packetId) {
    case 11: { // StartGame
        std::string original = data;
        if (removeStartGameEditorField(data, offset) && zeroBlockPaletteChecksum(data)) {
            hookLog("[JERCPLD] reverse start_game: editor removed, checksum zeroed, len={}", data.size());
        } else {
            data = std::move(original);
            hookLog("[JERCPLD] reverse start_game rewrite failed: len={}", data.size());
        }
        break;
    }
    case 44: // Animate
        removeAnimateHandField(data, offset);
        break;
    case 58: // LevelChunk
        if (!removeLevelChunkTrailingBool(data, offset)) {
            hookLog("[JERCPLD] reverse level_chunk trailing bool not removed: len={}", data.size());
        }
        break;
    case 12:
    case 13:
    case 63:
        warnReverseTodoOnce(static_cast<int>(packetId));
        break;
    default:
        break;
    }
}

// 2193 客户端 -> 2225 服务端：animate 需要补 hand(u8)。枚举实测一致，只补字节。
// 标记待实测：真实 2225 服务端未验证。
void addAnimateHandFor2225Server(std::string& data) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    if (packetId != 44) return;
    PacketCursor c{data, offset};
    c.u8();    // action_id
    c.var();   // runtime_entity_id
    c.skip(4); // data
    uint8_t hasSwing = c.u8();
    if (!c.ok) return;
    if (hasSwing == 1) {
        size_t b = 0, e = 0;
        if (!c.str(b, e)) return;
    }
    if (c.pos != data.size()) return;
    data.push_back('\0'); // hand = main_hand
    hookLog("[JERCPLD] reverse c2s animate hand appended: newLen={}", data.size());
}

// 26.45（2169）与 26.4x（2168）已由 PTL 的 2168-2169 / 2169-2193 规则覆盖，
// 客户端走 NetherNet 直连即可进服（见 README「已完成」）。
// 仍受限的是「仅 RakNet」的旧客户端：nethernet 下 getRakPeer() 返回 null、
// RakPeer 构造符号未导出，那条传输路径不可用，详见 docs/BLOCKERS.md B4。

// ============================================================================
// PTL 规则引擎（Packet Translation Language）
// 每个版本对一个 JSON（PTL/<a>-<b>.json），文件内同时表达两个方向。
// 对称差异只声明一条事实（addedIn），引擎按方向自动取动作；
// 不对称差异写在 overrides.olderToNewer / newerToOlder。
// 结构性重写（需重新编码整包）以 builtin 形式引用引擎内置实现。
// ============================================================================

struct PtlRule {
    std::string                 type;    // appendBytes / insertField / enumRemap / zeroFixed / builtin
    int                         addedIn = 0;
    std::vector<std::string>    skip;
    std::string                 bytes;
    int                         zeros = 0;
    std::string                 builtin;
    std::unordered_map<int,int> remap;
};

struct PtlPacketRules {
    std::string          name;
    std::vector<PtlRule> rules;     // 对称事实：older->newer 加，newer->older 撤
    std::vector<PtlRule> olderToNewer;
    std::vector<PtlRule> newerToOlder;
    // 运行时自检状态：s2c / c2s 会在不同线程并发命中同一个 pair，用原子量避免数据竞争。
    mutable std::atomic_bool validated{false};
    mutable std::atomic_bool disabled{false};

    PtlPacketRules() = default;
    PtlPacketRules(PtlPacketRules const& other) { *this = other; }
    PtlPacketRules& operator=(PtlPacketRules const& other) {
        if (this == &other) return *this;
        name         = other.name;
        rules        = other.rules;
        olderToNewer = other.olderToNewer;
        newerToOlder = other.newerToOlder;
        validated.store(other.validated.load(std::memory_order_relaxed), std::memory_order_relaxed);
        disabled.store(other.disabled.load(std::memory_order_relaxed), std::memory_order_relaxed);
        return *this;
    }
    PtlPacketRules(PtlPacketRules&& other) { *this = other; }
    PtlPacketRules& operator=(PtlPacketRules&& other) {
        if (this != &other) *this = static_cast<PtlPacketRules const&>(other);
        return *this;
    }
};

struct PtlPair {
    int                                        api = 0;
    int                                        a = 0, b = 0;
    std::vector<std::string>                   serverVersions;
    std::unordered_map<int, PtlPacketRules>    packets;
    std::string                                file;
};

std::vector<PtlPair>           gPtlPairs;
std::shared_mutex              gPtlMutex;
std::atomic_bool               gPtlLoaded{false};
// 当前包的 drop 决策：由 ptlTranslate 按方向设置，发送/接收 hook 在其后立即读取。
thread_local bool              gPtlDrop = false;
// 当前包是否命中了至少一条改写规则（含 drop），用于性能计数里的「是否拷贝」。
thread_local bool              gPtlRuleHit = false;

// ============================================================================
// 翻译性能计数（默认关闭）
// 开启后热路径只做 steady_clock 取时 + relaxed 原子累加：不加锁、不分配、不打印。
// 计数按 [方向][客户端协议档][包号] 分槽，30 秒由后台线程汇总一次写入 hook.log。
// 关闭时热路径只多一次 atomic load。
// ============================================================================
constexpr int kPerfPacketSlots = 384;
constexpr int kPerfHistBuckets = 24; // floor(log2(ns))，用于近似 P50
constexpr int kPerfClassCount  = 4;  // 0=2168(两跳) 1=2169 2=2225 3=其他
constexpr int kPerfDirCount    = 2;  // 0=s2c 1=c2s

struct PerfSlot {
    std::atomic<uint64_t> count{0};
    std::atomic<uint64_t> totalNs{0};
    std::atomic<uint64_t> maxNs{0};
    std::atomic<uint64_t> inBytes{0};
    std::atomic<uint64_t> outBytes{0};
    std::atomic<uint64_t> ruleHits{0};
    std::atomic<uint64_t> hist[kPerfHistBuckets]{};
};

PerfSlot         gPerfSlots[kPerfDirCount][kPerfClassCount][kPerfPacketSlots];
std::atomic_bool gPerfEnabled{false};

int perfClassOf(int protocol) {
    switch (protocol) {
    case 2168: return 0;
    case 2169: return 1;
    case 2225: return 2;
    default: return 3;
    }
}

uint64_t perfNowNs() {
    return static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()
        )
            .count()
    );
}

void perfRecord(
    int      dir,
    int      protocol,
    int      connSlot,
    uint32_t packetId,
    size_t   inLen,
    size_t   outLen,
    bool     ruleHit,
    uint64_t ns
) {
    if (connSlot >= 0 && connSlot < kConnSlotCount) {
        auto& conn = gConnStats[connSlot];
        conn.totalNs.fetch_add(ns, std::memory_order_relaxed);
        conn.packets.fetch_add(1, std::memory_order_relaxed);
        conn.inBytes.fetch_add(inLen, std::memory_order_relaxed);
        conn.outBytes.fetch_add(outLen, std::memory_order_relaxed);
    }
    if (packetId >= kPerfPacketSlots) return;
    auto& slot = gPerfSlots[dir][perfClassOf(protocol)][packetId];
    slot.count.fetch_add(1, std::memory_order_relaxed);
    slot.totalNs.fetch_add(ns, std::memory_order_relaxed);
    slot.inBytes.fetch_add(inLen, std::memory_order_relaxed);
    slot.outBytes.fetch_add(outLen, std::memory_order_relaxed);
    if (ruleHit) slot.ruleHits.fetch_add(1, std::memory_order_relaxed);
    uint64_t prev = slot.maxNs.load(std::memory_order_relaxed);
    while (ns > prev && !slot.maxNs.compare_exchange_weak(prev, ns, std::memory_order_relaxed)) {
    }
    int bucket = 0;
    if (ns != 0) {
        int width = 0;
        for (uint64_t v = ns; v != 0; v >>= 1) ++width;
        bucket = width - 1;
    }
    if (bucket >= kPerfHistBuckets) bucket = kPerfHistBuckets - 1;
    slot.hist[bucket].fetch_add(1, std::memory_order_relaxed);
}

// 从直方图近似 P50：取累计计数首次过半的桶，值取该桶左边界。
uint64_t perfP50Ns(PerfSlot const& slot, uint64_t count) {
    if (count == 0) return 0;
    uint64_t cumulative = 0;
    for (int i = 0; i < kPerfHistBuckets; ++i) {
        cumulative += slot.hist[i].load(std::memory_order_relaxed);
        if (cumulative * 2 >= count) return 1ull << i;
    }
    return 1ull << (kPerfHistBuckets - 1);
}

// 汇总并清零上一个窗口。只在后台线程调用，热路径不会读这里的中间量。
void perfFlushWindow(int windowSeconds) {
    struct TopEntry {
        int      dir = 0;
        int      cls = 0;
        int      id  = 0;
        uint64_t count = 0;
        uint64_t totalNs = 0;
        uint64_t maxNs = 0;
        uint64_t p50 = 0;
        uint64_t inBytes = 0;
        uint64_t outBytes = 0;
        uint64_t ruleHits = 0;
    };
    TopEntry top[6];
    int      topCount = 0;

    uint64_t totalCount = 0;
    uint64_t totalNs    = 0;
    uint64_t totalIn    = 0;
    uint64_t totalOut   = 0;
    uint64_t dirCount[kPerfDirCount]{};
    uint64_t clsCount[kPerfClassCount]{};
    uint64_t clsNs[kPerfClassCount]{};

    for (int d = 0; d < kPerfDirCount; ++d) {
        for (int c = 0; c < kPerfClassCount; ++c) {
            for (int id = 0; id < kPerfPacketSlots; ++id) {
                auto& slot   = gPerfSlots[d][c][id];
                uint64_t n   = slot.count.exchange(0, std::memory_order_relaxed);
                if (n == 0) {
                    // 计数为 0 时直方图理论上也为 0；防御性清零。
                    for (auto& h : slot.hist) h.store(0, std::memory_order_relaxed);
                    slot.totalNs.store(0, std::memory_order_relaxed);
                    slot.maxNs.store(0, std::memory_order_relaxed);
                    slot.inBytes.store(0, std::memory_order_relaxed);
                    slot.outBytes.store(0, std::memory_order_relaxed);
                    slot.ruleHits.store(0, std::memory_order_relaxed);
                    continue;
                }
                uint64_t ns      = slot.totalNs.exchange(0, std::memory_order_relaxed);
                uint64_t maxNs   = slot.maxNs.exchange(0, std::memory_order_relaxed);
                uint64_t inBytes = slot.inBytes.exchange(0, std::memory_order_relaxed);
                uint64_t outBytes= slot.outBytes.exchange(0, std::memory_order_relaxed);
                uint64_t hits    = slot.ruleHits.exchange(0, std::memory_order_relaxed);
                uint64_t p50     = perfP50Ns(slot, n);
                for (auto& h : slot.hist) h.store(0, std::memory_order_relaxed);

                totalCount += n;
                totalNs    += ns;
                totalIn    += inBytes;
                totalOut   += outBytes;
                dirCount[d]+= n;
                clsCount[c]+= n;
                clsNs[c]   += ns;

                TopEntry entry{d, c, id, n, ns, maxNs, p50, inBytes, outBytes, hits};
                int      pos = topCount < 6 ? topCount++ : -1;
                if (pos < 0) {
                    pos = 0;
                    for (int i = 1; i < 6; ++i) {
                        if (top[i].totalNs < top[pos].totalNs) pos = i;
                    }
                    if (top[pos].totalNs >= entry.totalNs) continue;
                }
                top[pos] = entry;
            }
        }
    }

    double   totalMs   = static_cast<double>(totalNs) / 1e6;
    double   budgetMs  = static_cast<double>(windowSeconds) * 50.0 * 20.0;
    double   tickShare = budgetMs > 0 ? totalMs * 100.0 / budgetMs : 0.0;
    double   seconds   = windowSeconds > 0 ? static_cast<double>(windowSeconds) : 1.0;

    std::string tops;
    static char const* kClassNames[kPerfClassCount] = {"2168", "2169", "2225", "other"};
    for (int i = 0; i < topCount; ++i) {
        if (top[i].count == 0) continue;
        if (!tops.empty()) tops += " | ";
        tops += fmt::format(
            "{}:{} p{} n={} t={:.2f}ms p50={:.1f}us max={:.1f}us in={}B out={}B hit={}",
            top[i].dir == 0 ? "s2c" : "c2s",
            top[i].id,
            kClassNames[top[i].cls],
            top[i].count,
            static_cast<double>(top[i].totalNs) / 1e6,
            static_cast<double>(top[i].p50) / 1e3,
            static_cast<double>(top[i].maxNs) / 1e3,
            top[i].inBytes,
            top[i].outBytes,
            top[i].ruleHits
        );
    }
    hookLog(
        "[JERCPLD][PERF] window={}s pkts={} (s2c={} c2s={}) pps={:.1f} bytes={}B->{}B time={:.2f}ms tick={:.3f}% "
        "| hop2168 n={} t={:.2f}ms | hop2169 n={} t={:.2f}ms | 2225 n={} t={:.2f}ms{}",
        windowSeconds,
        totalCount,
        dirCount[0],
        dirCount[1],
        static_cast<double>(totalCount) / seconds,
        totalIn,
        totalOut,
        totalMs,
        tickShare,
        clsCount[0],
        static_cast<double>(clsNs[0]) / 1e6,
        clsCount[1],
        static_cast<double>(clsNs[1]) / 1e6,
        clsCount[2],
        static_cast<double>(clsNs[2]) / 1e6,
        tops.empty() ? std::string{} : (" | top: " + tops)
    );
}

bool ptlDecodeHex(std::string const& hex, std::string& out) {
    if (hex.size() % 2 != 0) return false;
    out.clear();
    out.reserve(hex.size() / 2);
    auto nibble = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    for (size_t i = 0; i < hex.size(); i += 2) {
        int hi = nibble(hex[i]);
        int lo = nibble(hex[i + 1]);
        if (hi < 0 || lo < 0) return false;
        out.push_back(static_cast<char>((hi << 4) | lo));
    }
    return true;
}

// 游标程序：把 pos 移到目标字段。全部越界检查，失败不改数据。
bool ptlRunSkip(std::string const& data, size_t& pos, std::vector<std::string> const& prog) {
    for (auto const& token : prog) {
        if (token == "var") {
            int shift = 0;
            for (;;) {
                if (pos >= data.size() || shift > 63) return false;
                uint8_t byte = static_cast<uint8_t>(data[pos++]);
                if ((byte & 0x80) == 0) break;
                shift += 7;
            }
        } else if (token == "u8") {
            if (pos + 1 > data.size()) return false;
            pos += 1;
        } else if (token == "u16") {
            if (pos + 2 > data.size()) return false;
            pos += 2;
        } else if (token == "i32" || token == "lf32") {
            if (pos + 4 > data.size()) return false;
            pos += 4;
        } else if (token == "str") {
            uint64_t len = 0;
            int      shift = 0;
            for (;;) {
                if (pos >= data.size() || shift > 63) return false;
                uint8_t byte = static_cast<uint8_t>(data[pos++]);
                len |= static_cast<uint64_t>(byte & 0x7F) << shift;
                if ((byte & 0x80) == 0) break;
                shift += 7;
            }
            if (len > data.size() - pos) return false;
            pos += static_cast<size_t>(len);
        } else if (token == "nbt") {
            PacketCursor cursor{data, pos};
            if (!skipNetworkNbt(cursor)) return false;
            pos = cursor.pos;
        } else if (token == "optionalVar") {
            if (pos >= data.size()) return false;
            uint8_t flag = static_cast<uint8_t>(data[pos++]);
            if (flag == 1) {
                int shift = 0;
                for (;;) {
                    if (pos >= data.size() || shift > 63) return false;
                    uint8_t byte = static_cast<uint8_t>(data[pos++]);
                    if ((byte & 0x80) == 0) break;
                    shift += 7;
                }
            }
        } else if (token.rfind("skip:", 0) == 0) {
            long n = std::strtol(token.c_str() + 5, nullptr, 10);
            if (n < 0 || static_cast<size_t>(n) > data.size() - pos) return false;
            pos += static_cast<size_t>(n);
        } else if (token.rfind("marker:", 0) == 0) {
            // marker:<text>：定位到该文本起点，再按「前置长度字节」跳到字符串结束。
            std::string needle = token.substr(7);
            size_t      found  = data.find(needle, pos);
            if (found == std::string::npos || found == 0) return false;
            uint8_t lenByte = static_cast<uint8_t>(data[found - 1]);
            if ((lenByte & 0x80) != 0) return false;
            size_t end = found + lenByte;
            if (end > data.size()) return false;
            pos = end;
        } else {
            return false;
        }
    }
    return true;
}

// 2169/2193 结构改写辅助（varint/zigzag/字符串）。
bool ptlReadVarUInt(std::string const& data, size_t& pos, uint32_t& value) {
    value      = 0;
    int shift  = 0;
    while (pos < data.size() && shift <= 28) {
        uint8_t byte = static_cast<uint8_t>(data[pos++]);
        value |= static_cast<uint32_t>(byte & 0x7F) << shift;
        if ((byte & 0x80) == 0) return true;
        shift += 7;
    }
    return false;
}

void ptlAppendVarUInt(std::string& out, uint32_t value) {
    while (value >= 0x80) {
        out.push_back(static_cast<char>((value & 0x7F) | 0x80));
        value >>= 7;
    }
    out.push_back(static_cast<char>(value));
}

bool ptlReadString(std::string const& data, size_t& pos, std::string& out) {
    uint32_t len = 0;
    if (!ptlReadVarUInt(data, pos, len)) return false;
    if (len > data.size() - pos) return false;
    out.assign(data, pos, len);
    pos += len;
    return true;
}

void ptlAppendString(std::string& out, std::string const& value) {
    ptlAppendVarUInt(out, static_cast<uint32_t>(value.size()));
    out.append(value);
}

bool ptlReadZigZag32(std::string const& data, size_t& pos, int32_t& value) {
    uint32_t raw = 0;
    if (!ptlReadVarUInt(data, pos, raw)) return false;
    value = static_cast<int32_t>((raw >> 1) ^ static_cast<uint32_t>(-static_cast<int32_t>(raw & 1)));
    return true;
}

void ptlAppendZigZag32(std::string& out, int32_t value) {
    uint32_t raw = (static_cast<uint32_t>(value) << 1) ^ static_cast<uint32_t>(value >> 31);
    ptlAppendVarUInt(out, raw);
}

// 读取 varint（最多 10 字节），只推进位置；用于 zigzag64/varint64 字段。
bool ptlSkipVarint(std::string const& data, size_t& pos) {
    for (int i = 0; i < 10; ++i) {
        if (pos >= data.size()) return false;
        uint8_t byte = static_cast<uint8_t>(data[pos++]);
        if ((byte & 0x80) == 0) return true;
    }
    return false;
}

// boss_event：2169（1.26.45）在 target_entity_id 之后无条件带 player_id(zigzag64)，
// 2193（1.26.51）没有该字段。方向必须与版本一致：
//   2169 -> 2193 删除 player_id；2193 -> 2169 插入 0（无归属）。
bool bossEventTo2193(std::string& data, size_t payloadOffset) {
    size_t pos = payloadOffset;
    if (!ptlSkipVarint(data, pos)) return false; // target_entity_id
    size_t start = pos;
    if (!ptlSkipVarint(data, pos)) return false; // player_id
    data.erase(start, pos - start);
    return true;
}

bool bossEventTo2169(std::string& data, size_t payloadOffset) {
    size_t pos = payloadOffset;
    if (!ptlSkipVarint(data, pos)) return false; // target_entity_id
    data.insert(pos, 1, '\0');                   // player_id = 0
    return true;
}

// play_sound：2193 在 loop_count 后新增 bypass_listener_range_check(bool)，
// 末尾新增 playback_position_seconds(option lf32)；2169 两者都没有。
bool playSoundTo2169(std::string& data, size_t payloadOffset) {
    size_t      pos = payloadOffset;
    std::string name;
    if (!ptlReadString(data, pos, name)) return false;
    for (int i = 0; i < 3; ++i) { // BlockCoordinates(x,y,z zigzag32)
        if (!ptlSkipVarint(data, pos)) return false;
    }
    if (pos + 8 > data.size()) return false;
    pos += 8; // volume + pitch
    if (!ptlSkipVarint(data, pos)) return false; // loop_count
    size_t bypassPos = pos;
    if (pos >= data.size()) return false;
    ++pos; // bypass_listener_range_check
    size_t handleBegin = pos;
    if (pos >= data.size()) return false;
    uint8_t hasHandle = static_cast<uint8_t>(data[pos++]);
    if (hasHandle == 1) {
        if (pos + 8 > data.size()) return false;
        pos += 8;
    } else if (hasHandle != 0) {
        return false;
    }
    size_t handleEnd = pos;
    if (pos >= data.size()) return false;
    uint8_t hasPlayback = static_cast<uint8_t>(data[pos++]);
    if (hasPlayback == 1) {
        if (pos + 4 > data.size()) return false;
        pos += 4;
    } else if (hasPlayback != 0) {
        return false;
    }
    if (pos != data.size()) return false;
    std::string out = data.substr(0, bypassPos);
    out.append(data, handleBegin, handleEnd - handleBegin);
    data = std::move(out);
    return true;
}

bool playSoundTo2193(std::string& data, size_t payloadOffset) {
    size_t      pos = payloadOffset;
    std::string name;
    if (!ptlReadString(data, pos, name)) return false;
    for (int i = 0; i < 3; ++i) {
        if (!ptlSkipVarint(data, pos)) return false;
    }
    if (pos + 8 > data.size()) return false;
    pos += 8;
    if (!ptlSkipVarint(data, pos)) return false;
    data.insert(pos, 1, '\0'); // bypass_listener_range_check = false
    data.push_back('\0');      // playback_position_seconds 不存在
    return true;
}

// inventory_transaction(30)：2169 的 transaction_type/actions 为 option，
// 2193 改为必选；只增删存在标志，transaction_data 结构不变。
bool skipTransactionLegacy(std::string const& data, size_t& pos) {
    if (!ptlSkipVarint(data, pos)) return false; // legacy_request_id
    if (pos >= data.size()) return false;
    uint8_t hasLegacy = static_cast<uint8_t>(data[pos++]);
    if (hasLegacy == 0) return true;
    if (hasLegacy != 1) return false;
    uint32_t count = 0;
    if (!ptlReadVarUInt(data, pos, count)) return false;
    for (uint32_t i = 0; i < count; ++i) {
        if (pos >= data.size()) return false;
        ++pos; // container_id
        uint32_t slots = 0;
        if (!ptlReadVarUInt(data, pos, slots)) return false;
        if (slots > data.size() - pos) return false;
        pos += slots; // slot_id
    }
    return true;
}

bool transactionTo2169(std::string& data, size_t payloadOffset) {
    size_t pos = payloadOffset;
    if (!skipTransactionLegacy(data, pos)) return false;
    data.insert(pos, 1, '\x01'); // option<transaction_type> = present
    ++pos;
    if (!ptlSkipVarint(data, pos)) return false; // transaction_type
    data.insert(pos, 1, '\x01'); // option<actions> = present
    return true;
}

bool transactionTo2193(std::string& data, size_t payloadOffset) {
    size_t pos = payloadOffset;
    if (!skipTransactionLegacy(data, pos)) return false;
    if (pos >= data.size() || static_cast<uint8_t>(data[pos]) != 1) return false;
    data.erase(pos, 1);
    if (!ptlSkipVarint(data, pos)) return false;
    if (pos >= data.size() || static_cast<uint8_t>(data[pos]) != 1) return false;
    data.erase(pos, 1);
    return true;
}

// item_stack_response(148)：2169 比 2193 多 containers_presence(bool)
// 和每个 slot 的 item_stack_id_presence(bool)；值分别等于紧随其后的 option 标志。
bool itemStackResponseTo2169(std::string& data, size_t payloadOffset) {
    size_t   pos = payloadOffset;
    uint32_t count = 0;
    if (!ptlReadVarUInt(data, pos, count)) return false;
    std::string out = data.substr(0, pos);
    for (uint32_t i = 0; i < count; ++i) {
        if (pos >= data.size()) return false;
        out.push_back(data[pos++]); // status
        size_t requestStart = pos;
        if (!ptlSkipVarint(data, pos)) return false; // request_id
        out.append(data, requestStart, pos - requestStart);
        if (pos >= data.size()) return false;
        uint8_t hasContainers = static_cast<uint8_t>(data[pos]);
        out.push_back(static_cast<char>(hasContainers)); // containers_presence
        out.push_back(data[pos++]);                      // option<containers>
        if (hasContainers) {
            uint32_t containerCount = 0;
            size_t   containerCountStart = pos;
            if (!ptlReadVarUInt(data, pos, containerCount)) return false;
            out.append(data, containerCountStart, pos - containerCountStart);
            for (uint32_t c = 0; c < containerCount; ++c) {
                if (pos >= data.size()) return false;
                out.push_back(data[pos++]); // container_id
                if (pos >= data.size()) return false;
                uint8_t hasDynamic = static_cast<uint8_t>(data[pos]);
                out.push_back(data[pos++]); // option<dynamic_container_id>
                if (hasDynamic) {
                    if (pos + 4 > data.size()) return false;
                    out.append(data, pos, 4);
                    pos += 4;
                }
                uint32_t slotCount = 0;
                size_t   slotCountStart = pos;
                if (!ptlReadVarUInt(data, pos, slotCount)) return false;
                out.append(data, slotCountStart, pos - slotCountStart);
                for (uint32_t s = 0; s < slotCount; ++s) {
                    if (pos + 3 > data.size()) return false;
                    out.append(data, pos, 3); // slot + hotbar_slot + count
                    pos += 3;
                    if (pos >= data.size()) return false;
                    uint8_t hasStackId = static_cast<uint8_t>(data[pos]);
                    out.push_back(static_cast<char>(hasStackId)); // item_stack_id_presence
                    out.push_back(data[pos++]);                    // option<item_stack_id>
                    if (hasStackId) {
                        size_t stackIdStart = pos;
                        if (!ptlSkipVarint(data, pos)) return false;
                        out.append(data, stackIdStart, pos - stackIdStart);
                    }
                    for (int k = 0; k < 2; ++k) { // custom_name + filtered_custom_name
                        size_t      strStart = pos;
                        std::string tmp;
                        if (!ptlReadString(data, pos, tmp)) return false;
                        out.append(data, strStart, pos - strStart);
                    }
                    size_t durabilityStart = pos;
                    if (!ptlSkipVarint(data, pos)) return false; // durability_correction
                    out.append(data, durabilityStart, pos - durabilityStart);
                }
            }
        }
    }
    if (pos != data.size()) return false;
    data = std::move(out);
    return true;
}

// subchunk(174)：2193 的 HeightMap = 16 行(每行 varint count=16 + 16 字节)，
// 2169 的 option<buffer count=256> 是去掉行头后的裸 256 字节。
bool appendHeightMapTo2169(std::string const& data, size_t& pos, std::string& out) {
    if (pos >= data.size()) return false;
    uint8_t present = static_cast<uint8_t>(data[pos++]);
    out.push_back(static_cast<char>(present));
    if (present == 0) return true;
    if (present != 1) return false;
    for (int row = 0; row < 16; ++row) {
        uint32_t rowLen = 0;
        if (!ptlReadVarUInt(data, pos, rowLen)) return false;
        if (rowLen != 16 || pos + 16 > data.size()) return false;
        out.append(data, pos, 16);
        pos += 16;
    }
    return true;
}

bool appendHeightMapTo2193(std::string const& data, size_t& pos, std::string& out) {
    if (pos >= data.size()) return false;
    uint8_t present = static_cast<uint8_t>(data[pos++]);
    out.push_back(static_cast<char>(present));
    if (present == 0) return true;
    if (present != 1) return false;
    if (pos + 256 > data.size()) return false;
    for (int row = 0; row < 16; ++row) {
        ptlAppendVarUInt(out, 16);
        out.append(data, pos, 16);
        pos += 16;
    }
    return true;
}

bool subchunkTo2169(std::string& data, size_t payloadOffset) {
    size_t      pos = payloadOffset;
    std::string out = data.substr(0, payloadOffset);
    if (pos >= data.size()) return false;
    out.push_back(data[pos++]); // cache_enabled
    size_t dimensionStart = pos;
    if (!ptlSkipVarint(data, pos)) return false;
    out.append(data, dimensionStart, pos - dimensionStart);
    if (pos + 12 > data.size()) return false;
    out.append(data, pos, 12); // origin
    pos += 12;
    uint32_t entries = 0;
    size_t   entriesStart = pos;
    if (!ptlReadVarUInt(data, pos, entries)) return false;
    out.append(data, entriesStart, pos - entriesStart);
    for (uint32_t i = 0; i < entries; ++i) {
        if (pos + 4 > data.size()) return false;
        out.append(data, pos, 4); // dx + dy + dz + result
        pos += 4;
        if (pos >= data.size()) return false;
        uint8_t hasPayload = static_cast<uint8_t>(data[pos]);
        out.push_back(data[pos++]);
        if (hasPayload) {
            uint32_t len = 0;
            size_t   lenStart = pos;
            if (!ptlReadVarUInt(data, pos, len)) return false;
            if (len > data.size() - pos) return false;
            out.append(data, lenStart, (pos + len) - lenStart);
            pos += len;
        }
        if (pos >= data.size()) return false;
        out.push_back(data[pos++]); // heightmap_type
        if (!appendHeightMapTo2169(data, pos, out)) return false;
        if (pos >= data.size()) return false;
        out.push_back(data[pos++]); // render_heightmap_type
        if (!appendHeightMapTo2169(data, pos, out)) return false;
        if (pos >= data.size()) return false;
        uint8_t hasBlob = static_cast<uint8_t>(data[pos]);
        out.push_back(data[pos++]);
        if (hasBlob) {
            if (pos + 8 > data.size()) return false;
            out.append(data, pos, 8);
            pos += 8;
        }
    }
    if (pos != data.size()) return false;
    data = std::move(out);
    return true;
}

bool subchunkTo2193(std::string& data, size_t payloadOffset) {
    size_t      pos = payloadOffset;
    std::string out = data.substr(0, payloadOffset);
    if (pos >= data.size()) return false;
    out.push_back(data[pos++]);
    size_t dimensionStart = pos;
    if (!ptlSkipVarint(data, pos)) return false;
    out.append(data, dimensionStart, pos - dimensionStart);
    if (pos + 12 > data.size()) return false;
    out.append(data, pos, 12);
    pos += 12;
    uint32_t entries = 0;
    size_t   entriesStart = pos;
    if (!ptlReadVarUInt(data, pos, entries)) return false;
    out.append(data, entriesStart, pos - entriesStart);
    for (uint32_t i = 0; i < entries; ++i) {
        if (pos + 4 > data.size()) return false;
        out.append(data, pos, 4);
        pos += 4;
        if (pos >= data.size()) return false;
        uint8_t hasPayload = static_cast<uint8_t>(data[pos]);
        out.push_back(data[pos++]);
        if (hasPayload) {
            uint32_t len = 0;
            size_t   lenStart = pos;
            if (!ptlReadVarUInt(data, pos, len)) return false;
            if (len > data.size() - pos) return false;
            out.append(data, lenStart, (pos + len) - lenStart);
            pos += len;
        }
        if (pos >= data.size()) return false;
        out.push_back(data[pos++]);
        if (!appendHeightMapTo2193(data, pos, out)) return false;
        if (pos >= data.size()) return false;
        out.push_back(data[pos++]);
        if (!appendHeightMapTo2193(data, pos, out)) return false;
        if (pos >= data.size()) return false;
        uint8_t hasBlob = static_cast<uint8_t>(data[pos]);
        out.push_back(data[pos++]);
        if (hasBlob) {
            if (pos + 8 > data.size()) return false;
            out.append(data, pos, 8);
            pos += 8;
        }
    }
    if (pos != data.size()) return false;
    data = std::move(out);
    return true;
}

// player_auth_input(144)：真机 2169 的 input_data 是 3 字节小端 bitfield（bit i = InputData i，
// 0..23 与 2193 的枚举名称一致）；2193 服务端期待 array<InputData>。
// 其后的 5 个 option<bool> 在真机样本中均为 (present, false)，2193 对应 5 个 option 字段，
// 这里统一转成“absent”；若遇到 true 则显式失败，避免把未知载荷猜成空。
// 已经是 2193 array 布局时不要重复改写（模拟客户端/部分第三方客户端走这条）。
bool validatePlayerAuthInputArray(std::string const& data, size_t payloadOffset) {
    size_t pos = payloadOffset;
    if (pos + 32 > data.size()) return false;
    pos += 32;
    uint32_t count = 0;
    if (!ptlReadVarUInt(data, pos, count) || count > 66) return false;
    for (uint32_t i = 0; i < count; ++i) {
        uint32_t raw = 0;
        if (!ptlReadVarUInt(data, pos, raw)) return false;
        int32_t value = static_cast<int32_t>((raw >> 1) ^ static_cast<uint32_t>(-static_cast<int32_t>(raw & 1)));
        if (value < 0 || value > 65) return false;
    }
    for (int i = 0; i < 3; ++i) { // input_mode / play_mode / interaction_model
        if (!ptlSkipVarint(data, pos)) return false;
    }
    if (pos + 8 > data.size()) return false;
    pos += 8; // interact_rotation
    if (!ptlSkipVarint(data, pos)) return false; // tick
    if (pos + 12 > data.size()) return false;
    pos += 12; // delta
    for (int i = 0; i < 5; ++i) { // 2193 的五个 option 字段：本校验只接受 absent
        if (pos >= data.size() || static_cast<uint8_t>(data[pos++]) != 0) return false;
    }
    return pos + 28 == data.size();
}

// 真机 2169 的尾部固定为 5 组 (present=1, bool=false)，即 10 字节；用它区分两种编码。
bool looksLike2169AuthInput(std::string const& data) {
    if (data.size() < 38) return false;
    size_t pos = data.size() - 38;
    for (int i = 0; i < 5; ++i) {
        if (static_cast<uint8_t>(data[pos]) != 1 || static_cast<uint8_t>(data[pos + 1]) != 0) return false;
        pos += 2;
    }
    return true;
}

#if 0
// 已被 tryPlayerAuthInputList（长度前缀确定性解析）取代，无调用者，勿重新启用。
// 下面注释里的「小端 bitfield」是错误方向：真机数据是长度前缀列表，不是位域。
bool tryPlayerAuthInputWidth(
    std::string const& data,
    size_t             payloadOffset,
    int                width,
    bool               lenient,
    std::string&       out,
    uint64_t&          bits,
    uint64_t&          bitsHi,
    uint64_t&          tickOut
) {
    size_t bitfieldPos = payloadOffset + 32;
    if (bitfieldPos + width > data.size()) return false;
    bits   = 0;
    bitsHi = 0;
    // 2169 客户端的位域是**大端字节序**（最后一个字节才是最低 8 位）。
    // 真机静止帧验证：按小端读会把 0x01/0x02 解释成 sneaking/sneak_down，
    // 导致服务端蹲状态反复切换；按大端读则静止帧不含 sneaking。
    for (int i = 0; i < width; ++i) {
        uint8_t byte = static_cast<uint8_t>(data[bitfieldPos + (width - 1 - i)]);
        if (i < 8) bits |= static_cast<uint64_t>(byte) << (8 * i);
        else if (i < 16) bitsHi |= static_cast<uint64_t>(byte) << (8 * (i - 8));
    }
    constexpr int kInputNum = 66; // PlayerAuthInputPacketPayload::InputData::InputNum
    auto bitSet = [&](int i) -> bool {
        if (i < 64) return (bits & (1ull << i)) != 0;
        return (bitsHi & (1ull << (i - 64))) != 0;
    };
    // BDS 1.26.51 InputData 中 2/54/55 标记为 Deprecated；送进 2193 会被
    // “enum value is deprecated” 拒绝。显式过滤，不塞进数组。
    auto isDeprecated = [](int i) { return i == 2 || i == 54 || i == 55; };
    uint32_t count = 0;
    for (int i = 0; i < kInputNum; ++i) {
        if (bitSet(i) && !isDeprecated(i)) ++count;
    }
    out = data.substr(0, bitfieldPos);
    ptlAppendVarUInt(out, count);
    for (int i = 0; i < kInputNum; ++i) {
        if (bitSet(i) && !isDeprecated(i)) {
            ptlAppendVarUInt(out, static_cast<uint32_t>(i) * 2); // zigzag32
        }
    }
    size_t   pos        = bitfieldPos + width;
    uint32_t inputMode  = 0;
    uint32_t playMode   = 0;
    uint32_t rawInteract = 0;
    size_t   start      = pos;
    if (!ptlReadVarUInt(data, pos, inputMode) || inputMode > 4) return false;
    out.append(data, start, pos - start);
    start = pos;
    if (!ptlReadVarUInt(data, pos, playMode) || playMode > 9) return false;
    out.append(data, start, pos - start);
    start = pos;
    if (!ptlReadVarUInt(data, pos, rawInteract)) return false;
    int32_t interaction = static_cast<int32_t>(
        (rawInteract >> 1) ^ static_cast<uint32_t>(-static_cast<int32_t>(rawInteract & 1))
    );
    if (interaction < 0 || interaction > 2) return false;
    out.append(data, start, pos - start);
    if (pos + 8 > data.size()) return false;
    out.append(data, pos, 8); // interact_rotation
    pos += 8;
    start = pos;
    uint64_t tick      = 0;
    int      tickShift = 0;
    bool     tickDone  = false;
    while (pos < data.size() && tickShift <= 63) {
        uint8_t byte = static_cast<uint8_t>(data[pos++]);
        tick |= static_cast<uint64_t>(byte & 0x7f) << tickShift;
        if ((byte & 0x80) == 0) {
            tickDone = true;
            break;
        }
        tickShift += 7;
    }
    if (!tickDone) return false;
    tickOut = tick;
    out.append(data, start, pos - start);
    if (pos + 12 > data.size()) return false;
    out.append(data, pos, 12); // delta
    pos += 12;
    if (lenient) {
        // block_action / item_stack_request 等可选载荷长度未知：放弃这些载荷，
        // 用当前帧的 input_data/modes/tick/delta + 帧尾 28 字节重建。
        if (pos + 28 > data.size()) return false;
        for (int i = 0; i < 5; ++i) out.push_back('\0'); // 2193 对应 option 字段 absent
        out.append(data, data.size() - 28, 28);
        return true;
    }
    for (int i = 0; i < 5; ++i) { // 2169 的 5 个 option<bool>：只接受 absent/false
        if (pos >= data.size()) return false;
        uint8_t hasValue = static_cast<uint8_t>(data[pos++]);
        if (hasValue == 1) {
            if (pos >= data.size()) return false;
            if (static_cast<uint8_t>(data[pos++]) != 0) return false;
        } else if (hasValue != 0) {
            return false;
        }
        out.push_back('\0'); // 2193 对应 option 字段 absent
    }
    if (pos + 28 != data.size()) return false; // analogue(8) + camera(12) + raw(8)
    out.append(data, pos, 28);
    return true;
}
#endif

// 临时兜底：unparseable 144 用上一帧合法 2193 内容 + 当前帧 position/rotation。
// 只解决位置冻结，block_action 仍会丢失；覆盖率表保持 RULE-LOSSY。
thread_local std::string gLastAuthInput2193;
thread_local uint32_t    gLastAuthInputSession = 0;
thread_local uint64_t    gLastAuthInputTick    = 0;

// 把 2193 player_auth_input 的 input_data 数组清空，其它字段原样保留。
// 用于 fallback：宁可这一帧没有按键状态，也不要把上一帧的 sneaking 等旧状态重复送进服务端。
bool clearAuthInputData(std::string& packet, size_t payloadOffset) {
    size_t   pos   = payloadOffset + 32;
    uint32_t count = 0;
    if (!ptlReadVarUInt(packet, pos, count)) return false;
    for (uint32_t i = 0; i < count; ++i) {
        if (!ptlSkipVarint(packet, pos)) return false;
    }
    std::string cleared = packet.substr(0, payloadOffset + 32);
    cleared.push_back('\0');
    cleared.append(packet, pos, packet.size() - pos);
    packet = std::move(cleared);
    return true;
}

// 2169 真机的 input_data 结构（抓包确认）：
//   varuint blobCount（通常 1）
//   每个 blob：varuint length + length 字节的 zigzag InputData 值
// 2193 要 array<InputData>：把所有 blob 的值按升序展平。
// 解析完全由长度前缀决定，不搜索宽度，同一帧重复解析结果一致。
bool tryPlayerAuthInputList(
    std::string const& data,
    size_t             payloadOffset,
    bool               lenient,
    std::string&       out,
    std::vector<int>&  values,
    uint64_t&          tickOut
) {
    size_t   pos       = payloadOffset + 32;
    uint32_t blobCount = 0;
    if (!ptlReadVarUInt(data, pos, blobCount)) return false;
    if (blobCount == 0 || blobCount > 8) return false;
    values.clear();
    for (uint32_t b = 0; b < blobCount; ++b) {
        uint32_t len = 0;
        if (!ptlReadVarUInt(data, pos, len)) return false;
        if (len > 256 || pos + len > data.size()) return false;
        size_t end = pos + len;
        while (pos < end) {
            uint32_t raw = 0;
            if (!ptlReadVarUInt(data, pos, raw)) return false;
            if (pos > end) return false;
            int32_t v = static_cast<int32_t>(
                (raw >> 1) ^ static_cast<uint32_t>(-static_cast<int32_t>(raw & 1))
            );
            if (v < 0 || v > 65) return false;
            if (v != 2 && v != 54 && v != 55) values.push_back(v);
        }
        if (pos != end) return false;
    }
    std::sort(values.begin(), values.end());
    values.erase(std::unique(values.begin(), values.end()), values.end());

    size_t modeStart = pos;
    uint32_t inputMode = 0, playMode = 0, rawInteract = 0;
    if (!ptlReadVarUInt(data, pos, inputMode) || inputMode > 4) return false;
    if (!ptlReadVarUInt(data, pos, playMode) || playMode > 9) return false;
    if (!ptlReadVarUInt(data, pos, rawInteract)) return false;
    int32_t interaction = static_cast<int32_t>(
        (rawInteract >> 1) ^ static_cast<uint32_t>(-static_cast<int32_t>(rawInteract & 1))
    );
    if (interaction < 0 || interaction > 2) return false;
    size_t modeEnd = pos;
    if (pos + 8 > data.size()) return false;
    size_t rotationStart = pos;
    pos += 8;
    size_t   tickStart = pos;
    uint64_t tick      = 0;
    int      shift     = 0;
    bool     tickDone  = false;
    while (pos < data.size() && shift <= 63) {
        uint8_t byte = static_cast<uint8_t>(data[pos++]);
        tick |= static_cast<uint64_t>(byte & 0x7f) << shift;
        if ((byte & 0x80) == 0) { tickDone = true; break; }
        shift += 7;
    }
    if (!tickDone) return false;
    tickOut = tick;
    size_t deltaStart = pos;
    if (pos + 12 > data.size()) return false;
    pos += 12;
    struct BlockActionEntry {
        int action = 0;
        int x = 0, y = 0, z = 0;
        int face = 0;
    };
    std::vector<BlockActionEntry> blockActions;
    bool                          hasBlockAction = false;
    auto readZig = [&](int& value) -> bool {
        uint32_t raw = 0;
        if (!ptlReadVarUInt(data, pos, raw)) return false;
        value = static_cast<int32_t>(
            (raw >> 1) ^ static_cast<uint32_t>(-static_cast<int32_t>(raw & 1))
        );
        return true;
    };
    if (!lenient) {
        for (int i = 0; i < 5; ++i) { // option 顺序：transaction / item_stack_request / block_action / vehicle / predicted
            if (pos >= data.size()) return false;
            uint8_t present = static_cast<uint8_t>(data[pos++]);
            if (present == 0) continue;
            if (present != 1) return false;
            if (i == 2) {
                // block_action：outerCount + innerCount + entries[action,pos,face]
                uint32_t outer = 0, inner = 0;
                if (!ptlReadVarUInt(data, pos, outer) || outer != 1) return false;
                if (!ptlReadVarUInt(data, pos, inner) || inner > 16) return false;
                for (uint32_t k = 0; k < inner; ++k) {
                    BlockActionEntry entry;
                    if (!readZig(entry.action)) return false;
                    if (entry.action < 0 || entry.action > 37) return false;
                    if (!readZig(entry.x) || !readZig(entry.y) || !readZig(entry.z)) return false;
                    if (!readZig(entry.face)) return false;
                    blockActions.push_back(entry);
                }
                hasBlockAction = true;
            } else {
                // 其余 option 在 2169 是 option<bool>：只接受 false。
                if (pos >= data.size() || static_cast<uint8_t>(data[pos++]) != 0) return false;
            }
        }
        if (pos + 28 != data.size()) return false;
    } else {
        // item_stack_request / vehicle 等载荷长度未知：丢弃该组，保留帧尾 28B。
        if (pos + 28 > data.size()) return false;
    }
    out = data.substr(0, payloadOffset + 32);
    ptlAppendVarUInt(out, static_cast<uint32_t>(values.size()));
    for (int v : values) ptlAppendVarUInt(out, static_cast<uint32_t>(v) * 2); // zigzag32
    out.append(data, modeStart, modeEnd - modeStart);
    out.append(data, rotationStart, 8);
    out.append(data, tickStart, deltaStart - tickStart);
    out.append(data, deltaStart, 12);
    auto appendZig = [&](int v) {
        uint32_t raw = (static_cast<uint32_t>(v) << 1) ^ static_cast<uint32_t>(v >> 31);
        ptlAppendVarUInt(out, raw);
    };
    out.push_back('\0'); // transaction
    out.push_back('\0'); // item_stack_request
    if (hasBlockAction) {
        out.push_back('\1');
        ptlAppendVarUInt(out, static_cast<uint32_t>(blockActions.size()));
        for (auto const& e : blockActions) {
            appendZig(e.action);
            appendZig(e.x);
            appendZig(e.y);
            appendZig(e.z);
            appendZig(e.face);
        }
    } else {
        out.push_back('\0');
    }
    out.push_back('\0'); // vehicle_rotation
    out.push_back('\0'); // predicted_vehicle
    out.append(data, data.size() - 28, 28);
    return true;
}

bool playerAuthInputTo2193(std::string& data, size_t payloadOffset) {
    if (!looksLike2169AuthInput(data) && validatePlayerAuthInputArray(data, payloadOffset)) {
        return true; // 已经是 2193 布局
    }
    static char const* kInputNames[66] = {
        "ascend", "descend", "north_jump", "jump_down", "sprint_down", "change_height",
        "jumping", "auto_jumping_in_water", "sneaking", "sneak_down", "up", "down",
        "left", "right", "up_left", "up_right", "want_up", "want_down",
        "want_down_slow", "want_up_slow", "sprinting", "ascend_block", "descend_block", "sneak_toggle_down",
        "persist_sneak", "start_sprinting", "stop_sprinting", "start_sneaking", "stop_sneaking",
        "start_swimming", "stop_swimming", "start_jumping", "start_gliding", "stop_gliding",
        "item_interact", "block_action", "item_stack_request", "handled_teleport", "emoting",
        "missed_swing", "start_crawling", "stop_crawling", "start_flying", "stop_flying",
        "received_server_data", "client_predicted_vehicle", "paddling_left", "paddling_right",
        "block_breaking_delay_enabled", "horizontal_collision", "vertical_collision", "down_left",
        "down_right", "start_using_item", "camera_relative_movement_enabled",
        "rot_controlled_by_move_direction", "start_spin_attack", "stop_spin_attack",
        "hotbar_only_touch", "jump_released_raw", "jump_pressed_raw", "jump_current_raw",
        "sneak_released_raw", "sneak_pressed_raw", "sneak_current_raw", "internal_update"
    };
    // 确定式解析：先严格（option 全 absent），失败再宽松（丢弃未知载荷）。
    std::string      out;
    std::vector<int> values;
    uint64_t         usedTick = 0;
    bool             strict   = tryPlayerAuthInputList(data, payloadOffset, false, out, values, usedTick);
    if (!strict) {
        values.clear();
        if (!tryPlayerAuthInputList(data, payloadOffset, true, out, values, usedTick)) out.clear();
    }
    int candidateCount = out.empty() ? 0 : 1;
    // 稳定性约束：同一会话的 tick 必须递增；不满足的候选整帧拒绝，避免宽度/位集抖动。
    uint32_t sessionNow = gRawC2sSession.load(std::memory_order_relaxed);
    bool     hasLast    = !gLastAuthInput2193.empty() && gLastAuthInputSession == sessionNow;
    if (hasLast && candidateCount > 0 && usedTick < gLastAuthInputTick) {
        // tick 倒退：该帧不可信，直接丢弃（不再沿用上一帧载荷）。
        candidateCount = 0;
        out.clear();
    }
    if (candidateCount == 0) {
        // 不受 64 条上限限制，保证无法解析的帧一定有原始 hex。
        if (rawDumpBudget(4096)) {
            static std::mutex mutex;
            std::lock_guard   lock(mutex);
            std::ofstream     out("plugins/JERCPLD/c2s_raw.log", std::ios::app);
            out << nowStamp() << " session=" << gRawC2sSession.load(std::memory_order_relaxed)
                << " id=144 UNPARSEABLE len=" << data.size() << " hex=";
            size_t limit = data.size() < 4096 ? data.size() : 4096;
            for (size_t i = 0; i < limit; ++i) {
                char buf[3]{};
                sprintf_s(buf, sizeof(buf), "%02x", static_cast<unsigned char>(data[i]));
                out << buf;
            }
            if (limit < data.size()) out << "...truncated";
            out << '\n';
        }
        uint32_t session = gRawC2sSession.load(std::memory_order_relaxed);
        (void)session;
        // 方案 A：宁可丢一帧（客户端会自动重发），也不沿用上一帧的 tick/option 载荷，
        // 否则会出现「position 冻结」和「挖两下 / 放两个」。
        gPtlDrop = true;
        hookLog(
            "[JERCPLD][PTL] auth_input unparseable: drop frame len={} (no fallback; raw hex in c2s_raw.log)",
            data.size()
        );
        return true;
    }
    static std::atomic_int loggedChosen{0};
    int                    chosenIndex = loggedChosen.fetch_add(1, std::memory_order_relaxed);
    if (chosenIndex < 200) {
        std::string names;
        for (int v : values) {
            if (!names.empty()) names += ',';
            names += kInputNames[v];
        }
        hookLog(
            "[JERCPLD][PTL] auth_input chosen tick={} opts={} values={} [{}]",
            usedTick,
            strict ? "keep" : "discarded",
            values.size(),
            names
        );
        if (chosenIndex < 30) {
            std::string hex;
            size_t      limit = data.size() < 160 ? data.size() : 160;
            hex.reserve(limit * 2);
            static char const* digits = "0123456789abcdef";
            for (size_t i = 0; i < limit; ++i) {
                unsigned char c = static_cast<unsigned char>(data[i]);
                hex.push_back(digits[c >> 4]);
                hex.push_back(digits[c & 0x0F]);
            }
            hookLog("[JERCPLD][PTL] auth_input raw len={} hex={}", data.size(), hex);
        }
    }
    gLastAuthInput2193    = out;
    gLastAuthInputSession = gRawC2sSession.load(std::memory_order_relaxed);
    gLastAuthInputTick    = usedTick;
    data                  = std::move(out);
    return true;
}

// serverbound_pack_setting_change：2193 新增 type=3(string_list)。
// 2169 只可能发送 0/1/2，且这三个枚举值与载荷结构未变；遇到 3 说明前提不成立。
bool packSettingChangeGuard(std::string& data, size_t payloadOffset) {
    size_t pos = payloadOffset;
    if (pos + 16 > data.size()) return false; // pack_id uuid
    pos += 16;
    std::string name;
    if (!ptlReadString(data, pos, name)) return false;
    uint32_t type = 0;
    if (!ptlReadVarUInt(data, pos, type)) return false;
    return type <= 2;
}

// move_entity_delta 的尾部 ticks(varint64)：从末尾回扫一个 varint 并删除。
bool stripTrailingVarint(std::string& data, size_t payloadOffset) {
    if (data.size() <= payloadOffset) return false;
    size_t end = data.size();
    if ((static_cast<uint8_t>(data[end - 1]) & 0x80) != 0) return false;
    size_t start = end - 1;
    while (start > payloadOffset && (static_cast<uint8_t>(data[start - 1]) & 0x80) != 0) {
        --start;
    }
    if (start <= payloadOffset) return false;
    data.erase(start, end - start);
    return true;
}

// dimension_data：2193(min_y,height_range,...,default_biome) -> 2169(max_height,min_height,...)
bool dimensionDataTo2169(std::string& data, size_t payloadOffset) {
    size_t   pos = payloadOffset;
    uint32_t count = 0;
    if (!ptlReadVarUInt(data, pos, count)) return false;
    std::string out = data.substr(0, payloadOffset);
    ptlAppendVarUInt(out, count);
    for (uint32_t i = 0; i < count; ++i) {
        std::string id;
        if (!ptlReadString(data, pos, id)) return false;
        ptlAppendString(out, id);
        int32_t minY = 0, heightRange = 0, generator = 0, dimensionType = 0;
        if (!ptlReadZigZag32(data, pos, minY) || !ptlReadZigZag32(data, pos, heightRange)
            || !ptlReadZigZag32(data, pos, generator) || !ptlReadZigZag32(data, pos, dimensionType))
            return false;
        if (pos + 16 > data.size()) return false;
        std::string packId = data.substr(pos, 16);
        pos += 16;
        std::string defaultBiome;
        if (!ptlReadString(data, pos, defaultBiome)) return false;
        ptlAppendZigZag32(out, minY + heightRange);
        ptlAppendZigZag32(out, minY);
        ptlAppendZigZag32(out, generator);
        ptlAppendZigZag32(out, dimensionType);
        out.append(packId);
    }
    if (pos != data.size()) return false;
    data = std::move(out);
    return true;
}

// dimension_data：2169(max_height,min_height,...) -> 2193(min_y,height_range,...,default_biome)
bool dimensionDataTo2193(std::string& data, size_t payloadOffset) {
    size_t   pos = payloadOffset;
    uint32_t count = 0;
    if (!ptlReadVarUInt(data, pos, count)) return false;
    std::string out = data.substr(0, payloadOffset);
    ptlAppendVarUInt(out, count);
    for (uint32_t i = 0; i < count; ++i) {
        std::string id;
        if (!ptlReadString(data, pos, id)) return false;
        ptlAppendString(out, id);
        int32_t maxHeight = 0, minHeight = 0, generator = 0, dimensionType = 0;
        if (!ptlReadZigZag32(data, pos, maxHeight) || !ptlReadZigZag32(data, pos, minHeight)
            || !ptlReadZigZag32(data, pos, generator) || !ptlReadZigZag32(data, pos, dimensionType))
            return false;
        if (pos + 16 > data.size()) return false;
        std::string packId = data.substr(pos, 16);
        pos += 16;
        ptlAppendZigZag32(out, minHeight);
        ptlAppendZigZag32(out, maxHeight - minHeight);
        ptlAppendZigZag32(out, generator);
        ptlAppendZigZag32(out, dimensionType);
        out.append(packId);
        char const* biome = "minecraft:plains";
        if (generator == 3) biome = "minecraft:nether_wastes";
        else if (generator == 4) biome = "minecraft:the_end";
        else if (generator == 5) biome = "minecraft:the_void";
        ptlAppendString(out, biome);
    }
    if (pos != data.size()) return false;
    data = std::move(out);
    return true;
}

// 内置结构性改写：按名字分发，forward 表示「旧 -> 新」。
bool ptlBuiltin(std::string& data, size_t payloadOffset, std::string const& name, bool olderToNewer) {
    if (name == "start_game_editor_checksum") {
        if (olderToNewer) {
            return insertStartGameEditorField(data, payloadOffset) && zeroBlockPaletteChecksum(data);
        }
        return removeStartGameEditorField(data, payloadOffset) && zeroBlockPaletteChecksum(data);
    }
    if (name == "start_game_zero_checksum") {
        // 2168/2169 与 2193 的 StartGame 布局一致，只需清调色板校验值。
        return zeroBlockPaletteChecksum(data);
    }
    if (name == "add_player_passenger") {
        if (olderToNewer) return insertAddPlayerPassengerField(data);
        return false; // TODO：反向需 2225 抓包
    }
    if (name == "level_chunk_skeleton") {
        if (olderToNewer) {
            if (promoteLevelChunkToSkeleton(data, payloadOffset)) return true;
            data.push_back('\0');
            return true;
        }
        return removeLevelChunkTrailingBool(data, payloadOffset);
    }
    if (name == "player_list_fab") {
        if (olderToNewer) return translatePlayerList(data, payloadOffset);
        return false; // TODO：反向需 2225 抓包
    }
    if (name == "client_cache_force_off") {
        // 两个方向都需要：2169/2225 客户端上报 true 时，2193 服务端只发缓存哈希，
        // 客户端缓存键与当前调色板不一致就会看到错误方块。
        disableClientCache(data);
        return true;
    }
    if (name == "camera_presets_strip") {
        if (olderToNewer) return false;
        return stripCameraPresetTrailingFields(data, payloadOffset);
    }
    if (name == "boss_event_to_2169") {
        if (olderToNewer) return false;
        return bossEventTo2169(data, payloadOffset);
    }
    if (name == "boss_event_to_2193") {
        if (!olderToNewer) return false;
        return bossEventTo2193(data, payloadOffset);
    }
    if (name == "play_sound_to_2169") {
        if (olderToNewer) return false;
        return playSoundTo2169(data, payloadOffset);
    }
    if (name == "play_sound_to_2193") {
        if (!olderToNewer) return false;
        return playSoundTo2193(data, payloadOffset);
    }
    if (name == "pack_setting_change_guard") {
        if (!olderToNewer) return false;
        return packSettingChangeGuard(data, payloadOffset);
    }
    if (name == "transaction_to_2169") {
        if (olderToNewer) return false;
        return transactionTo2169(data, payloadOffset);
    }
    if (name == "transaction_to_2193") {
        if (!olderToNewer) return false;
        return transactionTo2193(data, payloadOffset);
    }
    if (name == "player_auth_input_to_2193") {
        if (!olderToNewer) return false;
        return playerAuthInputTo2193(data, payloadOffset);
    }
    if (name == "item_stack_response_to_2169") {
        if (olderToNewer) return false;
        return itemStackResponseTo2169(data, payloadOffset);
    }
    if (name == "subchunk_to_2169") {
        if (olderToNewer) return false;
        return subchunkTo2169(data, payloadOffset);
    }
    if (name == "move_entity_delta_strip_ticks") {
        if (olderToNewer) return false;
        return stripTrailingVarint(data, payloadOffset);
    }
    if (name == "dimension_data_to_2169") {
        if (olderToNewer) return false;
        return dimensionDataTo2169(data, payloadOffset);
    }
    if (name == "dimension_data_to_2193") {
        if (!olderToNewer) return false;
        return dimensionDataTo2193(data, payloadOffset);
    }
    return false;
}

bool ptlApplyRules(
    std::string&              data,
    size_t                    payloadOffset,
    std::vector<PtlRule> const& rules,
    bool                      olderToNewer
) {
    for (auto const& rule : rules) {
        if (rule.type == "appendBytes") {
            if (olderToNewer) {
                data.append(rule.bytes);
            } else {
                if (data.size() < rule.bytes.size()) return false;
                if (data.compare(data.size() - rule.bytes.size(), rule.bytes.size(), rule.bytes) != 0) return false;
                data.erase(data.size() - rule.bytes.size());
            }
        } else if (rule.type == "insertField") {
            size_t pos = payloadOffset;
            if (!ptlRunSkip(data, pos, rule.skip)) return false;
            if (olderToNewer) {
                data.insert(pos, rule.bytes);
            } else {
                if (pos + rule.bytes.size() > data.size()) return false;
                if (data.compare(pos, rule.bytes.size(), rule.bytes) != 0) return false;
                data.erase(pos, rule.bytes.size());
            }
        } else if (rule.type == "enumRemap") {
            size_t pos = payloadOffset;
            if (!ptlRunSkip(data, pos, rule.skip)) return false;
            if (pos >= data.size()) return false;
            uint8_t value = static_cast<uint8_t>(data[pos]);
            auto    it    = rule.remap.find(value);
            if (it != rule.remap.end()) data[pos] = static_cast<char>(it->second);
        } else if (rule.type == "zeroFixed") {
            size_t pos = payloadOffset;
            if (!ptlRunSkip(data, pos, rule.skip)) return false;
            if (rule.zeros <= 0 || pos + static_cast<size_t>(rule.zeros) > data.size()) return false;
            std::fill_n(data.begin() + static_cast<std::ptrdiff_t>(pos), rule.zeros, '\0');
        } else if (rule.type == "builtin") {
            if (!ptlBuiltin(data, payloadOffset, rule.builtin, olderToNewer)) return false;
        } else {
            return false;
        }
    }
    return true;
}

// builtin 名字写错时必须在加载期报错，否则规则语法合法、静默失效，只在运行期表现为「规则失败」。
bool isKnownBuiltin(std::string const& name) {
    static constexpr char const* kBuiltins[] = {
        "start_game_editor_checksum", "start_game_zero_checksum", "add_player_passenger",
        "level_chunk_skeleton",       "player_list_fab",           "client_cache_force_off",
        "camera_presets_strip",       "boss_event_to_2169",        "boss_event_to_2193",
        "play_sound_to_2169",         "play_sound_to_2193",        "pack_setting_change_guard",
        "transaction_to_2169",        "transaction_to_2193",       "player_auth_input_to_2193",
        "item_stack_response_to_2169","subchunk_to_2169",          "move_entity_delta_strip_ticks",
        "dimension_data_to_2169",     "dimension_data_to_2193",
    };
    for (auto candidate : kBuiltins) {
        if (name == candidate) return true;
    }
    return false;
}

bool ptlParseRules(nlohmann::json const& arr, std::vector<PtlRule>& out, std::string& error) {
    if (!arr.is_array()) {
        error = "rules must be an array";
        return false;
    }
    for (auto const& item : arr) {
        if (!item.is_object()) {
            error = "rule must be an object";
            return false;
        }
        PtlRule rule;
        rule.type    = item.value("type", std::string{});
        rule.addedIn = item.value("addedIn", 0);
        rule.zeros   = item.value("zeros", 0);
        rule.builtin = item.value("name", std::string{});
        if (item.contains("skip")) {
            if (!item["skip"].is_array()) {
                error = "skip must be an array";
                return false;
            }
            for (auto const& token : item["skip"]) {
                if (!token.is_string()) {
                    error = "skip token must be a string";
                    return false;
                }
                rule.skip.push_back(token.get<std::string>());
            }
        }
        if (item.contains("bytes")) {
            if (!item["bytes"].is_string() || !ptlDecodeHex(item["bytes"].get<std::string>(), rule.bytes)) {
                error = "invalid bytes hex";
                return false;
            }
        }
        if (item.contains("map")) {
            if (!item["map"].is_object()) {
                error = "map must be an object";
                return false;
            }
            for (auto const& [k, v] : item["map"].items()) {
                try {
                    rule.remap[std::stoi(k)] = v.get<int>();
                } catch (...) {
                    error = "invalid map entry";
                    return false;
                }
            }
        }
        if (rule.type == "builtin" && rule.builtin.empty()) {
            error = "builtin requires name";
            return false;
        }
        if (rule.type == "builtin" && !isKnownBuiltin(rule.builtin)) {
            error = "unknown builtin: " + rule.builtin;
            return false;
        }
        if (rule.type != "builtin" && rule.type != "appendBytes" && rule.type != "insertField"
            && rule.type != "enumRemap" && rule.type != "zeroFixed" && rule.type != "drop") {
            error = "unknown rule type: " + rule.type;
            return false;
        }
        out.push_back(std::move(rule));
    }
    return true;
}

bool ptlParsePairJson(std::string const& name, nlohmann::json const& json, PtlPair& pair, std::string& error) {
    pair.api  = json.value("ptlApi", 0);
    pair.file = name;
    if (pair.api != 1) {
        error = fmt::format("unsupported ptlApi {} (需要 1)", pair.api);
        return false;
    }
    if (!json.contains("pair") || !json["pair"].is_array() || json["pair"].size() != 2) {
        error = "pair must be [a, b]";
        return false;
    }
    pair.a = json["pair"][0].get<int>();
    pair.b = json["pair"][1].get<int>();
    if (pair.a <= 0 || pair.b <= 0 || pair.a == pair.b) {
        error = "invalid pair";
        return false;
    }
    if (json.contains("serverVersions")) {
        for (auto const& v : json["serverVersions"]) pair.serverVersions.push_back(v.get<std::string>());
    }
    if (!json.contains("packets") || !json["packets"].is_object()) {
        error = "packets must be an object";
        return false;
    }
    for (auto const& [key, value] : json["packets"].items()) {
        int packetId = 0;
        try {
            packetId = std::stoi(key);
        } catch (...) {
            error = "invalid packet id: " + key;
            return false;
        }
        PtlPacketRules rules;
        rules.name = value.value("name", std::string{});
        if (value.contains("rules") && !ptlParseRules(value["rules"], rules.rules, error)) return false;
        if (value.contains("overrides")) {
            auto const& ov = value["overrides"];
            if (ov.contains("olderToNewer") && !ptlParseRules(ov["olderToNewer"], rules.olderToNewer, error))
                return false;
            if (ov.contains("newerToOlder") && !ptlParseRules(ov["newerToOlder"], rules.newerToOlder, error))
                return false;
        }
        pair.packets.emplace(packetId, std::move(rules));
    }
    return true;
}

bool ptlLoadJsonText(std::string const& name, std::string const& text, PtlPair& pair, std::string& error) {
    nlohmann::json json;
    try {
        json = nlohmann::json::parse(text);
    } catch (std::exception const& e) {
        error = std::string("json parse: ") + e.what();
        return false;
    }
    return ptlParsePairJson(name, json, pair, error);
}

bool ptlLoadFile(std::filesystem::path const& path, PtlPair& pair, std::string& error) {
    std::ifstream in(path);
    if (!in) {
        error = "cannot open";
        return false;
    }
    std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    return ptlLoadJsonText(path.filename().string(), text, pair, error);
}

// 单文件翻译模块：plugins/JERCPLD/PTL/JERCPLD.PTL.dll
// 只经 C ABI 读取内嵌规则，解析后立即 FreeLibrary，运行期不持有模块句柄。
constexpr uint32_t kPtlModuleApiVersion = 1;

using PtlModuleApiVersionFn = uint32_t (*)();
using PtlModulePairCountFn  = uint32_t (*)();
using PtlModulePairNameFn   = char const* (*)(uint32_t);
using PtlModulePairRulesFn  = char const* (*)(uint32_t);
using PtlModuleSelfCheckFn  = int (*)();

bool ptlLoadModule(std::vector<PtlPair>& loaded) {
    namespace fs = std::filesystem;
    fs::path        modulePath = fs::path("plugins/JERCPLD/PTL") / "JERCPLD.PTL.dll";
    std::error_code ec;
    if (!fs::exists(modulePath, ec)) return false;

    HMODULE module = LoadLibraryW(modulePath.wstring().c_str());
    if (!module) {
        hookLog("[JERCPLD][PTL] 拒绝加载模块 {}: LoadLibrary 失败 error={}", modulePath.string(), GetLastError());
        modLog("[JERCPLD][PTL] 拒绝加载模块 {}: LoadLibrary 失败 error={}", modulePath.string(), GetLastError());
        return false;
    }

    auto apiVersion = reinterpret_cast<PtlModuleApiVersionFn>(GetProcAddress(module, "jercpld_ptl_api_version"));
    auto pairCount  = reinterpret_cast<PtlModulePairCountFn>(GetProcAddress(module, "jercpld_ptl_pair_count"));
    auto pairName   = reinterpret_cast<PtlModulePairNameFn>(GetProcAddress(module, "jercpld_ptl_pair_name"));
    auto pairRules  = reinterpret_cast<PtlModulePairRulesFn>(GetProcAddress(module, "jercpld_ptl_pair_rules"));
    auto selfCheck  = reinterpret_cast<PtlModuleSelfCheckFn>(GetProcAddress(module, "jercpld_ptl_self_check"));

    bool ok = true;
    if (!apiVersion || !pairCount || !pairName || !pairRules || !selfCheck) {
        hookLog("[JERCPLD][PTL] 拒绝加载模块 {}: 导出缺失", modulePath.string());
        modLog("[JERCPLD][PTL] 拒绝加载模块 {}: 导出缺失", modulePath.string());
        ok = false;
    } else if (apiVersion() != kPtlModuleApiVersion) {
        hookLog(
            "[JERCPLD][PTL] 拒绝加载模块 {}: API 版本 {} != {}",
            modulePath.string(), apiVersion(), kPtlModuleApiVersion
        );
        modLog(
            "[JERCPLD][PTL] 拒绝加载模块 {}: API 版本 {} != {}",
            modulePath.string(), apiVersion(), kPtlModuleApiVersion
        );
        ok = false;
    } else if (selfCheck() != 0) {
        hookLog("[JERCPLD][PTL] 拒绝加载模块 {}: 自检失败 code={}", modulePath.string(), selfCheck());
        modLog("[JERCPLD][PTL] 拒绝加载模块 {}: 自检失败 code={}", modulePath.string(), selfCheck());
        ok = false;
    } else if (pairCount() == 0) {
        hookLog("[JERCPLD][PTL] 拒绝加载模块 {}: 规则对数量为 0", modulePath.string());
        modLog("[JERCPLD][PTL] 拒绝加载模块 {}: 规则对数量为 0", modulePath.string());
        ok = false;
    }

    if (ok) {
        uint32_t count = pairCount();
        for (uint32_t i = 0; i < count; ++i) {
            char const* name  = pairName(i);
            char const* rules = pairRules(i);
            if (!name || !rules) {
                hookLog("[JERCPLD][PTL] 拒绝模块 {} 的第 {} 个规则对: 空指针", modulePath.string(), i);
                modLog("[JERCPLD][PTL] 拒绝模块 {} 的第 {} 个规则对: 空指针", modulePath.string(), i);
                ok = false;
                break;
            }
            PtlPair     pair;
            std::string error;
            std::string source = "JERCPLD.PTL.dll:" + std::string(name);
            if (!ptlLoadJsonText(source, rules, pair, error)) {
                hookLog("[JERCPLD][PTL] 拒绝模块 {} 的规则对 {}: {}", modulePath.string(), name, error);
                modLog("[JERCPLD][PTL] 拒绝模块 {} 的规则对 {}: {}", modulePath.string(), name, error);
                ok = false;
                break;
            }
            hookLog("[JERCPLD][PTL] 已加载模块规则 {} pair={}-{} packets={}", source, pair.a, pair.b, pair.packets.size());
            loaded.push_back(std::move(pair));
        }
    }

    FreeLibrary(module);
    if (!ok) loaded.clear();
    return ok && !loaded.empty();
}

void ptlLoadAll() {
    namespace fs = std::filesystem;
    std::vector<PtlPair> loaded;
    fs::path             dir = "plugins/JERCPLD/PTL";
    std::error_code      ec;
    bool                 fromModule = ptlLoadModule(loaded);
    if (fromModule) {
        hookLog("[JERCPLD][PTL] 规则来源=模块 JERCPLD.PTL.dll");
        modLog("[JERCPLD][PTL] 规则来源=模块 JERCPLD.PTL.dll");
    } else {
        // 模块不存在或无效时，退回 JSON 文件（开发/兼容模式）。
        // 模块存在但加载失败时不得让服务端退出，翻译能力降级为空。
        hookLog("[JERCPLD][PTL] 模块不可用，尝试 JSON 文件兼容模式");
        modLog("[JERCPLD][PTL] 模块不可用，尝试 JSON 文件兼容模式");
        if (fs::exists(dir, ec)) {
            for (auto const& entry : fs::directory_iterator(dir, ec)) {
                if (!entry.is_regular_file()) continue;
                if (entry.path().extension() != ".json") continue;
                PtlPair     pair;
                std::string error;
                if (!ptlLoadFile(entry.path(), pair, error)) {
                    hookLog("[JERCPLD][PTL] 拒绝加载 {}: {}", entry.path().filename().string(), error);
                    modLog("[JERCPLD][PTL] 拒绝加载 {}: {}", entry.path().filename().string(), error);
                    continue;
                }
                hookLog(
                    "[JERCPLD][PTL] 已加载 {} pair={}-{} packets={}",
                    pair.file, pair.a, pair.b, pair.packets.size()
                );
                loaded.push_back(std::move(pair));
            }
        }
    }
    std::unique_lock lock(gPtlMutex);
    gPtlPairs   = std::move(loaded);
    gPtlLoaded  = true;
    hookLog("[JERCPLD][PTL] 规则对数量={}", gPtlPairs.size());
}

PtlPair const* ptlFindPair(int from, int to) {
    for (auto const& pair : gPtlPairs) {
        if (pair.a == from && pair.b == to) return &pair;
        if (pair.b == from && pair.a == to) return &pair;
    }
    return nullptr;
}

struct PtlHop {
    int from = 0;
    int to   = 0;
};

// 按已注册版本对建图，BFS 找最短路径；超过 3 档直接拒绝。
bool ptlFindPath(int from, int to, std::vector<PtlHop>& path, std::string& error) {
    if (from == to) return true;
    std::vector<int>              queue{from};
    std::unordered_map<int, int>  prev;
    std::unordered_set<int>       seen{from};
    prev[from] = -1;
    while (!queue.empty()) {
        int current = queue.front();
        queue.erase(queue.begin());
        for (auto const& pair : gPtlPairs) {
            int next = 0;
            if (pair.a == current) next = pair.b;
            else if (pair.b == current) next = pair.a;
            else continue;
            if (seen.contains(next)) continue;
            seen.insert(next);
            prev[next] = current;
            if (next == to) {
                std::vector<int> chain;
                for (int node = to; node != -1; node = prev[node]) chain.push_back(node);
                std::reverse(chain.begin(), chain.end());
                if (chain.size() - 1 > 3) {
                    error = fmt::format("路径超过 3 档（{} 档）", chain.size() - 1);
                    return false;
                }
                for (size_t i = 0; i + 1 < chain.size(); ++i) path.push_back({chain[i], chain[i + 1]});
                return true;
            }
            queue.push_back(next);
        }
    }
    error = fmt::format("缺少 {} -> {} 的规则对", from, to);
    return false;
}

// [CONN] 行使用的路由摘要：hops=1 或 hops=2 [2168->2169,2169->2193]。
std::string ptlRouteSummary(int from, int to) {
    if (from == to) return "hops=0 [same]";
    std::vector<PtlHop> path;
    std::string         error;
    std::shared_lock    lock(gPtlMutex);
    if (!ptlFindPath(from, to, path, error)) return fmt::format("no-route({})", error);
    if (path.size() == 1) return "hops=1";
    std::string hops;
    for (auto const& hop : path) {
        if (!hops.empty()) hops += ',';
        hops += fmt::format("{}->{}", hop.from, hop.to);
    }
    return fmt::format("hops={} [{}]", path.size(), hops);
}

void ptlTranslate(std::string& data, int from, int to, char const* tag) {
    gPtlDrop = false;
    if (from == to) return;
    std::vector<PtlHop> path;
    std::string         error;
    {
        std::shared_lock lock(gPtlMutex);
        if (gPtlPairs.empty()) return;
        if (!ptlFindPath(from, to, path, error)) {
            hookLog("[JERCPLD][PTL][{}] 路由失败: {}", tag, error);
            return;
        }
        {
            // 每条 (方向, 起止) 只记录一次，用于确认链式路由实际走了哪些档。
            static std::mutex                      routeLogMutex;
            static std::unordered_set<std::string> loggedRoutes;
            std::string                            key = fmt::format("{}:{}->{}", tag, from, to);
            bool                                   shouldLog = false;
            {
                std::lock_guard<std::mutex> lock(routeLogMutex);
                shouldLog = loggedRoutes.insert(key).second;
            }
            if (shouldLog) {
                std::string hops;
                for (auto const& hop : path) {
                    if (!hops.empty()) hops += ", ";
                    hops += fmt::format("{}->{}", hop.from, hop.to);
                }
                hookLog("[JERCPLD][PTL][{}] route {}->{} hops={} [{}]", tag, from, to, path.size(), hops);
            }
        }
        for (auto const& hop : path) {
            PtlPair const* pair = ptlFindPair(hop.from, hop.to);
            if (!pair) return;
            size_t   offset   = 0;
            uint32_t packetId = readVarUInt(data, offset);
            auto     it       = pair->packets.find(static_cast<int>(packetId));
            if (it == pair->packets.end()) continue;
            if (it->second.disabled.load(std::memory_order_acquire)) {
                // 规则已被自检判死：该包的格式必然不匹配，继续下发只会让客户端 BadPacket。
                static std::mutex                      logMutex;
                static std::unordered_set<std::string> loggedDisabled;
                std::string                            key = fmt::format("{}:{}", tag, packetId);
                bool                                   first = false;
                {
                    std::lock_guard<std::mutex> guard(logMutex);
                    first = loggedDisabled.insert(key).second;
                }
                if (first) {
                    hookLog(
                        "[JERCPLD][PTL][{}] packet={}({}) 规则已禁用，丢弃后续同包",
                        tag, packetId, it->second.name
                    );
                }
                gPtlDrop    = true;
                gPtlRuleHit = true;
                return;
            }
            bool olderToNewer = (hop.from == pair->a);
            std::vector<PtlRule> effective = it->second.rules;
            if (olderToNewer) {
                effective.insert(effective.end(), it->second.olderToNewer.begin(), it->second.olderToNewer.end());
            } else {
                effective.insert(effective.end(), it->second.newerToOlder.begin(), it->second.newerToOlder.end());
            }
            bool drop = false;
            for (auto const& rule : effective) {
                if (rule.type == "drop") {
                    drop = true;
                    break;
                }
            }
            if (drop) {
                gPtlDrop    = true;
                gPtlRuleHit = true;
                hookLog(
                    "[JERCPLD][PTL][{}] packet={}({}) {}->{} dropped",
                    tag, packetId, it->second.name, hop.from, hop.to
                );
                return;
            }
            std::string original = data;
            gPtlRuleHit          = true;
            if (!ptlApplyRules(data, offset, effective, olderToNewer)) {
                // fail-closed：已知需要改写的包改不动时直接丢弃，不能把服务端格式原样发给新客户端，
                // 否则客户端会以 BadPacket(90) 断开且现场极难定位。
                data     = std::move(original);
                gPtlDrop = true;
                hookLog(
                    "[JERCPLD][PTL][{}] packet={}({}) 规则失败，已丢弃（{}->{}）",
                    tag, packetId, it->second.name, hop.from, hop.to
                );
                return;
            }
            {
                static std::atomic<unsigned> logged{0};
                unsigned                     bit = 1u << (packetId & 15u);
                if ((logged.fetch_or(bit, std::memory_order_relaxed) & bit) == 0) {
                    hookLog(
                        "[JERCPLD][PTL][{}] packet={}({}) {}->{} len {}->{}",
                        tag, packetId, it->second.name, hop.from, hop.to, original.size(), data.size()
                    );
                }
            }
            if (!it->second.validated.load(std::memory_order_relaxed) && !it->second.rules.empty()) {
                // 对称规则首次使用时做 A->B->A 自检，失败即禁用该包规则。
                std::string check = original;
                if (ptlApplyRules(check, offset, it->second.rules, true)
                    && ptlApplyRules(check, offset, it->second.rules, false)
                    && check == original) {
                    it->second.validated.store(true, std::memory_order_release);
                } else {
                    it->second.disabled.store(true, std::memory_order_release);
                    data                = std::move(original);
                    gPtlDrop            = true;
                    hookLog(
                        "[JERCPLD][PTL][{}] packet={} 往返校验失败，规则已禁用且本包丢弃",
                        tag,
                        packetId
                    );
                    return;
                }
            }
        }
    }
}

void dispatchOutboundTranslation(std::string& data, int clientProtocol, int connSlot) {
    gPtlDrop    = false;
    gPtlRuleHit = false;
    size_t   perfIn = data.size();
    uint32_t perfId = 0;
    {
        size_t off = 0;
        perfId     = readVarUInt(data, off);
    }
    uint64_t perfT0 = perfNowNs();
    int         server = gServerProtocol.load(std::memory_order_relaxed);
    std::string before = data;
    ptlTranslate(data, server, clientProtocol, "s2c");
    // 2170 以下的老客户端分支（26.45 及更老）非官方维护，仅作社区示例保留；
    // acceptMinProtocol 默认 2193，运行时不会进入该分支。
    if (data == before && clientProtocol < 2168) {
        translateOutboundPacket(data, clientProtocol);
    }
    perfRecord(0, clientProtocol, connSlot, perfId, perfIn, data.size(), gPtlRuleHit, perfNowNs() - perfT0);
}

void appendVarUInt(std::string& out, size_t value) {
    while (value >= 0x80) {
        out.push_back(static_cast<char>((value & 0x7F) | 0x80));
        value >>= 7;
    }
    out.push_back(static_cast<char>(value));
}

// 出站缓冲区在 CompressedNetworkPeer::sendPacket 处已经是「打包后」的形式：
// 若干 [varint len][packet body] 串联（单包时就是 [len][body]，实测 NetworkSettings
// 为 0c 8f 01 ...）。旧实现把长度前缀当成 packet id，25 字节的 StartGame 因此
// 永远匹配不到规则。这里先严格校验批格式，失败则按单个裸包处理。
bool splitOutboundBatch(std::string const& data, std::vector<std::pair<size_t, size_t>>& spans) {
    size_t pos = 0;
    while (pos < data.size()) {
        size_t   bodyOff = pos;
        uint32_t len     = readVarUInt(data, pos);
        if (len == 0 || pos + len > data.size()) return false;
        spans.emplace_back(pos, len);
        pos += len;
    }
    return pos == data.size() && !spans.empty();
}

void captureOutboundBatch(std::string const& data, void const* sessionKey) {
    std::vector<std::pair<size_t, size_t>> spans;
    if (splitOutboundBatch(data, spans)) {
        for (auto const& [off, len] : spans) {
            capturePacket("s2c", sessionKey, data.substr(off, len));
        }
        return;
    }
    capturePacket("s2c", sessionKey, data);
}

// 逐包翻译后重新写长度前缀；被规则丢弃的包不进输出。
void dispatchOutboundBatch(std::string& data, int clientProtocol, int connSlot, void const* sessionKey) {
    std::vector<std::pair<size_t, size_t>> spans;
    if (!splitOutboundBatch(data, spans)) {
        dispatchOutboundTranslation(data, clientProtocol, connSlot);
        if (!gPtlDrop) capturePacket("s2c", sessionKey, data);
        return;
    }
    std::string out;
    out.reserve(data.size() + 16);
    for (auto const& [off, len] : spans) {
        std::string body = data.substr(off, len);
        dispatchOutboundTranslation(body, clientProtocol, connSlot);
        if (gPtlDrop) continue;
        capturePacket("s2c", sessionKey, body);
        appendVarUInt(out, body.size());
        out += body;
    }
    data.swap(out);
}

// /jercpld reload 需要 LeviLamina 命令 API，而它依赖 boost.pfr（本机无该头）。
// 在补齐依赖前改用「PTL 目录 mtime 变化 + 无在线会话」时自动重载，
// 安全门禁与命令一致：有会话在线就只记录日志，不切换规则。
std::filesystem::file_time_type gPtlMtime{};

std::filesystem::file_time_type ptlDirMtime() {
    namespace fs = std::filesystem;
    std::error_code ec;
    fs::path        dir = "plugins/JERCPLD/PTL";
    if (!fs::exists(dir, ec)) return {};
    auto newest = fs::file_time_type{};
    for (auto const& entry : fs::directory_iterator(dir, ec)) {
        if (!entry.is_regular_file()) continue;
        auto ext = entry.path().extension();
        // 规则可能是 JSON，也可能是独立的 PTL 模块 dll，两者都要参与 mtime 判定。
        if (ext != ".json" && ext != ".dll") continue;
        auto t = entry.last_write_time(ec);
        if (t > newest) newest = t;
    }
    return newest;
}

void maybeAutoReloadPtl() {
    static std::atomic_uint counter{0};
    if ((counter.fetch_add(1, std::memory_order_relaxed) & 255U) != 0) return;
    auto now = ptlDirMtime();
    if (now == std::filesystem::file_time_type{}) return;
    if (gPtlMtime == std::filesystem::file_time_type{}) {
        gPtlMtime = now;
        return;
    }
    if (now == gPtlMtime) return;
    if (gActiveSessions.load(std::memory_order_relaxed) > 0) {
        hookLog("[JERCPLD][PTL] 检测到规则变更，但有会话在线，暂不重载");
        return;
    }
    gPtlMtime = now;
    ptlLoadAll();
    hookLog("[JERCPLD][PTL] 规则已自动重载（无在线会话）");
}

void dispatchInboundTranslation(std::string& data, int clientProtocol, int connSlot) {
    gPtlDrop    = false;
    gPtlRuleHit = false;
    size_t   perfIn = data.size();
    uint32_t perfId = 0;
    {
        size_t off = 0;
        perfId     = readVarUInt(data, off);
    }
    uint64_t perfT0 = perfNowNs();
    maybeAutoReloadPtl();
    int server = gServerProtocol.load(std::memory_order_relaxed);
    ptlTranslate(data, clientProtocol, server, "c2s");
    perfRecord(1, clientProtocol, connSlot, perfId, perfIn, data.size(), gPtlRuleHit, perfNowNs() - perfT0);
}

void logSendHead(char const* tag, std::string const& data);

// ============================================================================
// RakNet spike（默认关闭；只在独立测试实例上开启，现网不动）
// 目的：确认 nethernet 模式下 getRakPeer() 返回的 peer 是否已启动、
// 能否在其上再起一个 RakNet 监听。级别 1 只做观测与一次 Startup 尝试。
// ============================================================================
class ProbeNetworkPeer final : public NetworkPeer {
    std::atomic_bool mHasPacket{true};
    std::string      mPacket;

public:
    ProbeNetworkPeer() : mPacket("\xfe\x06\xc1\x01\x00\x00\x08\x79", 8) {}

    void sendPacket(std::string const&, Reliability, Compressibility) override {}

    [[nodiscard]] NetworkStatus getNetworkStatus() const override { return NetworkStatus{}; }

    [[nodiscard]] DataStatus
    _receivePacket(std::string& out, std::shared_ptr<std::chrono::steady_clock::time_point> const&) override {
        if (mHasPacket.exchange(false, std::memory_order_acq_rel)) {
            out = mPacket;
            return DataStatus::HasData;
        }
        return DataStatus::NoData;
    }

    [[nodiscard]] bool isLocal() const override { return false; }
    [[nodiscard]] bool isEncrypted() const override { return false; }
    [[nodiscard]] bool isLan() const override { return false; }
};

void runConnectionProbe() {
    auto nsRef = ll::service::bedrock::getNetworkSystem();
    if (!nsRef) {
        hookLog("[CONNPROBE] getNetworkSystem() = null");
        return;
    }
    NetworkIdentifier id{};
    id.mType = NetworkIdentifier::Type::RakNet;
    id.mGuid = RakNet::RakNetGUID{0x1122334455667788ULL, 0};
    RakNet::SystemAddress sa{};
    auto* raw = reinterpret_cast<unsigned char*>(&sa);
    raw[0] = 0x02; // AF_INET
    raw[1] = 0x00;
    raw[2] = 0x4A; // 19132 network byte order
    raw[3] = 0xBC;
    raw[4] = 127;
    raw[5] = 0;
    raw[6] = 0;
    raw[7] = 1;
    sa.debugPort  = 19132;
    sa.systemIndex = 0;
    std::memcpy(&id.mSock, &sa, sizeof(sa));

    auto peer = std::make_shared<ProbeNetworkPeer>();
    bool ok   = nsRef->onNewIncomingConnection(id, std::move(peer));
    auto* conn = nsRef->getConnectionFromId(id);
    hookLog("[CONNPROBE] onNewIncoming={} conn={}", ok, static_cast<void*>(conn));
    modLog("[JERCPLD][CONNPROBE] onNewIncoming={} conn={}", ok, static_cast<void*>(conn));
}

void runSymProbe() {
    auto resolve = [](char const* name) { return ll::memory::SymbolView(name).resolve(true); };
    void* onNew = resolve(
        "?$onNewIncomingConnection@NetworkSystem@@QEAA_NAEBVNetworkIdentifier@@$$QEAV?$shared_ptr@VNetworkPeer@@@std@@@Z"
    );
    void* ctor = resolve(
        "?$ctor@NetworkConnection@@QEAAPEAXAEBVNetworkIdentifier@@V?$shared_ptr@VNetworkPeer@@@std@@_NV?$NonOwnerPointer@VIPacketObserver@@@Bedrock@@AEAVScheduler@@2AEBUNetworkSettingOptions@@V?$shared_ptr@VIPacketSecurityController@@@4@@Z"
    );
    void* getConn = resolve(
        "?getConnectionFromId@NetworkSystem@@QEAAPEAVNetworkConnection@@AEBVNetworkIdentifier@@@Z"
    );
    hookLog(
        "[SYMPROBE] onNewIncomingConnection={} NetworkConnection_ctor={} getConnectionFromId={}",
        onNew,
        ctor,
        getConn
    );
    modLog("[JERCPLD][SYMPROBE] onNewIncoming={} ctor={} getConnection={}", onNew, ctor, getConn);
}

void runRaknetSpike(int port, int maxConnections) {
    hookLog("[SPIKE][L1] begin port={} max={}", port, maxConnections);
    auto peerRef = ll::service::bedrock::getRakPeer();
    if (!peerRef) {
        hookLog("[SPIKE][L1] getRakPeer() = null（没有可用的 RakPeer 对象）");
        return;
    }
    RakNet::RakPeer* peer = &*peerRef;
    hookLog("[SPIKE][L1] peer={} active={}", static_cast<void*>(peer), peer->IsActive());

    unsigned count = peer->GetNumberOfAddresses();
    hookLog("[SPIKE][L1] bound addresses={}", count);
    for (unsigned i = 0; i < count && i < 4; ++i) {
        RakNet::SystemAddress sa = peer->GetMyBoundAddress(static_cast<int>(i));
        unsigned char const* raw = reinterpret_cast<unsigned char const*>(&sa.address);
        hookLog(
            "[SPIKE][L1] bound[{}] familyBytes={:02x}{:02x} portBE={:02x}{:02x} debugPort={} idx={}",
            i, raw[0], raw[1], raw[2], raw[3], sa.debugPort, sa.systemIndex
        );
    }

    if (peer->IsActive()) {
        hookLog("[SPIKE][L1] peer 已处于 active，拒绝二次 Startup（避免影响现网）");
        return;
    }

    // SocketDescriptor 的构造符号未导出，这里用零初始化内存 + 端口放在 offset 0。
    alignas(16) unsigned char storage[64]{};
    *reinterpret_cast<uint16_t*>(storage) = static_cast<uint16_t>(port);
    auto* descriptor = reinterpret_cast<RakNet::SocketDescriptor*>(storage);
    auto  result     = peer->Startup(
        static_cast<unsigned>(maxConnections),
        descriptor,
        1,
        0
    );
    hookLog("[SPIKE][L1] Startup result={}", static_cast<int>(result));
    if (static_cast<int>(result) == 0) {
        peer->SetMaximumIncomingConnections(static_cast<unsigned short>(maxConnections));
        hookLog(
            "[SPIKE][L1] after startup active={} addresses={}",
            peer->IsActive(),
            peer->GetNumberOfAddresses()
        );
    }
}


void encryptedSendPacketDetour(
    EncryptedNetworkPeer*     self,
    std::string const&        data,
    NetworkPeer::Reliability  reliability,
    Compressibility           compressible
) {
    auto origin = reinterpret_cast<EncryptedSendPacketFn>(gOriginalEncryptedSendPacket);
    if (!origin) return;
    if (!gTranslationEnabled.load(std::memory_order_relaxed)) {
        origin(self, data, reliability, compressible);
        return;
    }
    static std::atomic_int sSendLogCount{0};
    if (sSendLogCount.fetch_add(1, std::memory_order_relaxed) < 4) {
        logSendHead("encrypted", data);
    }
    origin(self, data, reliability, compressible);
}

using PeerSendFn = void (*)(void*, std::string const&, NetworkPeer::Reliability, Compressibility);

void logSendHead(char const* tag, std::string const& data) {
    std::string hex;
    for (size_t i = 0; i < data.size() && i < 12; ++i) {
        char buf[4]{};
        sprintf_s(buf, sizeof(buf), "%02x ", static_cast<unsigned char>(data[i]));
        hex += buf;
    }
    hookLog("[JERCPLD] outbound {} send len={} head={}", tag, data.size(), hex);
}

void compressedSendDetour(
    void*                    self,
    std::string const&       data,
    NetworkPeer::Reliability reliability,
    Compressibility          compressible
) {
    static std::atomic_int logCount{0};
    if (logCount.fetch_add(1, std::memory_order_relaxed) < 8) logSendHead("compressed", data);
    auto origin = reinterpret_cast<PeerSendFn>(gOriginalCompressedSendPacket);
    if (origin) origin(self, data, reliability, compressible);
}

// nethernet 下出站链路是 CompressedNetworkPeer -> EncryptedNetworkPeer -> NetherNet，
// 既没有 BatchedNetworkPeer，也没有可解析的 CompressedNetworkPeer::$sendPacket 符号。
// 因此从连接实例的 vtable 取 sendPacket 地址后挂钩。这里拿到的是压缩前的裸包
// （varint packetId + 字段），与 batched 层语义一致，可直接复用同一套 s2c 翻译。
void compressedPeerSendDetour(
    void*                    self,
    std::string const&       data,
    NetworkPeer::Reliability reliability,
    Compressibility          compressible
) {
    auto origin = reinterpret_cast<PeerSendFn>(gOriginalCompressedVtableSend);
    if (!origin) return;
    int connSlot     = -1;
    int peerProtocol = peerProtocolVersion(self, &connSlot);
    if (peerProtocol != 0) {
        static std::atomic_int sLogCount{0};
        if (sLogCount.fetch_add(1, std::memory_order_relaxed) < 24) {
            std::vector<std::pair<size_t, size_t>> spans;
            if (splitOutboundBatch(data, spans)) {
                std::string ids;
                for (auto const& [off, len] : spans) {
                    size_t   pos = off;
                    uint32_t id  = readVarUInt(data, pos);
                    ids += fmt::format("{}({}) ", id, len);
                }
                hookLog(
                    "[JERCPLD][S2C] batch proto={} bytes={} packets=[{}]",
                    peerProtocol,
                    data.size(),
                    ids
                );
            } else {
                size_t   pos = 0;
                uint32_t id  = readVarUInt(data, pos);
                hookLog("[JERCPLD][S2C] raw proto={} id={} len={}", peerProtocol, id, data.size());
            }
        }
    }
    if (gTranslationEnabled.load(std::memory_order_relaxed) && peerProtocol != 0
        && peerProtocol != gServerProtocol.load(std::memory_order_relaxed)) {
        std::string translated = data;
        dispatchOutboundBatch(translated, peerProtocol, connSlot, peerCaptureKey(self));
        origin(self, translated, reliability, compressible);
        return;
    }
    captureOutboundBatch(data, peerCaptureKey(self));
    origin(self, data, reliability, compressible);
}

// NetworkPeer 虚表槽位：0 dtor, 1 sendPacket, 2 getNetworkStatus, 3 update,
// 4 flush, 5 isLocal, 6 isEncrypted, 7 isLan, 8 _receivePacket。
// 派生类 override 占用基类同一槽位，所以 vtable[1] 就是 CompressedNetworkPeer::sendPacket。
void registerCompressedPeerHook(void* compressedPeerPtr) {
    if (gCompressedVtableTarget || !compressedPeerPtr) return;
    void** vtable = *reinterpret_cast<void***>(compressedPeerPtr);
    if (!vtable) return;
    void* target = vtable[1];
    if (!target) return;
    gCompressedVtableTarget = target;
    int result              = ll::memory::hook(
        target,
        reinterpret_cast<ll::memory::FuncPtr>(&compressedPeerSendDetour),
        reinterpret_cast<ll::memory::FuncPtr*>(&gOriginalCompressedVtableSend),
        ll::memory::HookPriority::Normal,
        true
    );
    hookLog("[JERCPLD] compressed vtable send hook: target={} result={}", target, result);
    modLog("[JERCPLD] compressed vtable send hook: target={} result={}", target, result);
}

void batchedSendDetour(
    void*                    self,
    std::string const&       data,
    NetworkPeer::Reliability reliability,
    Compressibility          compressible
) {
    static std::atomic_int logCount{0};
    if (logCount.fetch_add(1, std::memory_order_relaxed) < 4) logSendHead("batched", data);
    auto origin = reinterpret_cast<PeerSendFn>(gOriginalBatchedSendPacket);
    if (!origin) return;
    // 已挂钩 compressed 层时由它负责 s2c 翻译与抓包，避免同一包被翻译两次。
    if (gCompressedVtableTarget) {
        origin(self, data, reliability, compressible);
        return;
    }
    std::string        translated;
    std::string const* payload = &data;
    // 已登记的非 2193 客户端才需要 s2c 改写；具体包的处理按 peer 协议号分流：
    // 2225 走结构补齐，2169/2168/1001 只需裁剪 camera_presets 的新增尾部字段。
    int                connSlot = -1;
    int peerProtocol = peerProtocolVersion(self, &connSlot);
    if (gTranslationEnabled.load(std::memory_order_relaxed) && peerProtocol != 0
        && peerProtocol != gServerProtocol.load(std::memory_order_relaxed)) {
        translated = data;
        dispatchOutboundTranslation(translated, peerProtocol, connSlot);
        if (gPtlDrop) {
            hookLog("[JERCPLD][PTL][s2c] packet dropped for protocol={}", peerProtocol);
            return;
        }
        payload = &translated;
    }
    capturePacket("s2c", self, *payload);
    origin(self, *payload, reliability, compressible);
}

using OnDisconnectFn = void (*)(
    ServerNetworkHandler*,
    NetworkIdentifier const&,
    Connection::DisconnectFailReason,
    Connection::DisconnectionStage,
    std::string const&,
    std::string const&,
    bool,
    std::string const&
);

// 连接断开：输出该连接的累计翻译耗时，然后释放槽位。
void logConnectionClose(NetworkIdentifier const& id) {
    int      slotIndex = -1;
    uint32_t connId    = 0;
    int      proto     = 0;
    uint64_t startMs   = 0;
    uint64_t totalNs   = 0;
    uint64_t packets   = 0;
    uint64_t inBytes   = 0;
    uint64_t outBytes  = 0;
    {
        std::unique_lock lock(gTranslatedPeersMutex);
        auto             it = gConnSlotByIdentifier.find(connIdentifierKey(id));
        if (it == gConnSlotByIdentifier.end()) return;
        slotIndex = it->second;
        gConnSlotByIdentifier.erase(it);
        for (auto e = gTranslatedPeers.begin(); e != gTranslatedPeers.end();) {
            if (e->second.connSlot == slotIndex) e = gTranslatedPeers.erase(e);
            else ++e;
        }
    }
    if (slotIndex < 0 || slotIndex >= kConnSlotCount) return;
    auto& slot = gConnStats[slotIndex];
    connId   = slot.id.load(std::memory_order_relaxed);
    proto    = slot.protocol.load(std::memory_order_relaxed);
    startMs  = slot.startMs.load(std::memory_order_relaxed);
    totalNs  = slot.totalNs.load(std::memory_order_relaxed);
    packets  = slot.packets.load(std::memory_order_relaxed);
    inBytes  = slot.inBytes.load(std::memory_order_relaxed);
    outBytes = slot.outBytes.load(std::memory_order_relaxed);
    slot.used.store(0, std::memory_order_relaxed);

    double durationS = static_cast<double>(steadyNowMs() - startMs) / 1000.0;
    if (durationS <= 0) durationS = 1.0;
    double totalMs = static_cast<double>(totalNs) / 1e6;
    double tickPct = totalMs * 100.0 / (durationS * 1000.0);
    int    server  = gServerProtocol.load(std::memory_order_relaxed);
    std::string line = fmt::format(
        "[JERCPLD][CONN] id={} proto={}->{} {} jump={} perf=累计 {:.3f}ms (tick {:.3f}%, pkts={}, bytes={}->{}, dur={:.1f}s)",
        connId,
        proto,
        server,
        ptlRouteSummary(proto, server),
        authInputModeSummary(proto),
        totalMs,
        tickPct,
        packets,
        inBytes,
        outBytes,
        durationS
    );
    modLog("{}", line);
    hookLog("{}", line);
}

void onDisconnectDetour(
    ServerNetworkHandler*                   self,
    NetworkIdentifier const&                id,
    Connection::DisconnectFailReason        reason,
    Connection::DisconnectionStage          stage,
    std::string const&                      serverMessage,
    std::string const&                      bodyOverride,
    bool                                    skipMessage,
    std::string const&                      telemetryOverride
) {
    logConnectionClose(id);
    if (gActiveSessions.load(std::memory_order_relaxed) > 0) {
        gActiveSessions.fetch_sub(1, std::memory_order_relaxed);
    }
    hookLog(
        "[JERCPLD] onDisconnect reason={} stage={} serverMsg='{}' body='{}' skip={}",
        static_cast<int>(reason),
        static_cast<int>(stage),
        serverMessage,
        bodyOverride,
        skipMessage
    );
    auto origin = reinterpret_cast<OnDisconnectFn>(gOriginalOnDisconnect);
    if (origin) {
        origin(self, id, reason, stage, serverMessage, bodyOverride, skipMessage, telemetryOverride);
    }
}

bool installProtocolHooks() {
    if (gHooksInstalled) return true;
    auto loginTarget   = ll::memory::SymbolView{kLoginHandlerSymbol}.resolve(true);
    auto reqTarget     = ll::memory::SymbolView{kRequestNetworkSettingsHandlerSymbol}.resolve(true);
    auto receiveTarget = ll::memory::SymbolView{kReceivePacketSymbol}.resolve(true);
    auto sendTarget    = ll::memory::SymbolView{kEncryptedSendPacketSymbol}.resolve(true);
    auto compressedTarget = ll::memory::SymbolView{kCompressedSendPacketSymbol}.resolve(true);
    auto batchedTarget    = ll::memory::SymbolView{kBatchedSendPacketSymbol}.resolve(true);
    auto disconnectTarget = ll::memory::SymbolView{kOnDisconnectSymbol}.resolve(true);
    modLog(
        "[JERCPLD] resolve login={} req={} recv={} enc_send={} comp_send={} batch_send={} on_disconnect={}",
        loginTarget,
        reqTarget,
        receiveTarget,
        sendTarget,
        compressedTarget,
        batchedTarget,
        disconnectTarget
    );
    if (!loginTarget || !reqTarget || !receiveTarget || !sendTarget) {
        modLog("[JERCPLD] hook failed: handler symbol not resolved");
        return false;
    }
    gLoginTarget                  = loginTarget;
    gRequestNetworkSettingsTarget = reqTarget;
    gReceivePacketTarget          = receiveTarget;
    gEncryptedSendTarget          = sendTarget;

    int loginResult = ll::memory::hook(
        loginTarget,
        reinterpret_cast<ll::memory::FuncPtr>(&loginHandlerDetour),
        reinterpret_cast<ll::memory::FuncPtr*>(&gOriginalLoginHandler),
        ll::memory::HookPriority::Normal,
        true
    );
    int reqResult = ll::memory::hook(
        reqTarget,
        reinterpret_cast<ll::memory::FuncPtr>(&requestNetworkSettingsHandlerDetour),
        reinterpret_cast<ll::memory::FuncPtr*>(&gOriginalRequestNetworkSettingsHandler),
        ll::memory::HookPriority::Normal,
        true
    );
    int receiveResult = ll::memory::hook(
        receiveTarget,
        reinterpret_cast<ll::memory::FuncPtr>(&receivePacketDetour),
        reinterpret_cast<ll::memory::FuncPtr*>(&gOriginalReceivePacket),
        ll::memory::HookPriority::Normal,
        true
    );
    int sendResult = ll::memory::hook(
        sendTarget,
        reinterpret_cast<ll::memory::FuncPtr>(&encryptedSendPacketDetour),
        reinterpret_cast<ll::memory::FuncPtr*>(&gOriginalEncryptedSendPacket),
        ll::memory::HookPriority::Normal,
        true
    );
    int compressedResult = 0;
    int batchedResult    = 0;
    int disconnectResult = 0;
    if (disconnectTarget) {
        gOnDisconnectTarget = disconnectTarget;
        disconnectResult    = ll::memory::hook(
            disconnectTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&onDisconnectDetour),
            reinterpret_cast<ll::memory::FuncPtr*>(&gOriginalOnDisconnect),
            ll::memory::HookPriority::Normal,
            true
        );
    }
    if (compressedTarget) {
        gCompressedSendTarget = compressedTarget;
        compressedResult      = ll::memory::hook(
            compressedTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&compressedSendDetour),
            reinterpret_cast<ll::memory::FuncPtr*>(&gOriginalCompressedSendPacket),
            ll::memory::HookPriority::Normal,
            true
        );
    }
    if (batchedTarget) {
        gBatchedSendTarget = batchedTarget;
        batchedResult      = ll::memory::hook(
            batchedTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&batchedSendDetour),
            reinterpret_cast<ll::memory::FuncPtr*>(&gOriginalBatchedSendPacket),
            ll::memory::HookPriority::Normal,
            true
        );
    }

    if (loginResult != 0 || reqResult != 0 || receiveResult != 0 || sendResult != 0) {
        modLog(
            "[JERCPLD] hook failed: login={} req_net_settings={} receive_packet={} encrypted_send={}",
            loginResult,
            reqResult,
            receiveResult,
            sendResult
        );
        return false;
    }

    gHooksInstalled = true;
    modLog("[JERCPLD] protocol hooks installed (symbol-based, raw rewrite enabled)");
    return true;
}

void uninstallProtocolHooks() {
    if (!gHooksInstalled) return;
    if (gLoginTarget) {
        ll::memory::unhook(
            gLoginTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&loginHandlerDetour),
            true
        );
    }
    if (gRequestNetworkSettingsTarget) {
        ll::memory::unhook(
            gRequestNetworkSettingsTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&requestNetworkSettingsHandlerDetour),
            true
        );
    }
    if (gReceivePacketTarget) {
        ll::memory::unhook(
            gReceivePacketTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&receivePacketDetour),
            true
        );
    }
    if (gEncryptedSendTarget) {
        ll::memory::unhook(
            gEncryptedSendTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&encryptedSendPacketDetour),
            true
        );
    }
    if (gCompressedSendTarget) {
        ll::memory::unhook(
            gCompressedSendTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&compressedSendDetour),
            true
        );
    }
    if (gBatchedSendTarget) {
        ll::memory::unhook(
            gBatchedSendTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&batchedSendDetour),
            true
        );
    }
    if (gOnDisconnectTarget) {
        ll::memory::unhook(
            gOnDisconnectTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&onDisconnectDetour),
            true
        );
    }
    if (gCompressedVtableTarget) {
        ll::memory::unhook(
            gCompressedVtableTarget,
            reinterpret_cast<ll::memory::FuncPtr>(&compressedPeerSendDetour),
            true
        );
        gCompressedVtableTarget = nullptr;
    }
    gHooksInstalled = false;
    modLog("[JERCPLD] protocol hooks removed");
}

} // namespace

class JerCpldMod {
public:
    static JerCpldMod& getInstance() {
        static JerCpldMod instance;
        return instance;
    }

    JerCpldMod() : mSelf(*ll::mod::NativeMod::current()) {}

    [[nodiscard]] ll::mod::NativeMod& getSelf() const { return mSelf; }

    bool load() {
        auto& logger = getSelf().getLogger();
        loadConfig(logger);
        printBanner(logger);
        logger.info(
            "[{}] loaded v{} | server {}/{} | clients protocol {}-{} | translation={}",
            kModName,
            kVersion,
            mConfig.serverVersion,
            mConfig.serverProtocol,
            mConfig.acceptMinProtocol,
            mConfig.acceptMaxProtocol,
            mConfig.translationEnabled ? "ON" : "OFF"
        );
        return true;
    }

    bool enable() {
        auto& logger = getSelf().getLogger();
        gTranslationEnabled.store(mConfig.translationEnabled, std::memory_order_relaxed);
        logger.info("[{}] enabled (translation={})", kModName, mConfig.translationEnabled ? "ON" : "OFF");
        if (mConfig.translationEnabled) {
            ptlLoadAll();
            gPtlMtime = ptlDirMtime();
            installProtocolHooks();
        }
        if (mConfig.raknetSpike.enabled) {
            runRaknetSpike(mConfig.raknetSpike.port, mConfig.raknetSpike.maxConnections);
        }
        if (mConfig.symProbeEnabled) {
            runSymProbe();
        }
        if (mConfig.connProbeEnabled) {
            std::thread([] {
                std::this_thread::sleep_for(std::chrono::seconds(12));
                runConnectionProbe();
            }).detach();
        }
        if (mConfig.perfStatsEnabled) {
            // 30s 汇总一次；只读原子计数，不与热路径共享锁。
            std::thread([] {
                for (;;) {
                    std::this_thread::sleep_for(std::chrono::seconds(30));
                    if (!gPerfEnabled.load(std::memory_order_relaxed)) continue;
                    perfFlushWindow(30);
                }
            }).detach();
            logger.info("[{}] perf stats ON (30s window -> hook.log)", kModName);
        }
        return true;
    }

    bool disable() {
        uninstallProtocolHooks();
        getSelf().getLogger().info("[{}] disabled", kModName);
        return true;
    }

private:
    void loadConfig(ll::io::Logger& logger) {
        namespace fs = std::filesystem;
        try {
            auto configDir = getSelf().getConfigDir();
            if (configDir.empty()) {
                configDir = getSelf().getModDir() / "config";
            }
            fs::create_directories(configDir);
            auto configPath = configDir / "config.json";
            gConfigPath      = configPath.string();
            {
                std::error_code ec;
                gConfigMtime = fs::last_write_time(configPath, ec);
            }

            if (!fs::exists(configPath)) {
                std::ofstream out(configPath);
                out << "{\n"
                    << "  \"translationEnabled\": true,\n"
                    << "  \"serverVersion\": \"1.26.51\",\n"
                    << "  \"serverProtocol\": 2193,\n"
                    << "  \"acceptMinProtocol\": 2168,\n"
                    << "  \"acceptMaxProtocol\": 2225,\n"
                    << "  \"perfStatsEnabled\": false,\n"
                    << "  \"capture\": {\n"
                    << "    \"enabled\": false,\n"
                    << "    \"outputDir\": \"diagnostics\",\n"
                    << "    \"maxFileBytes\": 8388608,\n"
                    << "    \"maxFiles\": 4,\n"
                    << "    \"packetWhitelist\": []\n"
                    << "  }\n"
                    << "}\n";
            }

            std::ifstream in(configPath);
            nlohmann::json json;
            in >> json;
            mConfig.translationEnabled = json.value("translationEnabled", mConfig.translationEnabled);
            mConfig.serverVersion      = json.value("serverVersion", mConfig.serverVersion);
            mConfig.serverProtocol     = json.value("serverProtocol", mConfig.serverProtocol);
            mConfig.acceptMinProtocol  = json.value("acceptMinProtocol", mConfig.acceptMinProtocol);
            mConfig.acceptMaxProtocol  = json.value("acceptMaxProtocol", mConfig.acceptMaxProtocol);
            mConfig.symProbeEnabled    = json.value("symProbeEnabled", false);
            mConfig.connProbeEnabled   = json.value("connProbeEnabled", false);
            mConfig.perfStatsEnabled   = json.value("perfStatsEnabled", false);
            if (json.contains("capture")) {
                auto const& capture          = json["capture"];
                mConfig.capture.enabled      = capture.value("enabled", false);
                mConfig.capture.outputDir    = capture.value("outputDir", std::string("diagnostics"));
                mConfig.capture.maxFileBytes = capture.value("maxFileBytes", 8 * 1024 * 1024);
                mConfig.capture.maxFiles     = capture.value("maxFiles", 4);
                mConfig.capture.whitelist    = capture.value("packetWhitelist", std::vector<int>{});
            }
            gTranslationEnabled.store(mConfig.translationEnabled, std::memory_order_relaxed);
            gServerProtocol.store(mConfig.serverProtocol, std::memory_order_relaxed);
            if (json.contains("raknetSpike")) {
                auto const& spike = json["raknetSpike"];
                mConfig.raknetSpike.enabled        = spike.value("enabled", false);
                mConfig.raknetSpike.port           = spike.value("port", 19234);
                mConfig.raknetSpike.maxConnections = spike.value("maxConnections", 8);
            }
            gAcceptMinProtocol.store(mConfig.acceptMinProtocol, std::memory_order_relaxed);
            gAcceptMaxProtocol.store(mConfig.acceptMaxProtocol, std::memory_order_relaxed);
            gPerfEnabled.store(mConfig.perfStatsEnabled, std::memory_order_relaxed);
            gCaptureEnabled.store(mConfig.capture.enabled, std::memory_order_relaxed);
            gRawDumpEnabled.store(mConfig.capture.enabled, std::memory_order_relaxed);
            gCaptureDir          = mConfig.capture.outputDir;
            gCaptureMaxFileBytes = mConfig.capture.maxFileBytes;
            gCaptureMaxFiles     = mConfig.capture.maxFiles;
            gCaptureWhitelist    = mConfig.capture.whitelist;
            logger.info(
                "[{}] capture={} dir={} whitelist={}",
                kModName,
                mConfig.capture.enabled ? "ON" : "OFF",
                mConfig.capture.outputDir,
                mConfig.capture.whitelist.size()
            );
            logger.info("[{}] config loaded: {}", kModName, configPath.string());
        } catch (std::exception const& e) {
            logger.error("[{}] config load failed, using defaults: {}", kModName, e.what());
        }
    }

    void printBanner(ll::io::Logger& logger) {
        static char const* banner[] = {
            "     ###  #######  ######   ######  ######   ##      ######",
            "      ##  ##       ##   ## ##      ##   ##  ##      ##   ##",
            "      ##  #####    ######  ##      ######   ##      ##   ##",
            "      ##  ##       ## ##   ##      ##       ##      ##   ##",
            "##    ##  ##       ##  ##  ##      ##       ##      ##   ##",
            " #####   #######  ##   ##  ######  ##       ####### ######",
        };
        logger.info("");
        for (auto line : banner) {
            logger.info("{}", line);
        }
        logger.info("==========================================================================");
        logger.info("  Mod name        : {}", kModName);
        logger.info("  Mod version     : {}", kVersion);
        logger.info(
            "  Server target   : BDS {} (protocol {})",
            mConfig.serverVersion,
            mConfig.serverProtocol
        );
        logger.info(
            "  Client range    : protocol {} - {} ({} ~ {})",
            mConfig.acceptMinProtocol,
            mConfig.acceptMaxProtocol,
            protocolToVersionLabel(mConfig.acceptMinProtocol),
            protocolToVersionLabel(mConfig.acceptMaxProtocol)
        );
        logger.info("  Translation     : {}", mConfig.translationEnabled ? "ON" : "OFF");
        logger.info("  Perf stats      : {}", mConfig.perfStatsEnabled ? "ON (30s -> hook.log)" : "OFF");
        logger.info(
            "  Packet capture  : {}",
            mConfig.capture.enabled ? fmt::format("ON -> {}", mConfig.capture.outputDir) : "OFF"
        );
        logger.info("  Config file     : config/config.json");
        logger.info("==========================================================================");
        logger.info("");
    }

    ll::mod::NativeMod& mSelf;
    Config              mConfig;
};

} // namespace jercpld

LL_REGISTER_MOD(jercpld::JerCpldMod, jercpld::JerCpldMod::getInstance());
