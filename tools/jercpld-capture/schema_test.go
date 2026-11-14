package main

import "testing"

// TestZigzag 校验 zigzag 解码:0→0, 1→-1, 2→1, 3→-2。
func TestZigzag(t *testing.T) {
	cases := []struct {
		raw  []byte
		want int32
	}{
		{[]byte{0x00}, 0},
		{[]byte{0x01}, -1},
		{[]byte{0x02}, 1},
		{[]byte{0x03}, -2},
	}
	for _, tc := range cases {
		c := NewCursor(tc.raw)
		got := c.Varint()
		if got != tc.want {
			t.Fatalf("zigzag(%x) = %d, want %d", tc.raw, got, tc.want)
		}
		if err := c.Err(); err != nil {
			t.Fatalf("zigzag(%x) err = %v", tc.raw, err)
		}
	}
}

// TestAirItem1001 校验 1001 的 air 只占 1 字节(network_id 后立即短路)。
func TestAirItem1001(t *testing.T) {
	c := NewCursor([]byte{0x00})
	n, air, err := parseItem(c, spec1001)
	if err != nil {
		t.Fatalf("parseItem: %v", err)
	}
	if !air {
		t.Fatal("air = false, want true")
	}
	if n != 1 {
		t.Fatalf("n = %d, want 1", n)
	}
}

// TestAirItem2168 校验 2168 的 air 写满 8 字节。
//
// li16 id(2) + lu16 count(2) + metadata varint(1) + hasStack(1)
// + blockRuntime varint(1) + extraLen varint(1) = 8
func TestAirItem2168(t *testing.T) {
	raw := []byte{0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00}
	c := NewCursor(raw)
	n, air, err := parseItem(c, spec2168)
	if err != nil {
		t.Fatalf("parseItem: %v", err)
	}
	if !air {
		t.Fatal("air = false, want true")
	}
	if n != 8 {
		t.Fatalf("n = %d, want 8", n)
	}
	if c.Remaining() != 0 {
		t.Fatalf("remaining = %d, want 0", c.Remaining())
	}
}

// TestMobEquipmentAir2168 校验 mob_equipment 在 2168 下恰好 12 字节。
//
// runtime_id varlong(1) + item(8) + slot(1) + selected_slot(1) + window_id(1)
func TestMobEquipmentAir2168(t *testing.T) {
	raw := make([]byte, 12)
	c := NewCursor(raw)
	il, air, err := parseMobEquipment(c, spec2168)
	if err != nil {
		t.Fatalf("parseMobEquipment: %v", err)
	}
	if !air {
		t.Fatal("air = false, want true")
	}
	if il != 8 {
		t.Fatalf("itemLen = %d, want 8", il)
	}
	if c.Remaining() != 0 {
		t.Fatalf("remaining = %d, want 0", c.Remaining())
	}
}

// TestMobEquipmentAir1001 校验同一段 12 字节在 1001 下会剩 7 字节,
// 这正是 verdictAir 的判据来源。
func TestMobEquipmentAir1001(t *testing.T) {
	raw := make([]byte, 12)
	c := NewCursor(raw)
	il, air, err := parseMobEquipment(c, spec1001)
	if err != nil {
		t.Fatalf("parseMobEquipment: %v", err)
	}
	if !air {
		t.Fatal("air = false, want true")
	}
	if il != 1 {
		t.Fatalf("itemLen = %d, want 1", il)
	}
	if c.Remaining() != 7 {
		t.Fatalf("remaining = %d, want 7", c.Remaining())
	}
}

// TestMetadataCount 校验 metadata 计数解析。
//
// count=1, key=0, type=0(byte), value=0 → 4 字节
func TestMetadataCount(t *testing.T) {
	raw := []byte{0x01, 0x00, 0x00, 0x00}
	c := NewCursor(raw)
	count, err := parseMetadata(c, meta1001)
	if err != nil {
		t.Fatalf("parseMetadata: %v", err)
	}
	if count != 1 {
		t.Fatalf("count = %d, want 1", count)
	}
	if c.Remaining() != 0 {
		t.Fatalf("remaining = %d, want 0", c.Remaining())
	}
}

// TestMetadataLegacyType 校验 meta2168 每条目多 1 字节。
//
// count=1, key=0, type=0(byte), legacy_type=0, value=0 → 5 字节
func TestMetadataLegacyType(t *testing.T) {
	raw := []byte{0x01, 0x00, 0x00, 0x00, 0x00}
	c := NewCursor(raw)
	count, err := parseMetadata(c, meta2168)
	if err != nil {
		t.Fatalf("parseMetadata: %v", err)
	}
	if count != 1 {
		t.Fatalf("count = %d, want 1", count)
	}
	if c.Remaining() != 0 {
		t.Fatalf("remaining = %d, want 0", c.Remaining())
	}
}
