#!/usr/bin/env node
/*
 * JERCPLD 自动验证客户端
 * 流程：握手 -> 登录 -> StartGame -> 区块 -> PASS/FAIL
 *
 * 用法：
 *   node verify.js --host 127.0.0.1 --port 19132 --version 1.26.51 [--transport raknet|nethernet] [--offline]
 *
 * 退出码：
 *   0  PASS（收到 StartGame 且区块数达标）
 *   2  登录被拒（disconnect / kick）
 *   3  超时，未收到 StartGame
 *   4  已收到 StartGame，但没收到区块
 *   1  其它失败（连接错误等）
 */
const { createClient } = require('bedrock-protocol')

function parseArgs (argv) {
  const out = {
    host: '127.0.0.1',
    port: 19132,
    version: '1.26.51',
    transport: 'raknet',
    username: 'JERCPLDVerify',
    offline: true,
    timeout: 60000,
    requireChunks: 0,
    jsonOut: null,
    dwellMs: 0,
    packetLog: null,
    move: false
  }
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i]
    const next = () => argv[++i]
    switch (a) {
      case '--host': out.host = next(); break
      case '--port': out.port = Number(next()); break
      case '--version': out.version = next(); break
      case '--transport': out.transport = next(); break
      case '--username': out.username = next(); break
      case '--offline': out.offline = next() !== 'false'; break
      case '--timeout': out.timeout = Number(next()); break
      case '--require-chunks': out.requireChunks = Number(next()); break
      case '--json-out': out.jsonOut = next(); break
      case '--network-id': out.networkId = BigInt(next()); break
      case '--dump-login': out.dumpLogin = true; break
      case '--spoof-game-version': out.spoofGameVersion = next(); break
      case '--dwell-ms': out.dwellMs = Number(next()); break
      case '--packet-log': out.packetLog = next(); break
      case '--move': out.move = true; break
      case '--help':
        console.log('node verify.js --host <ip> --port <port> --version <mc-version> [--transport raknet|nethernet] [--offline false] [--timeout ms]')
        process.exit(0)
        break
      default:
        console.error(`未知参数: ${a}`)
        process.exit(1)
    }
  }
  if (!['raknet', 'nethernet'].includes(out.transport)) {
    console.error(`不支持的 transport: ${out.transport}`)
    process.exit(1)
  }
  return out
}

const args = parseArgs(process.argv)
const t0 = Date.now()
const stages = {
  connected: false,
  loginSent: false,
  joined: false,
  startGame: false,
  spawn: false,
  chunks: 0,
  startGameAt: null,
  firstChunkAt: null,
  compression: null,
  levelName: null,
  blockProperties: null,
  kick: null,
  error: null
}
let finished = false
let client = null

function elapsed () {
  return `${((Date.now() - t0) / 1000).toFixed(1)}s`
}

function log (stage, detail = '') {
  console.log(`[${elapsed()}] ${stage}${detail ? ' | ' + detail : ''}`)
}

function finish (code, reason) {
  if (finished) return
  finished = true
  clearTimeout(timer)
  const result = {
    ok: code === 0,
    code,
    reason,
    target: {
      host: args.host,
      port: args.port,
      mcVersion: args.version,
      transport: args.transport
    },
    stages: {
      connected: stages.connected,
      loginSent: stages.loginSent,
      joined: stages.joined,
      startGame: stages.startGame,
      spawn: stages.spawn,
      chunks: stages.chunks
    },
    timing: {
      startGameMs: stages.startGameAt ? stages.startGameAt - t0 : null,
      firstChunkMs: stages.firstChunkAt ? stages.firstChunkAt - t0 : null
    },
    compression: stages.compression,
    levelName: stages.levelName,
    blockPropertyCount: stages.blockProperties,
    kick: stages.kick,
    error: stages.error,
    elapsedMs: Date.now() - t0
  }
  console.log('')
  console.log(`RESULT ${result.ok ? 'PASS' : 'FAIL'} code=${code} reason=${reason}`)
  console.log(JSON.stringify(result, null, 2))
  if (args.jsonOut) {
    require('fs').writeFileSync(args.jsonOut, JSON.stringify(result, null, 2))
  }
  try { client?.close() } catch {}
  process.exit(code)
}

function scheduleDwell () {
  if (args.dwellMs <= 0 || dwellScheduled) return
  dwellScheduled = true
  log('DWELL', `keeping connection for ${args.dwellMs} ms`)
  if (args.move) startMovement()
  setTimeout(() => finish(0, `dwell ${args.dwellMs} ms complete`), args.dwellMs)
}

let dwellScheduled = false
let moveTimer = null

function startMovement () {
  const start = client.startGameData?.player_position || { x: 0, y: 64, z: 0 }
  let tick = 0n
  let step = 0
  moveTimer = setInterval(() => {
    try {
      step++
      const x = start.x + (step % 40) * 0.1
      client.queue('player_auth_input', {
        pitch: 0,
        yaw: 0,
        position: { x, y: start.y, z: start.z },
        move_vector: { x: 0.1, z: 0 },
        head_yaw: 0,
        input_data: [],
        input_mode: 'mouse',
        play_mode: 'normal',
        interaction_model: 'crosshair',
        interact_rotation: { x: 0, z: 0 },
        tick,
        delta: { x: 0, y: 0, z: 0 },
        transaction: null,
        item_stack_request: null,
        block_action: null,
        vehicle_rotation: null,
        predicted_vehicle: null,
        analogue_move_vector: { x: 0, z: 0 },
        camera_orientation: { x: 0, y: 0, z: 0 },
        raw_move_vector: { x: 0, z: 0 }
      })
      tick += 1n
      if (step % 20 === 0) log('MOVE', `step=${step} x=${x.toFixed(2)}`)
    } catch (e) {
      log('MOVE_ERROR', String(e.message || e))
    }
  }, 100)
}

