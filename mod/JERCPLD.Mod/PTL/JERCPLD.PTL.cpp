// JERCPLD.PTL —— 单文件翻译规则模块（纯 C ABI）
//
// 设计：DLL 只承载数据（内嵌规则 JSON），执行引擎仍在主插件里。
// 这样主插件可以统一做边界检查、越界回滚、A→B→A 自检，
// 且跨 DLL 边界只传 const char*，不传 STL/C++ 类型。
//
// 对外导出（全部 extern "C"）：
//   uint32_t jercpld_ptl_api_version();
//   uint32_t jercpld_ptl_pair_count();
//   const char* jercpld_ptl_pair_name(uint32_t index);
//   const char* jercpld_ptl_pair_rules(uint32_t index);  // NUL 结尾的 JSON
//   int jercpld_ptl_self_check();                        // 0 = OK

#include <cstdint>

#include "ptl_rules_generated.h"

extern "C" {

__declspec(dllexport) uint32_t jercpld_ptl_api_version() {
    return 1u;
}

__declspec(dllexport) uint32_t jercpld_ptl_pair_count() {
    return kJercpldPtlPairCount;
}

__declspec(dllexport) const char* jercpld_ptl_pair_name(uint32_t index) {
    if (index >= kJercpldPtlPairCount) return nullptr;
    return kJercpldPtlPairs[index].name;
}

__declspec(dllexport) const char* jercpld_ptl_pair_rules(uint32_t index) {
    if (index >= kJercpldPtlPairCount) return nullptr;
    return kJercpldPtlPairs[index].rules;
}

__declspec(dllexport) int jercpld_ptl_self_check() {
    if (kJercpldPtlPairCount == 0) return 1;
    for (uint32_t i = 0; i < kJercpldPtlPairCount; ++i) {
        if (!kJercpldPtlPairs[i].name || !kJercpldPtlPairs[i].rules) return 2;
        // 规则必须是 JSON 对象，至少以 '{' 开头。
        if (kJercpldPtlPairs[i].rules[0] != '{') return 3;
    }
    return 0;
}

} // extern "C"
