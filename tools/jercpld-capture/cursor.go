package main

import (
	"encoding/binary"
	"errors"
	"fmt"
	"math"
)

var (
	errEOF     = errors.New("unexpected end of buffer")
	errVarint  = errors.New("varint overflow")
	errBadType = errors.New("invalid metadata type")
)

// Cursor 是 Bedrock 线格式的只读游标。
//
// 编码约定:
//   - varint / uvarint 为无符号 LEB128,zigzag 用于有符号整数
//   - li16 / lu16 / li32 / lu32 / f32 为小端定长
//   - string 为 varuint 长度前缀 + UTF-8
//
// 任何越界或非法编码都会把 err 置位,之后所有读取变为空操作,
// 调用方在合适的位置检查 Err() 即可。
type Cursor struct {
	b   []byte
	pos int
	err error
}

// NewCursor 在 b 上创建一个游标。
func NewCursor(b []byte) *Cursor { return &Cursor{b: b} }

// Err 返回首个错误(没有则 nil)。
func (c *Cursor) Err() error { return c.err }

// Pos 返回当前读取位置。
func (c *Cursor) Pos() int { return c.pos }

// Len 返回底层缓冲区总长度。
func (c *Cursor) Len() int { return len(c.b) }

// Remaining 返回剩余可读字节数。
func (c *Cursor) Remaining() int { return len(c.b) - c.pos }

func (c *Cursor) fail(err error) {
	if c.err == nil {
		c.err = err
	}
}

func (c *Cursor) need(n int) bool {
	if c.err != nil {
		return false
	}
	if n < 0 || c.pos+n > len(c.b) {
		c.fail(fmt.Errorf("%w: need %d at %d (len %d)", errEOF, n, c.pos, len(c.b)))
		return false
	}
	return true
}

// U8 读 1 字节。
func (c *Cursor) U8() uint8 {
	if !c.need(1) {
		return 0
	}
	v := c.b[c.pos]
	c.pos++
	return v
}

// Bool 读 1 字节并转 bool。
func (c *Cursor) Bool() bool { return c.U8() != 0 }

// Bytes 读 n 字节(切片指向底层缓冲区,不拷贝)。
func (c *Cursor) Bytes(n int) []byte {
	if !c.need(n) {
		return nil
	}
	v := c.b[c.pos : c.pos+n]
	c.pos += n
	return v
}

// Skip 跳过 n 字节。
func (c *Cursor) Skip(n int) { c.Bytes(n) }

// U16LE 读小端 uint16(lu16)。
func (c *Cursor) U16LE() uint16 {
	if !c.need(2) {
		return 0
	}
	v := binary.LittleEndian.Uint16(c.b[c.pos:])
	c.pos += 2
	return v
}

// I16LE 读小端 int16(li16)。
func (c *Cursor) I16LE() int16 {
	if !c.need(2) {
		return 0
	}
	v := int16(binary.LittleEndian.Uint16(c.b[c.pos:]))
	c.pos += 2
	return v
}

// U32LE 读小端 uint32。
func (c *Cursor) U32LE() uint32 {
	if !c.need(4) {
		return 0
	}
	v := binary.LittleEndian.Uint32(c.b[c.pos:])
	c.pos += 4
	return v
}

// I32LE 读小端 int32(li32)。
func (c *Cursor) I32LE() int32 {
	if !c.need(4) {
		return 0
	}
	v := int32(binary.LittleEndian.Uint32(c.b[c.pos:]))
	c.pos += 4
	return v
}

// F32 读小端 float32。
func (c *Cursor) F32() float32 {
	return math.Float32frombits(c.U32LE())
}

// Uvarint 读无符号 LEB128,最多 5 字节。
func (c *Cursor) Uvarint() uint32 {
	var x uint32
	var s uint
	for i := 0; i < 5; i++ {
		if !c.need(1) {
			return 0
		}
		b := c.b[c.pos]
		c.pos++
		if b < 0x80 {
			if i == 4 && b > 0x0F {
				c.fail(errVarint)
				return 0
			}
			return x | uint32(b)<<s
		}
		x |= uint32(b&0x7F) << s
		s += 7
	}
	c.fail(errVarint)
	return 0
}

// Varint 读 zigzag 编码的有符号 varint。
func (c *Cursor) Varint() int32 {
	u := c.Uvarint()
	return int32(u>>1) ^ -int32(u&1)
}

// Uvarint64 读无符号 LEB128,最多 10 字节。
func (c *Cursor) Uvarint64() uint64 {
	var x uint64
	var s uint
	for i := 0; i < 10; i++ {
		if !c.need(1) {
			return 0
		}
		b := c.b[c.pos]
		c.pos++
		if b < 0x80 {
			if i == 9 && b > 1 {
				c.fail(errVarint)
				return 0
			}
			return x | uint64(b)<<s
		}
		x |= uint64(b&0x7F) << s
		s += 7
	}
	c.fail(errVarint)
	return 0
}

// Varint64 读 zigzag64。
func (c *Cursor) Varint64() int64 {
	u := c.Uvarint64()
	return int64(u>>1) ^ -int64(u&1)
}

// String 读 varuint 长度前缀 + UTF-8。
func (c *Cursor) String() string {
	n := c.Uvarint()
	if c.err != nil {
		return ""
	}
	return string(c.Bytes(int(n)))
}
