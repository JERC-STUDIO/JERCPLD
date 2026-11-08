#define LL_MEMORY_OPERATORS
#include "ll/api/memory/MemoryOperators.h"

#include "ll/api/mod/NativeMod.h"
#include "ll/api/mod/RegisterHelper.h"

#include "ll/api/memory/Hook.h"
#include "ll/api/memory/Memory.h"
#include "ll/api/memory/Symbol.h"
#include "mc/deps/core/utility/optional_ref.h"
#include "mc/network/NetEventCallback.h"
#include "mc/network/EncryptedNetworkPeer.h"
#include "mc/network/NetworkConnection.h"
#include "mc/network/NetworkPeer.h"
#include "mc/network/ServerNetworkHandler.h"
#include "mc/network/packet/LoginPacket.h"
#include "mc/network/packet/RequestNetworkSettingsPacket.h"

// LeviLamina 26.51.5 源码包缺少 ll/api/service/TargetedBedrock.h，
// 直接声明已导出的 getServerNetworkHandler。
namespace ll::service::inline bedrock {
[[nodiscard]] optional_ref<ServerNetworkHandler> getServerNetworkHandler(bool isClientSide = false);
}

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <nlohmann/json.hpp>

namespace jercpld {

constexpr char const* kModName  = "JERCPLD";
constexpr char const* kVersion  = "0.1.0";

struct CaptureConfig {
    bool             enabled      = false;
    std::string      outputDir    = "diagnostics";
    int              maxFileBytes = 8 * 1024 * 1024;
    int              maxFiles     = 4;
    std::vector<int> whitelist;
};

struct Config {
    bool          translationEnabled = true;
    std::string   serverVersion      = "1.26.51";
    int           serverProtocol     = 2193;
    int           acceptMinProtocol  = 1001; // 1.26.30 (社区数据)
    int           acceptMaxProtocol  = 2225; // 1.26.60
    CaptureConfig capture;
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
std::mutex       gCaptureMutex;
std::unordered_map<void const*, uint32_t> gCaptureSessions;
uint32_t         gCaptureNextSession = 1;
std::mutex       gTranslatedPeersMutex;
std::unordered_set<void const*> gTranslatedPeers;
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

// 热路径不写文件；仅用于握手阶段诊断。
template <class... Args>
void hookLog(fmt::format_string<Args...> fmt, Args&&... args) {
    std::ofstream out("plugins/JERCPLD/hook.log", std::ios::app);
    out << nowStamp() << " " << fmt::format(fmt, std::forward<Args>(args)...) << '\n';
}

using LoginHandlerFn = void (*)(NetEventCallback*, NetworkIdentifier const&, std::shared_ptr<LoginPacket>);
using RequestNetworkSettingsHandlerFn =
    void (*)(NetEventCallback*, NetworkIdentifier const&, RequestNetworkSettingsPacket const&);

void loginHandlerDetour(NetEventCallback* self, NetworkIdentifier const& source, std::shared_ptr<LoginPacket> packet) {
    if (packet) {
        int  from       = packet->mClientNetworkVersion;
        bool hasRequest = packet->mConnectionRequest != nullptr;
        hookLog("[JERCPLD] login handler hit: protocol={} hasConnectionRequest={}", from, hasRequest);
        // 只在明确是 2225 时改写，避免槽位不匹配时误伤其它包。
        if (from == 2225 && gTranslationEnabled.load(std::memory_order_relaxed)) {
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
    if (from == 2225 && gTranslationEnabled.load(std::memory_order_relaxed)) {
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

// 在反序列化之前改写原始包，避免 BDS 按客户端的 2225 协议去解析 Login 的令牌。
void rewriteRawProtocol(std::string& data) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    if (packetId != 1 && packetId != 193) return;
    if (offset + 4 > data.size()) return;
    uint32_t version = (static_cast<uint32_t>(static_cast<uint8_t>(data[offset])) << 24)
                     | (static_cast<uint32_t>(static_cast<uint8_t>(data[offset + 1])) << 16)
                     | (static_cast<uint32_t>(static_cast<uint8_t>(data[offset + 2])) << 8)
                     | static_cast<uint32_t>(static_cast<uint8_t>(data[offset + 3]));
    if (version != 2225) return;
    data[offset]     = 0;
    data[offset + 1] = 0;
    data[offset + 2] = static_cast<char>(0x08);
    data[offset + 3] = static_cast<char>(0x91); // 2193
    hookLog("[JERCPLD] raw protocol rewrite: packet={} {} -> 2193", packetId, version);
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

bool is2225ProtocolPacket(std::string const& data) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    if (packetId != 1 && packetId != 193) return false;
    if (offset + 4 > data.size()) return false;
    uint32_t version = (static_cast<uint32_t>(static_cast<uint8_t>(data[offset])) << 24)
                     | (static_cast<uint32_t>(static_cast<uint8_t>(data[offset + 1])) << 16)
                     | (static_cast<uint32_t>(static_cast<uint8_t>(data[offset + 2])) << 8)
                     | static_cast<uint32_t>(static_cast<uint8_t>(data[offset + 3]));
    return version == 2225;
}

void registerTranslatedPeer(NetworkConnection* connection) {
    if (!connection) return;
    if (auto batched = connection->mBatchedPeer.lock()) {
        std::lock_guard lock(gTranslatedPeersMutex);
        gTranslatedPeers.insert(batched.get());
    }
}

bool isTranslatedPeer(void const* peer) {
    std::lock_guard lock(gTranslatedPeersMutex);
    return gTranslatedPeers.contains(peer);
}

bool connectionIsTranslated(NetworkConnection* connection) {
    if (!connection) return false;
    if (auto batched = connection->mBatchedPeer.lock()) return isTranslatedPeer(batched.get());
    return false;
}

using ReceivePacketFn = NetworkPeer::DataStatus (*)(
    NetworkConnection*,
    std::string&,
    std::shared_ptr<std::chrono::steady_clock::time_point> const&
);

void trimAnimateHand(std::string& data);

NetworkPeer::DataStatus receivePacketDetour(
    NetworkConnection*                                             self,
    std::string&                                                   buffer,
    std::shared_ptr<std::chrono::steady_clock::time_point> const& timepointPtr
) {
    auto origin = reinterpret_cast<ReceivePacketFn>(gOriginalReceivePacket);
    auto status = origin ? origin(self, buffer, timepointPtr) : NetworkPeer::DataStatus::NoData;
    if (status == NetworkPeer::DataStatus::HasData) {
        bool is2225 = is2225ProtocolPacket(buffer);
        if (gTranslationEnabled.load(std::memory_order_relaxed)) {
            rewriteRawProtocol(buffer);
            if (is2225 || connectionIsTranslated(self)) {
                disableClientCache(buffer);
                trimAnimateHand(buffer);
            }
        }
        if (is2225) {
            registerTranslatedPeer(self);
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

bool skipNbtPayload(PacketCursor& c, uint8_t type) {
    switch (type) {
    case 0: return true;
    case 1: c.skip(1); return c.ok;
    case 2: c.skip(2); return c.ok;
    case 3: c.skip(4); return c.ok;
    case 4: c.skip(8); return c.ok;
    case 5: c.skip(4); return c.ok;
    case 6: c.skip(8); return c.ok;
    case 7:
    case 8: {
        uint64_t len = c.var();
        if (!c.ok) return false;
        c.skip(static_cast<size_t>(len));
        return c.ok;
    }
    case 9: {
        uint8_t  elementType = c.u8();
        uint64_t count       = c.var();
        if (!c.ok || count > 1000000) return false;
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
    case 11: {
        uint64_t len = c.var();
        if (!c.ok || len > 1000000) return false;
        c.skip(static_cast<size_t>(len) * 4);
        return c.ok;
    }
    case 12: {
        uint64_t len = c.var();
        if (!c.ok || len > 1000000) return false;
        c.skip(static_cast<size_t>(len) * 8);
        return c.ok;
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
// 同时 2225 的 Action 枚举插入了新值：WakeUp 2->3、CriticalHit 3->4、
// MagicCriticalHit 4->5，c2s/s2c 都要重映射。
int zigzagDecode32(uint64_t raw) {
    return static_cast<int>((raw >> 1) ^ (0 - static_cast<int64_t>(raw & 1)));
}

void trimAnimateHand(std::string& data) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    if (packetId != 44) return;

    PacketCursor cursor{data, offset};
    size_t       actionPos = cursor.pos;
    uint8_t      actionId  = cursor.u8();
    if (!cursor.ok) return;
    uint8_t mapped = actionId;
    if (actionId == 3) mapped = 2;
    else if (actionId == 4) mapped = 3;
    else if (actionId == 5) mapped = 4;
    else if (actionId == 2) mapped = 0;
    if (mapped != actionId) data[actionPos] = static_cast<char>(mapped);

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

// 2225 GameType 重新编号：2193(0 undefined,1 survival,2 creative,3 adventure,
// 4 default,5 spectator,6 world_default) -> 2225(-1,0,1,2,5,6,0)。
int mapGameModeTo2225(int value) {
    switch (value) {
    case 0: return -1;
    case 1: return 0;
    case 2: return 1;
    case 3: return 2;
    case 4: return 5;
    case 5: return 6;
    case 6: return 0;
    default: return value;
    }
}

void writeZigzagByte(std::string& data, size_t pos, int value) {
    data[pos] = static_cast<char>((value << 1) ^ (value >> 31));
}

bool mapStartGameGameModes(std::string& data, size_t offset) {
    PacketCursor cursor{data, offset};
    cursor.var(); // entity_id
    cursor.var(); // runtime_entity_id
    size_t   playerPos = cursor.pos;
    uint64_t playerRaw = cursor.var();
    if (!cursor.ok || cursor.pos - playerPos != 1) return false;
    int playerMode = zigzagDecode32(playerRaw);

    cursor.skip(12 + 8 + 8 + 2); // position / rotation / seed / biome_type
    size_t begin = 0, end = 0;
    if (!cursor.str(begin, end)) return false; // biome_name
    cursor.var(); // dimension
    cursor.var(); // generator
    size_t   worldPos = cursor.pos;
    uint64_t worldRaw = cursor.var();
    if (!cursor.ok || cursor.pos - worldPos != 1) return false;
    int worldMode = zigzagDecode32(worldRaw);

    writeZigzagByte(data, playerPos, mapGameModeTo2225(playerMode));
    writeZigzagByte(data, worldPos, mapGameModeTo2225(worldMode));
    return true;
}

// s2c animate：2193 Action -> 2225 Action
bool remapAnimateActionTo2225(std::string& data, size_t offset) {
    PacketCursor cursor{data, offset};
    size_t       pos      = cursor.pos;
    uint8_t      actionId = cursor.u8();
    if (!cursor.ok) return false;
    uint8_t mapped = actionId;
    if (actionId == 2) mapped = 3;
    else if (actionId == 3) mapped = 4;
    else if (actionId == 4) mapped = 5;
    if (mapped != actionId) data[pos] = static_cast<char>(mapped);
    return true;
}

// s2c entity_event(27)：ActorEvent 枚举在 2225 重新编号
int mapActorEventTo2225(int value) {
    if (value >= 9 && value <= 38) return value + 1;
    if (value == 39) return 57;
    if (value >= 40 && value <= 54) return value + 20;
    if (value >= 55 && value <= 60) return value + 21;
    return value;
}

void mapEntityEventTo2225(std::string& data, size_t offset) {
    PacketCursor cursor{data, offset};
    cursor.var(); // runtime_entity_id
    size_t  pos     = cursor.pos;
    uint8_t eventId = cursor.u8();
    if (!cursor.ok) return;
    int mapped = mapActorEventTo2225(eventId);
    if (mapped != eventId) data[pos] = static_cast<char>(mapped);
}

// s2c 62 set_player_game_type / 151 update_player_game_type
void mapGameModePacketTo2225(std::string& data, size_t offset) {
    PacketCursor cursor{data, offset};
    size_t       pos      = cursor.pos;
    uint64_t     raw      = cursor.var();
    if (!cursor.ok || cursor.pos - pos != 1) return;
    int mode   = zigzagDecode32(raw);
    int mapped = mapGameModeTo2225(mode);
    if (mapped != mode) writeZigzagByte(data, pos, mapped);
}

// c2s interact(33)：2225 Action(3..6) -> 2193(1..4)
void mapInteractActionTo2193(std::string& data) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    if (packetId != 33 || offset >= data.size()) return;
    uint8_t action = static_cast<uint8_t>(data[offset]);
    uint8_t mapped = action;
    switch (action) {
    case 3: mapped = 1; break;
    case 4: mapped = 2; break;
    case 5: mapped = 3; break;
    case 6: mapped = 4; break;
    case 1:
    case 2: mapped = 0; break;
    default: break;
    }
    if (mapped != action) {
        data[offset] = static_cast<char>(mapped);
        hookLog("[JERCPLD] c2s interact action remapped: {} -> {}", action, mapped);
    }
}

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

// 2193 -> 2225：补齐新增的尾部字段（仅限字段确实位于末尾的包）。
void translateOutboundPacket(std::string& data) {
    size_t   offset   = 0;
    uint32_t packetId = readVarUInt(data, offset);
    switch (packetId) {
    case 11: // StartGame: editor_level_migration_version 在中间
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

void logSendHead(char const* tag, std::string const& data);

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
    std::string        translated;
    std::string const* payload = &data;
    if (gTranslationEnabled.load(std::memory_order_relaxed) && isTranslatedPeer(self)) {
        translated = data;
        translateOutboundPacket(translated);
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
            installProtocolHooks();
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
                    << "  \"acceptMinProtocol\": 1001,\n"
                    << "  \"acceptMaxProtocol\": 2225,\n"
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
            if (json.contains("capture")) {
                auto const& capture          = json["capture"];
                mConfig.capture.enabled      = capture.value("enabled", false);
                mConfig.capture.outputDir    = capture.value("outputDir", std::string("diagnostics"));
                mConfig.capture.maxFileBytes = capture.value("maxFileBytes", 8 * 1024 * 1024);
                mConfig.capture.maxFiles     = capture.value("maxFiles", 4);
                mConfig.capture.whitelist    = capture.value("packetWhitelist", std::vector<int>{});
            }
            gTranslationEnabled.store(mConfig.translationEnabled, std::memory_order_relaxed);
            gCaptureEnabled.store(mConfig.capture.enabled, std::memory_order_relaxed);
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
            "  Client range    : protocol {} - {} (1.26.30 - 1.26.62)",
            mConfig.acceptMinProtocol,
            mConfig.acceptMaxProtocol
        );
        logger.info("  Translation     : {}", mConfig.translationEnabled ? "ON" : "OFF");
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
