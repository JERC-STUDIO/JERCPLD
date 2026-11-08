#!/usr/bin/env node
// 对比 2193 (1.26.51) 与 2225 (1.26.60) 的包结构差异
const base = './node_modules/minecraft-data/minecraft-data/data/bedrock/'
const oldP = require(base + '1.26.51/protocol.json')
const newP = require(base + '1.26.60/protocol.json')

const packets = [
  'start_game', 'level_chunk', 'add_player', 'add_entity', 'set_actor_data',
  'player_list', 'player_skin', 'available_commands', 'inventory_content',
  'inventory_slot', 'item_stack_response', 'biome_definition_list',
  'creative_content', 'player_armor_damage', 'animate', 'set_time',
  'set_difficulty', 'play_status', 'chunk_radius_updated', 'network_settings',
  'request_network_settings', 'login', 'move_player', 'set_health', 'respawn'
]

function fields (proto, name) {
  const t = proto.types['packet_' + name]
  if (!Array.isArray(t) || t[0] !== 'container') return null
  const map = new Map()
  for (const f of t[1]) map.set(f.name, JSON.stringify(f.type))
  return map
}

for (const name of packets) {
  const a = fields(oldP, name)
  const b = fields(newP, name)
  if (!a || !b) { console.log(`### ${name}: schema missing (old=${!!a} new=${!!b})`); continue }
  const added = [...b.keys()].filter(k => !a.has(k))
  const removed = [...a.keys()].filter(k => !b.has(k))
  const changed = [...a.keys()].filter(k => b.has(k) && a.get(k) !== b.get(k))
  if (!added.length && !removed.length && !changed.length) continue
  console.log(`### ${name}`)
  if (added.length) console.log('  + ' + added.map(k => `${k}: ${b.get(k)}`).join('\n  + '))
  if (removed.length) console.log('  - ' + removed.map(k => `${k}: ${a.get(k)}`).join('\n  - '))
  for (const k of changed) {
    console.log(`  ~ ${k}\n    2193: ${a.get(k)}\n    2225: ${b.get(k)}`)
  }
}
