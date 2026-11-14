// JercpldPtl1001.h —— 2168(1.26.40) <-> 1001(1.26.30~26.33) s2c 结构转换
//
// 线格式依据 CloudburstMC/Protocol 3.0 的实际实现（不是 minecraft-data 的 schema）：
//   bedrock-codec/.../v975/BedrockCodecHelper_v975.java   ← 1001 继承自它
//   bedrock-codec/.../v2168/BedrockCodecHelper_v2168.java ← 2168 的独立实现
//   bedrock-codec/.../v291/serializer/MovePlayerSerializer_v291.java  ← 1001 继承
//   bedrock-codec/.../v2168/serializer/MovePlayerSerializer_v2168.java
//   bedrock-codec/.../v361/BedrockCodecHelper_v361.java   ← 1001 的 writeEntityData
//
// 三个关键结论（与旧版 PoC 不同）：
//
// 1. Item 的 air 在 1001 和 2168 下**完全相同**，都是 8 字节。
//    旧 PoC 认为「1001 短路 1 字节」是错的 —— v975/v2168 的
//    writeNetworkItemStackDescriptor 都无条件写 count(lu16)，air 不短路。
//
// 2. Item 的真正差异只在 has_stack_id == true 时：
//      1001: hasStack(u8) + variantType(varint) + stackId(zigzag)
//      2168: hasStack(u8) + stackId(zigzag)
//    1001 多一个 variantType（v975 里硬编码写 0）。
//
// 3. metadata 的 legacy_type **确实存在**，2168 每个条目多 1 字节，
//    值是 type 的副本（v2168: buffer.writeByte(definition.getFormat().ordinal())）。
//
// 本文件不依赖 LeviLamina，只依赖标准库，可单独用 g++ 编译测试。
// 接入方式：在 JerCpldMod.cpp 里 #include 本文件，并在
// translateOutboundPacket() 的 peerProtocol < 2168 分支里按 packetId 调用。
#pragma once

#include <cstdint>
#include <string>

namespace jercpld1001 {

// ---------------- 基础读 ----------------

struct Reader {
    std::string const& d;
    size_t             p;
    size_t             end;
    bool               ok = true;

    Reader(std::string const& data, size_t begin, size_t limit)
        : d(data), p(begin), end(limit < data.size() ? limit : data.size()) {}

    bool has(size_t n) const { return ok && p + n <= end; }

    uint8_t u8() {
        if (!has(1)) { ok = false; return 0; }
        return static_cast<uint8_t>(d[p++]);
    }

