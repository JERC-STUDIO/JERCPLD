// 物品栈编解码单测：1001(26.30~26.33) <-> 2168(26.40)
// 编译: g++ -std=c++20 -Wall -Wextra -O1 test_item1001.cpp -o test_item1001
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

static std::string to2168(std::string const& in, bool* ok = nullptr) {
    Reader      r(in, 0, in.size());
    std::string out;
    bool        k = item1001To2168(r, out);
    if (ok) *ok = k && r.p == in.size();
    return out;
}

static std::string to1001(std::string const& in, bool* ok = nullptr) {
    Reader      r(in, 0, in.size());
    std::string out;
    bool        k = item2168To1001(r, out);
    if (ok) *ok = k && r.p == in.size();
    return out;
}

int main() {
    printf("[1] air item: 1001 单字节 00 -> 2168 八字节\n");
    {
        std::string in("\x00", 1);
        bool        ok = false;
        std::string out = to2168(in, &ok);
        std::string want("\x00\x00"  // network_id li16 = 0
                         "\x00\x00"  // count
                         "\x00"      // metadata
                         "\x00"      // has_stack_id
                         "\x00"      // block_runtime_id
                         "\x00",     // extra len
                         8);
        expect(ok, "解析完整", out, want);
        expect(out == want, "字节一致", out, want);
    }

    printf("[2] 普通物品: id=5 count=3\n");
    {
        std::string in("\x0a"        // zigzag32(5)
                       "\x03\x00"    // count lu16 = 3
                       "\x00"        // metadata
                       "\x00"        // has_stack_id = 0
                       "\x00"        // block_runtime_id zigzag(0)
                       "\x00",       // extra len 0
                       7);
        bool        ok = false;
        std::string out = to2168(in, &ok);
        std::string want("\x05\x00\x03\x00\x00\x00\x00\x00", 8);
        expect(ok, "解析完整", out, want);
        expect(out == want, "字节一致", out, want);
    }

    printf("[3] 带 stack_id 与 extra\n");
    {
        std::string in("\x0a"        // network_id=5
                       "\x03\x00"    // count=3
                       "\x00"        // metadata
                       "\x01"        // has_stack_id=1
                       "\x0e"        // stack_id zigzag32(7)
                       "\x04"        // block_runtime_id zigzag32(2)
                       "\x02\xaa\xbb", // extra len=2
                       10);
        bool        ok = false;
        std::string out = to2168(in, &ok);
        std::string want("\x05\x00\x03\x00\x00\x01\x0e\x02\x02\xaa\xbb", 11);
        expect(ok, "解析完整", out, want);
        expect(out == want, "字节一致", out, want);
    }

    printf("[4] 往返: 2168 air -> 1001\n");
    {
        std::string in("\x00\x00\x00\x00\x00\x00\x00\x00", 8);
        bool        ok = false;
        std::string out = to1001(in, &ok);
        std::string want("\x00", 1);
        expect(ok, "解析完整", out, want);
        expect(out == want, "字节一致", out, want);
    }

    printf("[5] 往返: 1001 -> 2168 -> 1001 保值\n");
    {
        std::string in("\x0a\x03\x00\x00\x01\x0e\x04\x02\xaa\xbb", 10);
        bool        k1 = false, k2 = false;
        std::string mid  = to2168(in, &k1);
        std::string back = to1001(mid, &k2);
        expect(k1 && k2, "两段都解析完整", back, in);
        expect(back == in, "往返一致", back, in);
    }

    printf("[6] 负 network_id（li16 有符号）应被拒或截断可观测\n");
    {
        // 2168 侧 network_id = -1(0xFFFF) 不在合法物品表内，仅验证不越界
        std::string in("\xff\xff\x01\x00\x00\x00\x00\x00", 8);
        bool        ok = false;
        std::string out = to1001(in, &ok);
        printf("  INFO  ok=%d out=%s\n", static_cast<int>(ok), hex(out).c_str());
    }

    printf("\n%s (fail=%d)\n", gFail == 0 ? "ALL PASS" : "FAILED", gFail);
    return gFail == 0 ? 0 : 1;
}
