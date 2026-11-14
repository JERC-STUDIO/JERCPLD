package main

import (
	"bufio"
	"encoding/hex"
	"fmt"
	"os"
	"regexp"
	"strconv"
	"strings"
)

// JERCPLD 的包级抓包格式(见 docs/CAPTURE.md):
//
//	00:30:20.138 s2c id=58 name=FullChunkData len=9297
//	HEX 3ab101d007000001120000c348...
//
// 一个 header 行后面可以跟多行 HEX(长包会折行),HEX 拼接后是完整包字节,
// 即 packet.Header(varuint packetID) + payload。
//
// 与 .pcap2 的区别:
//   - .pcap2 是 bedrocktool 抓的,二进制 + s2 压缩,每包带方向和时间戳
//   - .log 是 JERCPLD 在 BDS 内部抓的,文本 HEX,方向在 header 行
//   - 两者的 payload 语义一致,剥掉 varint id 之后可以共用同一套 schema

var (
	logHeaderRe = regexp.MustCompile(`^(\d{2}):(\d{2}):(\d{2})\.(\d{3})\s+(c2s|s2c)\s+id=(\d+)\s+name=(\S+)\s+len=(\d+)\s*$`)
	logHexRe    = regexp.MustCompile(`^HEX\s+([0-9a-fA-F]+)\s*$`)
)

// ReadJERCPLDLog 解析一个 session-*.log,返回按出现顺序排列的记录。
//
// 容错:
//   - header 行格式不符的行直接跳过(日志里可能混有 [CONN] / [PERF] 等行)
//   - HEX 行可以多行,遇到下一个 header 或文件结束就 flush
//   - len= 与实际 HEX 长度不符时以实际长度为准(抓包可能被截断)
func ReadJERCPLDLog(path string) ([]Record, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("log: open %q: %w", path, err)
	}
	defer f.Close()

	var out []Record
	var cur *Record
	var hexBuf strings.Builder
	lineNo := 0
	idx := 0

	flush := func() error {
		if cur == nil {
			return nil
		}
		s := strings.TrimSpace(hexBuf.String())
		if s != "" {
			raw, err := hex.DecodeString(s)
			if err != nil {
				return fmt.Errorf("log: %s:%d bad hex: %w", path, lineNo, err)
			}
			c := NewCursor(raw)
			id := c.Uvarint()
			if c.Err() != nil {
				return fmt.Errorf("log: %s:%d bad packet id: %w", path, lineNo, c.Err())
			}
			cur.ID = id
			if n := c.Remaining(); n > 0 {
				cur.Payload = append([]byte(nil), c.Bytes(n)...)
			}
		}
		out = append(out, *cur)
		cur = nil
		hexBuf.Reset()
		return nil
	}

	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 1<<20), 1<<24)

	for sc.Scan() {
		lineNo++
		line := strings.TrimSpace(sc.Text())
		if line == "" {
			continue
		}

		if m := logHeaderRe.FindStringSubmatch(line); m != nil {
			if err := flush(); err != nil {
				return nil, err
			}
			h, _ := strconv.Atoi(m[1])
			mi, _ := strconv.Atoi(m[2])
			sec, _ := strconv.Atoi(m[3])
			ms, _ := strconv.Atoi(m[4])
			dir := m[5]
			id, _ := strconv.ParseUint(m[6], 10, 32)
			length, _ := strconv.Atoi(m[8])

			cur = &Record{
				Index:     idx,
				ToServer:  dir == "c2s",
				Timestamp: int64(h)*3600000 + int64(mi)*60000 + int64(sec)*1000 + int64(ms),
				ID:        uint32(id),
			}
			_ = length // 只用于日志对照,实际以 HEX 长度为准
			idx++
			continue
		}

		if m := logHexRe.FindStringSubmatch(line); m != nil {
			hexBuf.WriteString(m[1])
			continue
		}

		// 其他行([CONN] / [PERF] / 空行)忽略
	}

	if err := sc.Err(); err != nil {
		return nil, fmt.Errorf("log: %s scan: %w", path, err)
	}
	if err := flush(); err != nil {
		return nil, err
	}

	return out, nil
}
