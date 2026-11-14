#!/usr/bin/env node
// 穷举对比两个客户端版本（默认 2193(1.26.51) 与 2225(1.26.60)）的包结构差异
// 覆盖全部 packet_* 类型（含递归嵌套类型与枚举值），默认只输出有差异的条目。
// 用法：
//   node diff-schemas.js                     # 1.26.51 vs 1.26.60
//   node diff-schemas.js 1.26.45 1.26.51     # 指定版本对
//   node diff-schemas.js 1.26.45 1.26.51 start_game   # 只看指定包
const base = './node_modules/minecraft-data/minecraft-data/data/bedrock/'
const OLD_VER = process.argv[2] || '1.26.51'
const NEW_VER = process.argv[3] || '1.26.60'
const oldP = require(base + OLD_VER + '/protocol.json')
const newP = require(base + NEW_VER + '/protocol.json')
console.log(`# ${OLD_VER} vs ${NEW_VER}`)

const MAX_DEPTH = 14

// 把类型树规范化成字符串，用于快速判等；named type 递归展开（带环检测）
function canon (proto, type, depth, seen) {
  if (depth > MAX_DEPTH) return '"<deep>"'
  if (typeof type === 'string') {
    const t = proto.types[type]
    if (t === undefined || seen.includes(type)) return JSON.stringify(type)
    return 'T(' + type + '=' + canon(proto, t, depth + 1, seen.concat(type)) + ')'
  }
  if (!Array.isArray(type)) return JSON.stringify(type)
  const [kind, ...rest] = type
  if (kind === 'container') {
    const fields = rest[0] || []
    return 'container{' + fields.map(f => f.name + ':' + canon(proto, f.type, depth + 1, seen)).join(',') + '}'
  }
  if (kind === 'enum') {
    const spec = rest[0] || {}
    const values = spec.values || []
    return 'enum[' + values.map(v => Array.isArray(v) ? v[0] + '|' + canon(proto, v[1], depth + 1, seen) : JSON.stringify(v)).join(',') + ']'
  }
  return kind + '(' + rest.map(x => canon(proto, x, depth + 1, seen)).join(',') + ')'
}

function sameType (aType, bType) {
  return canon(oldP, aType, 0, []) === canon(newP, bType, 0, [])
}

