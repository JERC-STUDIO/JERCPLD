// 物品栈编解码单测：2168(1.26.40) <-> 1001(1.26.30~26.33)
// 编译: g++ -std=c++20 -Wall -Wextra -O1 test_item1001.cpp -o test_item1001
//
// 线格式依据 CloudburstMC/Protocol 3.0：
//   v975  (1001 继承) : networkId li16 + count lu16 + damage varint + hasStack u8
//                        + [variantType varint + stackId zigzag] + blockRuntimeId varint + extra
//   v2168             : 同上，但 hasStack 为真时没有 variantType
//   air 两边都是 8 字节，逐字节相同。
#include "../../mod/JERCPLD.Mod/src/JercpldPtl1001.h"

#include <cstdio>
#include <string>

using namespace jercpld1001;

static std::string hex(std::string const& s) {
    static char const* d = "0123456789abcdef";
    std::string        r;
    for (unsigned char c : s) {
        r.push_back(d[c >> 4]);
        r.push_back(d[c & 0x0F]);
        r.push_back(' ');
    }
    return r;
}

static int gFail = 0;

static void expect(bool cond, char const* what, std::string const& got, std::string const& want) {
    if (cond) {
        printf("  PASS  %s\n", what);
    } else {
        printf("  FAIL  %s\n        got = %s\n        want= %s\n", what, hex(got).c_str(), hex(want).c_str());
        ++gFail;
    }
}

static std::string to1001(std::string const& in, bool* ok = nullptr) {
    Reader      r(in, 0, in.size());
    std::string out;
    bool        k = item2168To1001(r, out);
    if (ok) *ok = k && r.p == in.size();
    return out;
}

static std::string to2168(std::string const& in, bool* ok = nullptr) {
    Reader      r(in, 0, in.size());
    std::string out;
    bool        k = item1001To2168(r, out);
    if (ok) *ok = k && r.p == in.size();
    return out;
}

int main() {
    printf("[1] air: 2168 八字节 -> 1001 八字节（原样，不短路）\n");
    {
        std::string in("\x00\x00"    // networkId li16 = 0
                       "\x00\x00"    // count lu16 = 0
                       "\x00"        // damage
                       "\x00"        // hasStack
                       "\x00"        // blockRuntimeId
                       "\x00",       // extraLen
                       8);
        bool        ok  = false;
        std::string out = to1001(in, &ok);
        expect(ok, "解析完整", out, in);
        expect(out == in, "字节一致（air 无需转换）", out, in);
        expect(out.size() == 8, "长度仍是 8", out, in);
    }

    printf("[2] 普通物品: networkId=5 count=3\n");
    {
        std::string in("\x05\x00"    // networkId = 5
                       "\x03\x00"    // count = 3
                       "\x00"        // damage
                       "\x00"        // hasStack = 0
                       "\x00"        // blockRuntimeId
                       "\x00",       // extraLen
                       8);
        bool        ok  = false;
        std::string out = to1001(in, &ok);
        expect(ok, "解析完整", out, in);
        expect(out == in, "字节一致（无 stack_id 时两版本相同）", out, in);
    }

    printf("[3] 带 stack_id: 2168 无 variant -> 1001 补 variant(0)\n");
    {
        std::string in("\x05\x00"        // networkId = 5
                       "\x03\x00"        // count = 3
                       "\x00"            // damage
                       "\x01"            // hasStack = 1
                       "\x0e"            // stackId zigzag(7)
                       "\x02"            // blockRuntimeId = 2
                       "\x02"            // extraLen = 2
                       "\xaa\xbb",       // extra
                       11);
        bool        ok  = false;
        std::string out = to1001(in, &ok);
        std::string want("\x05\x00\x03\x00\x00\x01\x00\x0e\x02\x02\xaa\xbb", 12);
        expect(ok, "解析完整", out, want);
        expect(out == want, "字节一致（多了 variantType=0）", out, want);
        expect(out.size() == in.size() + 1, "长度 +1", out, want);
    }

    printf("[4] 往返: 1001 -> 2168 -> 1001 保值\n");
    {
        std::string in("\x05\x00\x03\x00\x00\x01\x00\x0e\x02\x02\xaa\xbb", 12);
        bool        k1 = false, k2 = false;
        std::string mid  = to2168(in, &k1);
        std::string back = to1001(mid, &k2);
        expect(k1 && k2, "两段都解析完整", back, in);
        expect(back == in, "往返一致", back, in);
    }

    printf("[5] air 往返: 2168 -> 1001 -> 2168 保值\n");
    {
        std::string in("\x00\x00\x00\x00\x00\x00\x00\x00", 8);
        bool        k1 = false, k2 = false;
        std::string mid  = to1001(in, &k1);
        std::string back = to2168(mid, &k2);
        expect(k1 && k2, "两段都解析完整", back, in);
        expect(back == in, "往返一致", back, in);
    }

    printf("\n%s (fail=%d)\n", gFail == 0 ? "ALL PASS" : "FAILED", gFail);
    return gFail == 0 ? 0 : 1;
}
