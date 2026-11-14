// JercpldPtl1001.h —— 1001(26.30~26.33) <-> 2168(26.40) s2c 结构转换（PoC）
//
// 背景：1.26.33 是 1.26.30 的热修复，协议号同为 1001，线格式与 26.30 一致。
// 与 2168 的差异集中在 6 个 s2c 包，其中最麻烦的是物品栈 Item(1001) -> ItemV4(2168)。
//
// 本文件不依赖 LeviLamina，只依赖标准库，可单独用 g++ 编译测试。
// 接入方式：在 JerCpldMod.cpp 里 #include 本文件，并在
// translateOutboundPacket() 的 peerProtocol < 2168 分支中按 packetId 调用。
//
// 注意：metadata 的 legacy_type 字节是否为真实线格式仍待抓包确认，
// 因此 metadata1001To2168() 通过 modelLegacyType 参数控制，默认 false。
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

    // 无符号 LEB128，最多 10 字节
    uint64_t var() {
        uint64_t v = 0;
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

inline void putLi16(std::string& out, int16_t v) {
    out.push_back(static_cast<char>(static_cast<uint16_t>(v) & 0xFF));
    out.push_back(static_cast<char>((static_cast<uint16_t>(v) >> 8) & 0xFF));
}

inline void putLu16(std::string& out, uint16_t v) {
    out.push_back(static_cast<char>(v & 0xFF));
    out.push_back(static_cast<char>((v >> 8) & 0xFF));
}

// ---------------- Item(1001) -> ItemV4(2168) ----------------
//
// 1001: network_id(zigzag32) [0 => 结束]
//       count(lu16) metadata(varint) has_stack_id(u8)
//       [stack_id(zigzag32)] block_runtime_id(zigzag32) extra(varint len + bytes)
// 2168: network_id(li16) count(lu16) metadata(varint) has_stack_id(bool)
//       [stack_id(zigzag32)] block_runtime_id(varint) extra(varint len + bytes)
//
// 关键差异：network_id 由 varint 变定长 li16；air 由 1 字节变 8 字节（补齐尾部字段）。
inline bool item1001To2168(Reader& in, std::string& out) {
    int64_t networkId = in.zigzag();
    if (!in.ok) return false;

    putLi16(out, static_cast<int16_t>(networkId));

    if (networkId == 0) {         // air：2168 仍写满尾部字段
        putLu16(out, 0);          // count
        putVar(out, 0);           // metadata
        out.push_back('\0');      // has_stack_id = false
        putVar(out, 0);           // block_runtime_id
        putVar(out, 0);           // extra 长度 = 0
        return true;
    }

    uint16_t count = in.lu16();
    uint64_t metadata = in.var();
    uint8_t  hasStack = in.u8();
    if (!in.ok) return false;

    putLu16(out, count);
    putVar(out, metadata);
    out.push_back(hasStack ? '\1' : '\0');
    if (hasStack) {
        int64_t stackId = in.zigzag();
        if (!in.ok) return false;
        putZigZag(out, stackId);
    }

    int64_t blockRuntimeId = in.zigzag();
    if (!in.ok) return false;
    putVar(out, static_cast<uint64_t>(blockRuntimeId));

    uint64_t extraLen = in.var();
    if (!in.ok) return false;
    putVar(out, extraLen);
    if (extraLen > 0) {
        if (!in.has(static_cast<size_t>(extraLen))) return false;
        in.copy(static_cast<size_t>(extraLen), out);
    }
    return in.ok;
}

// ---------------- ItemV4(2168) -> Item(1001) ----------------
inline bool item2168To1001(Reader& in, std::string& out) {
    int16_t networkId = in.li16();
    if (!in.ok) return false;

    putZigZag(out, networkId);

    if (networkId == 0) {
        // ItemV4 没有 network_id==0 的短路分支，air 仍然写满尾部字段（共 8 字节）。
        // 必须把它们消费掉，否则紧随其后的字段会整体错位 6 字节。
        uint16_t count = in.lu16();
        uint64_t metadata = in.var();
        uint8_t  hasStack = in.u8();
        uint64_t blockRuntimeId = in.var();
        uint64_t extraLen = in.var();
        (void)count;
        (void)metadata;
        (void)hasStack;
        (void)blockRuntimeId;
        if (!in.ok) return false;
        if (extraLen > 0) {
            if (!in.has(static_cast<size_t>(extraLen))) return false;
            in.p += static_cast<size_t>(extraLen);
        }
        return in.ok;   // 1001 侧只输出 1 字节
    }

    uint16_t count = in.lu16();
    uint64_t metadata = in.var();
    uint8_t  hasStack = in.u8();
    if (!in.ok) return false;

    putLu16(out, count);
    putVar(out, metadata);
    out.push_back(hasStack ? '\1' : '\0');
    if (hasStack) {
        int64_t stackId = in.zigzag();
        if (!in.ok) return false;
        putZigZag(out, stackId);
    }

    uint64_t blockRuntimeId = in.var();
    if (!in.ok) return false;
    putZigZag(out, static_cast<int64_t>(blockRuntimeId));

    uint64_t extraLen = in.var();
    if (!in.ok) return false;
    putVar(out, extraLen);
    if (extraLen > 0) {
        if (!in.has(static_cast<size_t>(extraLen))) return false;
        in.copy(static_cast<size_t>(extraLen), out);
    }
    return in.ok;
}

// ---------------- metadata 字典（暂不实现） ----------------
//
// 1001: key(varint) type(varint) value(...)
// 2168: key(varint) type(varint) legacy_type(u8) value(...)
//
// 差异是「每个条目多 1 字节 legacy_type」。但该字节在真实线格式中是否存在尚未确认：
// 仓库此前已因同类问题给 1.26.40/45 打过补丁（docs/STATE.json:91 记载
// 「官方文档确认 2168/2169/2193 wire 一致，社区 schema 建模错误」）。
//
// 判定方法：抓一帧 12 add_player，按 1001 schema 解析 metadata 数组，
// 若条目边界整体错位 1 字节/条，则 legacy_type 真实存在；否则是 schema 假差异，不需要翻译。
//
// 确认后再补：
//   bool metadata1001To2168(Reader& in, std::string& out);
//   bool metadata2168To1001(Reader& in, std::string& out);
// 两者都需要 skipMetadataValue()，按 type 跳过 byte/short/varint/float/string/nbt/vec3i/long/vec3f。

} // namespace jercpld1001