// 在容器层面递归定位差异
function diffType (name, aType, bType, path, out, depth) {
  if (depth > MAX_DEPTH) return
  const a = typeof aType === 'string' ? oldP.types[aType] : aType
  const b = typeof bType === 'string' ? newP.types[bType] : bType
  if (a === undefined || b === undefined) {
    out.push(`${path}: 类型无法解析 old=${!!a} new=${!!b}`)
    return
  }
  if (!Array.isArray(a) || !Array.isArray(b)) {
    out.push(`${path}: ${JSON.stringify(aType)} -> ${JSON.stringify(bType)}`)
    return
  }
  if (a[0] !== b[0]) {
    out.push(`${path}: 结构类型 ${a[0]} -> ${b[0]}`)
    return
  }
  if (a[0] === 'container') {
    const fa = a[1] || []
    const fb = b[1] || []
    const namesA = fa.map(f => f.name)
    const namesB = fb.map(f => f.name)
    for (const n of namesA) if (!namesB.includes(n)) out.push(`${path}.${n}: 字段删除`)
    for (const n of namesB) if (!namesA.includes(n)) {
      const idx = namesB.indexOf(n)
      const prev = idx > 0 ? namesB[idx - 1] : '<head>'
      out.push(`${path}.${n}: 字段新增（位于 ${prev} 之后） type=${JSON.stringify(fb[idx].type)}`)
    }
    const commonA = namesA.filter(n => namesB.includes(n))
    const commonB = namesB.filter(n => namesA.includes(n))
    if (commonA.join(',') !== commonB.join(',')) {
      out.push(`${path}: 字段顺序变化 ${commonA.join(',')} -> ${commonB.join(',')}`)
    }
    for (const n of commonA) {
      const ta = fa.find(f => f.name === n).type
      const tb = fb.find(f => f.name === n).type
      if (!sameType(ta, tb)) diffType(n, ta, tb, path + '.' + n, out, depth + 1)
    }
    return
  }
  if (a[0] === 'enum' || a[0] === 'mapper' || a[0] === 'bitflags') {
    const mapA = new Map()
    const mapB = new Map()
    if (a[0] === 'enum') {
      for (const v of (a[1] && a[1].values) || []) mapA.set(Array.isArray(v) ? v[0] : v, Array.isArray(v) ? v[1] : v)
      for (const v of (b[1] && b[1].values) || []) mapB.set(Array.isArray(v) ? v[0] : v, Array.isArray(v) ? v[1] : v)
    } else {
      for (const [k, v] of Object.entries((a[1] && a[1].mappings) || {})) mapA.set(k, v)
      for (const [k, v] of Object.entries((b[1] && b[1].mappings) || {})) mapB.set(k, v)
    }
    const lines = []
    for (const [k, v] of mapA) {
      if (!mapB.has(k)) lines.push(`删除 ${k}=${JSON.stringify(v)}`)
      else if (JSON.stringify(v) !== JSON.stringify(mapB.get(k))) lines.push(`${k}: ${JSON.stringify(v)} -> ${JSON.stringify(mapB.get(k))}`)
    }
    for (const [k, v] of mapB) if (!mapA.has(k)) lines.push(`新增 ${k}=${JSON.stringify(v)}`)
    if (lines.length) out.push(`${path}: ${a[0]} 变化（${lines.length} 项）` + (lines.length <= 12 ? '\n      ' + lines.join('\n      ') : '\n      ' + lines.slice(0, 12).join('\n      ') + `\n      ... 其余 ${lines.length - 12} 项`))
    return
  }
  if (a[0] === 'switch') {
    const fa = (a[1] && a[1].fields) || {}
    const fb = (b[1] && b[1].fields) || {}
    const keysA = Object.keys(fa)
    const keysB = Object.keys(fb)
    for (const k of keysA) if (!keysB.includes(k)) out.push(`${path}: switch 分支删除 ${k}`)
    for (const k of keysB) if (!keysA.includes(k)) out.push(`${path}: switch 分支新增 ${k}`)
    for (const k of keysA.filter(x => keysB.includes(x))) {
      if (!sameType(fa[k], fb[k])) diffType(k, fa[k], fb[k], `${path}[${k}]`, out, depth + 1)
    }
    return
  }
  if (a[0] === 'option' || a[0] === 'array' || a[0] === 'pstring' || a[0] === 'buffer') {
    const ra = a.slice(1)
    const rb = b.slice(1)
    if (a[0] === 'option' || a[0] === 'array') {
      if (!sameType(a[1], b[1])) diffType('inner', a[1], b[1], path + '[]', out, depth + 1)
      return
    }
    if (JSON.stringify(ra) !== JSON.stringify(rb)) out.push(`${path}: ${a[0]} 参数变化 ${JSON.stringify(ra)} -> ${JSON.stringify(rb)}`)
    return
  }
  // 其余结构：按参数逐项比较
  const ra = a.slice(1)
  const rb = b.slice(1)
  if (JSON.stringify(ra) !== JSON.stringify(rb)) out.push(`${path}: ${a[0]} 参数变化 ${JSON.stringify(ra)} -> ${JSON.stringify(rb)}`)
}

const filter = process.argv[4]

function packetIds (proto) {
  const t = proto.types.mcpe_packet
  const map = (t && t[1] && t[1][0] && t[1][0].type && t[1][0].type[1] && t[1][0].type[1].mappings) || {}
  const out = {}
  for (const [id, name] of Object.entries(map)) out[name] = id
  return out
}
const idsOld = packetIds(oldP)
const idsNew = packetIds(newP)

const names = Object.keys(newP.types)
  .filter(k => k.startsWith('packet_'))
  .map(k => k.slice(7))
  .filter(n => !filter || n.includes(filter))

const onlyNew = names.filter(n => oldP.types['packet_' + n] === undefined)
const onlyOld = Object.keys(oldP.types)
  .filter(k => k.startsWith('packet_') && newP.types[k] === undefined)
  .map(k => k.slice(7))

if (onlyNew.length) console.log('## 2225 新增包\n  ' + onlyNew.map(n => `${idsNew[n]}:${n}`).join(', '))
if (onlyOld.length) console.log('## 2225 删除包\n  ' + onlyOld.map(n => `${idsOld[n]}:${n}`).join(', '))

let changed = 0
for (const n of names) {
  const a = oldP.types['packet_' + n]
  const b = newP.types['packet_' + n]
  if (a === undefined || b === undefined) continue
  if (sameType(a, b)) continue
  const out = []
  diffType('packet_' + n, 'packet_' + n, 'packet_' + n, n, out, 0)
  if (!out.length) out.push('（规范化不同但未定位到字段，需人工复核）')
  changed++
  console.log(`### ${n} (id=${idsNew[n]})`)
  for (const line of out) console.log('  - ' + line)
}
console.log(`\n共 ${names.length} 个同名包，其中 ${changed} 个结构有差异`)
