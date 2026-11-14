package main

// Record 是 .pcap2 里的一条包记录。
//
// Payload 是 s2 解压后的包体(不含 packet id 的 varuint 前缀),
// 即 Bedrock 包的字段区。ID 是包 ID。
type Record struct {
	Index     int    // 文件内出现顺序,从 0 开始
	ToServer  bool   // true = c2s(客户端→服务端),false = s2c
	Timestamp int64  // UnixMilli
	ID        uint32 // packet id
	Payload   []byte // 包体(已解压,已剥离 id 前缀)
}