const timer = setTimeout(() => {
  if (!stages.startGame) finish(3, 'timeout waiting for start_game')
  else if (stages.chunks < args.requireChunks) finish(4, `timeout waiting for level_chunk (${stages.chunks}/${args.requireChunks})`)
  else finish(0, 'all stages satisfied')
}, args.timeout)

log('START', `${args.host}:${args.port} version=${args.version} transport=${args.transport} offline=${args.offline}`)

const options = {
  host: args.host,
  port: args.port,
  version: args.version,
  username: args.username,
  offline: args.offline,
  transport: args.transport,
  connectTimeout: Math.min(args.timeout, 15000)
}
if (args.transport === 'nethernet') {
  options.nethernet = { signalling: 'lan' }
  if (args.networkId != null) options.nethernet.networkId = args.networkId
}

try {
  client = createClient(options)
} catch (err) {
  stages.error = String(err && err.stack || err)
  finish(1, 'createClient failed')
}

if (args.spoofGameVersion) {
  client.options.version = args.spoofGameVersion
  console.log('SPOOF GameVersion=' + args.spoofGameVersion + ' (protocol stays ' + client.options.protocolVersion + ')')
}

if (args.dumpLogin) {
  const origWrite = client.write.bind(client)
  client.write = (name, params, ...rest) => {
    if (name === 'login' && params) {
      const tokens = params.tokens || {}
      console.log('DUMP login protocol=' + params.protocol_version)
      console.log('DUMP identity(type=' + typeof tokens.identity + ',len=' + String(tokens.identity || '').length + ')=' + String(tokens.identity || '').slice(0, 600))
      console.log('DUMP client(len=' + String(tokens.client || '').length + ')=' + String(tokens.client || '').slice(0, 600))
    }
    return origWrite(name, params, ...rest)
  }
}

client.on('connect_allowed', () => {
  stages.connected = true
  log('TCP/UDP', 'connect_allowed')
})

if (args.packetLog) {
  const fs = require('fs')
  fs.writeFileSync(args.packetLog, '')
  client.on('packet', (p) => {
    try {
      const name = p?.data?.name || 'unknown'
      const params = p?.data?.params
      let approx = 0
      try { approx = params ? JSON.stringify(params).length : 0 } catch {}
      fs.appendFileSync(args.packetLog, `${new Date().toISOString()} ${name} approx_json=${approx}\n`)
    } catch {}
  })
}

client.on('loggingIn', () => {
  stages.loginSent = true
  log('LOGIN', `sent login protocol=${args.version}`)
})

client.on('network_settings', (packet) => {
  stages.compression = packet.compression_algorithm ?? packet.compression_threshold
  log('NETWORK_SETTINGS', JSON.stringify(packet))
})

client.on('join', () => {
  stages.joined = true
  log('HANDSHAKE', 'join (encryption or skip)')
})

client.on('start_game', (packet) => {
  if (stages.startGame) return
  stages.startGame = true
  stages.startGameAt = Date.now()
  stages.levelName = packet.level_name ?? null
  stages.blockProperties = Array.isArray(packet.block_properties) ? packet.block_properties.length : null
  log('START_GAME', `level=${stages.levelName} blockProperties=${stages.blockProperties} runtimeEntityId=${packet.runtime_entity_id}`)
  if (args.requireChunks === 0 && args.dwellMs <= 0) finish(0, 'start_game received (minimal self-check)')
})

client.on('level_chunk', () => {
  stages.chunks++
  if (stages.chunks === 1) {
    stages.firstChunkAt = Date.now()
    log('CHUNK', 'first level_chunk received')
    if (args.dwellMs > 0) scheduleDwell()
  } else if (stages.chunks % 16 === 0) {
    log('CHUNK', `${stages.chunks} chunks`)
  }
  if (stages.startGame && stages.chunks >= args.requireChunks && args.dwellMs <= 0) {
    finish(0, 'start_game + chunks received')
  }
})

client.on('spawn', () => {
  stages.spawn = true
  log('SPAWN', 'player spawned')
  if (args.dwellMs > 0) scheduleDwell()
})

client.on('kick', (packet) => {
  stages.kick = packet
  log('KICK', packet?.message ?? JSON.stringify(packet))
  if (!stages.startGame) finish(2, `login rejected: ${packet?.message}`)
})

client.on('disconnect', (packet) => {
  stages.kick = packet
  log('DISCONNECT', packet?.message ?? JSON.stringify(packet))
  if (!stages.startGame) finish(2, `server disconnect: ${packet?.message}`)
})

client.on('error', (err) => {
  stages.error = String(err && err.stack || err)
  log('ERROR', stages.error.split('\n')[0])
  if (!stages.startGame) finish(1, 'client error before start_game')
})

client.on('close', () => {
  log('CLOSE', 'connection closed')
  if (!finished) {
    if (!stages.startGame) finish(2, 'closed before start_game')
    else if (stages.chunks < args.requireChunks) finish(4, 'closed before chunks')
    else finish(0, 'closed after pass criteria')
  }
})

process.on('SIGINT', () => finish(1, 'interrupted'))