    // 无符号 LEB128
    uint64_t var() {
        uint64_t v     = 0;
        int      shift = 0;
        while (has(1) && shift <= 63) {
            uint8_t b = static_cast<uint8_t>(d[p++]);
            v |= static_cast<uint64_t>(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
        }
        ok = false;
        return 0;
    }

    int64_t zigzag() {
        uint64_t raw = var();
        if (!ok) return 0;
        return static_cast<int64_t>(raw >> 1) ^ -static_cast<int64_t>(raw & 1);
    }

    uint16_t lu16() {
        if (!has(2)) { ok = false; return 0; }
        uint16_t v = static_cast<uint16_t>(static_cast<uint8_t>(d[p]))
                   | static_cast<uint16_t>(static_cast<uint8_t>(d[p + 1]) << 8);
        p += 2;
        return v;
    }

    int16_t li16() { return static_cast<int16_t>(lu16()); }

    uint32_t li32() {
        if (!has(4)) { ok = false; return 0; }
        uint32_t v = static_cast<uint32_t>(static_cast<uint8_t>(d[p]))
                   | (static_cast<uint32_t>(static_cast<uint8_t>(d[p + 1])) << 8)
                   | (static_cast<uint32_t>(static_cast<uint8_t>(d[p + 2])) << 16)
                   | (static_cast<uint32_t>(static_cast<uint8_t>(d[p + 3])) << 24);
        p += 4;
        return v;
    }

    void skip(size_t n) {
        if (!has(n)) { ok = false; return; }
        p += n;
    }

    void copy(size_t n, std::string& out) {
        if (!has(n)) { ok = false; return; }
        out.append(d, p, n);
        p += n;
    }
};

// ---------------- 基础写 ----------------

inline void putVar(std::string& out, uint64_t v) {
    while (v >= 0x80) {
        out.push_back(static_cast<char>((v & 0x7F) | 0x80));
        v >>= 7;
    }
    out.push_back(static_cast<char>(v));
}

inline void putZigZag(std::string& out, int64_t v) {
    putVar(out, (static_cast<uint64_t>(v) << 1) ^ static_cast<uint64_t>(v >> 63));
}

inline void putLu16(std::string& out, uint16_t v) {
    out.push_back(static_cast<char>(v & 0xFF));
    out.push_back(static_cast<char>((v >> 8) & 0xFF));
}

inline void putLi16(std::string& out, int16_t v) {
    putLu16(out, static_cast<uint16_t>(v));
}

// ---------------- NBT 跳过（网络版，小端） ----------------

inline bool skipNbtPayload(Reader& in, uint8_t type);

inline bool skipNbtCompoundBody(Reader& in) {
    for (;;) {
        uint8_t t = in.u8();
        if (!in.ok) return false;
        if (t == 0) return true;
        uint16_t nameLen = in.lu16();      // 名称长度 lu16
        if (!in.ok) return false;
        in.skip(nameLen);
        if (!in.ok) return false;
        if (!skipNbtPayload(in, t)) return false;
    }
}

inline bool skipNbtPayload(Reader& in, uint8_t type) {
    switch (type) {
    case 0: return true;
    case 1: in.u8(); return in.ok;
    case 2: in.skip(2); return in.ok;
    case 3: in.skip(4); return in.ok;
    case 4: in.skip(8); return in.ok;
    case 5: in.skip(4); return in.ok;
    case 6: in.skip(8); return in.ok;
    case 7: { // byte array
        uint32_t n = in.li32();
        if (!in.ok) return false;
        in.skip(n);
        return in.ok;
    }
    case 8: { // string
        uint16_t n = in.lu16();
        if (!in.ok) return false;
        in.skip(n);
        return in.ok;
    }
    case 9: { // list
        uint8_t  elem = in.u8();
        uint32_t n    = in.li32();
        if (!in.ok) return false;
        for (uint32_t i = 0; i < n; i++) {
            if (!skipNbtPayload(in, elem)) return false;
        }
        return in.ok;
    }
    case 10: return skipNbtCompoundBody(in);
    case 11: { // int array
        uint32_t n = in.li32();
        if (!in.ok) return false;
        in.skip(static_cast<size_t>(n) * 4);
        return in.ok;
    }
    case 12: { // long array
        uint32_t n = in.li32();
        if (!in.ok) return false;
        in.skip(static_cast<size_t>(n) * 8);
        return in.ok;
    }
    default: return false;
    }
}

// ---------------- Item ----------------
//
// 2168（NetworkItemStackDescriptor）：
//   networkId        li16
//   count            lu16
//   damage           varint
//   hasStack         u8
//   [stackId         zigzag]
//   blockRuntimeId   varint
//   extraLen         varint + bytes
//
// 1001（v975 继承链）：
//   同上，但 hasStack 为真时多一个 variantType(varint)，值恒为 0。
//
// air（networkId == 0）两边逐字节相同，无需任何处理。

inline bool item2168To1001(Reader& in, std::string& out) {
    int16_t networkId = in.li16();
    if (!in.ok) return false;
    putLi16(out, networkId);

    uint16_t count    = in.lu16();
    uint64_t damage   = in.var();
    uint8_t  hasStack = in.u8();
    if (!in.ok) return false;

    putLu16(out, count);
    putVar(out, damage);
    out.push_back(static_cast<char>(hasStack ? 1 : 0));

    if (hasStack) {
        putVar(out, 0);              // 1001 的 variantType，v975 硬编码 0
        int64_t stackId = in.zigzag();
        if (!in.ok) return false;
        putZigZag(out, stackId);
    }

    uint64_t blockRuntimeId = in.var();
    if (!in.ok) return false;
    putVar(out, blockRuntimeId);

    uint64_t extraLen = in.var();
    if (!in.ok) return false;
    putVar(out, extraLen);
    if (extraLen > 0) {
        in.copy(static_cast<size_t>(extraLen), out);
    }
    return in.ok;
}

inline bool item1001To2168(Reader& in, std::string& out) {
    int16_t networkId = in.li16();
    if (!in.ok) return false;
    putLi16(out, networkId);

    uint16_t count    = in.lu16();
    uint64_t damage   = in.var();
    uint8_t  hasStack = in.u8();
    if (!in.ok) return false;

    putLu16(out, count);
    putVar(out, damage);
    out.push_back(static_cast<char>(hasStack ? 1 : 0));

    if (hasStack) {
        (void)in.var();              // 丢弃 1001 的 variantType
        if (!in.ok) return false;
        int64_t stackId = in.zigzag();
        if (!in.ok) return false;
        putZigZag(out, stackId);
    }

    uint64_t blockRuntimeId = in.var();
    if (!in.ok) return false;
    putVar(out, blockRuntimeId);

    uint64_t extraLen = in.var();
    if (!in.ok) return false;
    putVar(out, extraLen);
    if (extraLen > 0) {
        in.copy(static_cast<size_t>(extraLen), out);
    }
    return in.ok;
}

// ---------------- metadata 字典 ----------------
//
// 1001: key(varint) type(varint) value(...)
// 2168: key(varint) type(varint) legacy_type(u8) value(...)
//
// legacy_type 的值是 type 的副本（v2168 writeByte(format.ordinal())）。

inline bool skipMetadataValue(Reader& in, uint64_t type) {
    switch (type) {
    case 0: in.u8();     return in.ok;   // BYTE
    case 1: in.skip(2);  return in.ok;   // SHORT
    case 2: in.var();    return in.ok;   // INT（zigzag）
    case 3: in.skip(4);  return in.ok;   // FLOAT
    case 4: {                            // STRING
        uint64_t n = in.var();
        if (!in.ok) return false;
        in.skip(static_cast<size_t>(n));
        return in.ok;
    }
    case 5: return skipNbtCompoundBody(in);  // COMPOUND
    case 6: in.skip(12); return in.ok;   // VEC3I
    case 7: in.var();    return in.ok;   // LONG（zigzag）
    case 8: in.skip(12); return in.ok;   // VEC3F
    default: return false;
    }
}

inline bool metadata2168To1001(Reader& in, std::string& out) {
    uint64_t count = in.var();
    if (!in.ok) return false;
    putVar(out, count);

    for (uint64_t i = 0; i < count; i++) {
        uint64_t key  = in.var();
        uint64_t type = in.var();
        if (!in.ok) return false;

        putVar(out, key);
        putVar(out, type);

        (void)in.u8();               // 丢弃 legacy_type
        if (!in.ok) return false;

        size_t start = in.p;
        if (!skipMetadataValue(in, type)) return false;
        out.append(in.d, start, in.p - start);
    }
    return in.ok;
}

inline bool metadata1001To2168(Reader& in, std::string& out) {
    uint64_t count = in.var();
    if (!in.ok) return false;
    putVar(out, count);

    for (uint64_t i = 0; i < count; i++) {
        uint64_t key  = in.var();
        uint64_t type = in.var();
        if (!in.ok) return false;

        putVar(out, key);
        putVar(out, type);
        out.push_back(static_cast<char>(type & 0xFF));  // legacy_type = type 副本

        size_t start = in.p;
        if (!skipMetadataValue(in, type)) return false;
        out.append(in.d, start, in.p - start);
    }
    return in.ok;
}

// ---------------- 包级转换（2168 -> 1001） ----------------

// varint 长度前缀 + UTF-8
inline bool skipString(Reader& in) {
    uint64_t len = in.var();
    if (!in.ok) return false;
    in.skip(static_cast<size_t>(len));
    return in.ok;
}

// add_entity(13)，字段序列来自 v557/AddEntitySerializer_v557：
//   uniqueEntityId   zigzag64
//   runtimeEntityId  varuint64
//   identifier       string
//   position         vec3f   (12)
//   motion           vec3f   (12)
//   rotation         vec2f   (8)
//   headRotation     f32     (4)
//   bodyRotation     f32     (4)
//   attributes       count(varuint) + N × (name string + min f32 + value f32 + max f32)
//   metadata         ← 差异点
//   properties / links 原样拷贝
inline bool fixAddEntity(std::string& data) {
    Reader in(data, 0, data.size());

    (void)in.zigzag();        // uniqueEntityId
    (void)in.var();           // runtimeEntityId
    if (!skipString(in)) return false;   // identifier
    in.skip(12);              // position
    in.skip(12);              // motion
    in.skip(8);               // rotation (vec2f)
    in.skip(4);               // headRotation
    in.skip(4);               // bodyRotation
    if (!in.ok) return false;

    uint64_t attrCount = in.var();
    if (!in.ok) return false;
    for (uint64_t i = 0; i < attrCount; i++) {
        if (!skipString(in)) return false;  // attribute name
        in.skip(12);                        // min + value + max
        if (!in.ok) return false;
    }

    std::string out;
    out.reserve(data.size() + 32);
    out.append(data, 0, in.p);

    if (!metadata2168To1001(in, out)) return false;

    in.copy(in.end - in.p, out);           // properties + links
    if (!in.ok) return false;

    data = std::move(out);
    return true;
}

// add_player(12)，字段序列来自 v557/AddPlayerSerializer_v557：
//   uuid             16 字节
//   username         string
//   runtimeEntityId  varuint64
//   platformChatId   string
//   position         vec3f   (12)
//   motion           vec3f   (12)
//   rotation         vec3f   (12)
//   hand             Item    ← 与 mob_equipment 同一个 NetworkItemStackDescriptor
//   gameType         zigzag32
//   metadata         ← 差异点
//   properties / abilities / links / deviceId / buildPlatform 原样拷贝
inline bool fixAddPlayer(std::string& data) {
    Reader in(data, 0, data.size());

    in.skip(16);              // uuid
    if (!skipString(in)) return false;   // username
    (void)in.var();           // runtimeEntityId
    if (!skipString(in)) return false;   // platformChatId
    in.skip(12);              // position
    in.skip(12);              // motion
    in.skip(12);              // rotation (vec3f)
    if (!in.ok) return false;

    std::string out;
    out.reserve(data.size() + 16);
    out.append(data, 0, in.p);

    if (!item2168To1001(in, out)) return false;   // hand

    int64_t gameType = in.zigzag();
    if (!in.ok) return false;
    putZigZag(out, gameType);

    if (!metadata2168To1001(in, out)) return false;

    in.copy(in.end - in.p, out);                  // 尾部原样
    if (!in.ok) return false;

    data = std::move(out);
    return true;
}

// mob_equipment(31)：runtimeId(varlong) + item + slot(u8) + selected(u8) + window(u8)
inline bool fixMobEquipment(std::string& data) {
    Reader in(data, 0, data.size());
    (void)in.var();                  // runtimeId
    if (!in.ok) return false;

    std::string out;
    out.reserve(data.size() + 8);
    out.append(data, 0, in.p);       // runtimeId 原样
    if (!item2168To1001(in, out)) return false;
    in.copy(in.end - in.p, out);     // 剩余 3 个 u8
    if (!in.ok) return false;

    data = std::move(out);
    return true;
}

// mob_armor_equipment(32)：runtimeId(varlong) + 4 × item
inline bool fixMobArmorEquipment(std::string& data) {
    Reader in(data, 0, data.size());
    (void)in.var();
    if (!in.ok) return false;

    std::string out;
    out.reserve(data.size() + 8);
    out.append(data, 0, in.p);
    for (int i = 0; i < 4; i++) {
        if (!item2168To1001(in, out)) return false;
    }
    data = std::move(out);
    return true;
}

// add_item_entity(15)：runtimeId(varlong) + item + position(vec3f) + metadata + ...
inline bool fixAddItemEntity(std::string& data) {
    Reader in(data, 0, data.size());
    (void)in.var();
    if (!in.ok) return false;

    std::string out;
    out.reserve(data.size() + 16);
    out.append(data, 0, in.p);
    if (!item2168To1001(in, out)) return false;

    in.copy(12, out);                // position
    if (!in.ok) return false;

    if (!metadata2168To1001(in, out)) return false;
    in.copy(in.end - in.p, out);     // 尾部剩余
    if (!in.ok) return false;

    data = std::move(out);
    return true;
}

// move_player(19)：
//   runtimeId(varlong) position(vec3f) rotation(vec3f) mode(u8)
//   onGround(bool) ridingId(varlong)
//   2168: [optional(bool) cause(li32) entityType(li32)]
//   1001: [cause(li32) entityType(li32)]     ← 无前缀
//   tick(varlong)
inline bool fixMovePlayer(std::string& data) {
    Reader in(data, 0, data.size());
    (void)in.var();                  // runtimeId
    in.skip(12);                     // position
    in.skip(12);                     // rotation
    uint8_t mode = in.u8();
    (void)in.u8();                   // onGround
    (void)in.var();                  // ridingId
    if (!in.ok) return false;

    constexpr uint8_t kModeTeleport = 2;
    if (mode != kModeTeleport) return false;   // 只有 teleport 分支有差异

    std::string out;
    out.reserve(data.size());
    out.append(data, 0, in.p);

    uint8_t present = in.u8();       // 2168 的 option 前缀
    if (!in.ok) return false;

    if (present) {
        in.copy(8, out);             // cause(li32) + entityType(li32)
        if (!in.ok) return false;
    }
    in.copy(in.end - in.p, out);     // tick + 剩余
    if (!in.ok) return false;

    data = std::move(out);
    return true;
}

} // namespace jercpld1001
