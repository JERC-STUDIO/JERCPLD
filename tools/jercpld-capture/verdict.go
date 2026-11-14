package main

import "fmt"

// Verdict 是单个待确认项的判定结论。
type Verdict struct {
	Key        string   `json:"key"`
	Question   string   `json:"question"`
	Answer     string   `json:"answer"`
	Confidence string   `json:"confidence"` // high / medium / low / unknown
	Samples    int      `json:"samples"`
	Evidence   []string `json:"evidence,omitempty"`
}

const maxEvidence = 8

// runVerdicts 跑三个判定,顺序与 docs/PROTOCOL_1001.md 第八节一致。
func runVerdicts(recs []Record) []Verdict {
	return []Verdict{
		verdictAir(recs),
		verdictLegacyType(recs),
		verdictTeleport(recs),
	}
}

// ---------------------------------------------------------------------------
// 判定 1:air 物品长度
// ---------------------------------------------------------------------------

// verdictAir 判定 2168 ItemV4 的 air 是否写满 8 字节。
//
// 判据:同一个包用两套 spec 解析,谁能让 tail 恰好为 0 谁就是对的。
// 1001 短路时 item 只占 1 字节,2168 不短路时占 8 字节,
// 两者对同一段字节的消耗差 7 字节,不可能同时对齐包尾。
func verdictAir(recs []Record) Verdict {
	v := Verdict{
		Key:      "air-length",
		Question: "2168 ItemV4 的 air(network_id==0) 是 1 字节还是 8 字节?",
	}

	type sample struct {
		rec          Record
		l1001, t1001 int
		e1001        string
		l2168, t2168 int
		e2168        string
	}

	var samples []sample
	ok1001, ok2168 := 0, 0

	for _, r := range recs {
		if r.ID != PktMobEquipment && r.ID != PktMobArmorEquipment {
			continue
		}

		var s sample
		s.rec = r

		c := NewCursor(r.Payload)
		il, _, err := parseEquipByID(r.ID, c, spec1001)
		s.l1001, s.t1001 = il, c.Remaining()
		if err != nil {
			s.e1001 = err.Error()
		} else if s.t1001 == 0 {
			ok1001++
		}

		c = NewCursor(r.Payload)
		il, _, err = parseEquipByID(r.ID, c, spec2168)
		s.l2168, s.t2168 = il, c.Remaining()
		if err != nil {
			s.e2168 = err.Error()
		} else if s.t2168 == 0 {
			ok2168++
		}

		samples = append(samples, s)
	}

	v.Samples = len(samples)

	if len(samples) == 0 {
		v.Answer = "无样本"
		v.Confidence = "unknown"
		v.Evidence = append(v.Evidence,
			"没有抓到 mob_equipment(31) 或 mob_armor_equipment(32)。",
			"抓包时至少要让一个玩家切换一次快捷栏物品,并让另一个玩家旁观。")
		return v
	}

	switch {
	case ok2168 > ok1001:
		v.Answer = "8 字节(air 也写满尾部字段)"
		v.Confidence = "high"
	case ok1001 > ok2168:
		v.Answer = "1 字节(network_id 后立即结束)"
		v.Confidence = "high"
	case ok1001 == 0 && ok2168 == 0:
		v.Answer = "无法判定:两种 spec 都没能对齐包尾"
		v.Confidence = "low"
	default:
		v.Answer = "样本互相矛盾"
		v.Confidence = "low"
	}

	v.Evidence = append(v.Evidence,
		fmt.Sprintf("对齐包尾的样本数:1001 spec=%d,2168 spec=%d,总计=%d", ok1001, ok2168, len(samples)))

	for i, s := range samples {
		if i >= maxEvidence {
			v.Evidence = append(v.Evidence, fmt.Sprintf("... 其余 %d 条省略", len(samples)-maxEvidence))
			break
		}
		line := fmt.Sprintf("[%s] #%d %s id=%d len=%d | 1001: item=%dB tail=%d %s | 2168: item=%dB tail=%d %s",
			s.rec.Dir(), s.rec.Index, pktName(s.rec.ID), s.rec.ID, len(s.rec.Payload),
			s.l1001, s.t1001, errTag(s.e1001),
			s.l2168, s.t2168, errTag(s.e2168))
		v.Evidence = append(v.Evidence, line)
	}

	return v
}

func errTag(e string) string {
	if e == "" {
		return "OK"
	}
	return "ERR(" + e + ")"
}

// ---------------------------------------------------------------------------
// 判定 2:metadata 的 legacy_type
// ---------------------------------------------------------------------------

// verdictLegacyType 判定 2168 metadata 每个条目是否多 1 字节 legacy_type。
//
// 判据:两套 spec 在同一个 offset 命中、条目数相同,则
//   end2168 - end1001 == count  → 每条目多 1 字节,legacy_type 存在
//   end2168 - end1001 == 0      → 不存在
//
// 随机字节很难同时在两种模式下落点相同,所以"同 offset 同 count"本身
// 就是很强的信号。
func verdictLegacyType(recs []Record) Verdict {
	v := Verdict{
		Key:      "legacy-type",
		Question: "2168 metadata 每条目是否多 1 字节 legacy_type?",
	}

	present, absent, ambiguous := 0, 0, 0
	scanned := 0

	for _, r := range recs {
		if r.ID != PktAddEntity && r.ID != PktAddPlayer {
			continue
		}
		scanned++

		h1 := scanMetadata(r.Payload, meta1001)
		h2 := scanMetadata(r.Payload, meta2168)

		for _, a := range h1 {
			for _, b := range h2 {
				if a.Offset != b.Offset || a.Count != b.Count {
					continue
				}
				delta := b.End - a.End
				switch {
				case delta == a.Count && a.Count > 0:
					present++
					if len(v.Evidence) < maxEvidence {
						v.Evidence = append(v.Evidence, fmt.Sprintf(
							"[%s] #%d id=%d len=%d offset=%d count=%d: 1001 end=%d, 2168 end=%d, 差=%d == count",
							r.Dir(), r.Index, r.ID, len(r.Payload), a.Offset, a.Count, a.End, b.End, delta))
					}
				case delta == 0:
					absent++
					if len(v.Evidence) < maxEvidence {
						v.Evidence = append(v.Evidence, fmt.Sprintf(
							"[%s] #%d id=%d len=%d offset=%d count=%d: 两种 spec end 相同(%d)",
							r.Dir(), r.Index, r.ID, len(r.Payload), a.Offset, a.Count, a.End))
					}
				default:
					ambiguous++
					if len(v.Evidence) < maxEvidence {
						v.Evidence = append(v.Evidence, fmt.Sprintf(
							"[%s] #%d id=%d len=%d offset=%d count=%d: 差=%d,既不是 0 也不是 count",
							r.Dir(), r.Index, r.ID, len(r.Payload), a.Offset, a.Count, delta))
					}
				}
			}
		}
	}

	v.Samples = scanned

	switch {
	case scanned == 0:
		v.Answer = "无样本"
		v.Confidence = "unknown"
		v.Evidence = append(v.Evidence,
			"没有抓到 add_entity(13) 或 add_player(12)。",
			"抓包时让第二个玩家进服,或让服务器刷一只生物。")
	case present > absent && present > 0:
		v.Answer = "存在(每条目多 1 字节)"
		v.Confidence = "high"
	case absent > present:
		v.Answer = "不存在(是 minecraft-data 的建模差异)"
		v.Confidence = "high"
	case present == 0 && absent == 0:
		v.Answer = "无法判定:没有找到两套 spec 同时命中的位置"
		v.Confidence = "low"
		v.Evidence = append(v.Evidence, "可尝试加 --dump 输出原始 hex,人工对照。")
	default:
		v.Answer = "证据冲突"
		v.Confidence = "low"
	}

	v.Evidence = append(v.Evidence,
		fmt.Sprintf("命中统计:存在=%d,不存在=%d,存疑=%d", present, absent, ambiguous))

	return v
}

// ---------------------------------------------------------------------------
// 判定 3:move_player 的 teleport 语义
// ---------------------------------------------------------------------------

// verdictTeleport 判定 2168 的 teleport 段前是否带 1 字节 bool 前缀。
//
// 判据:对 mode==teleport 的包,switch 语义与 option 语义消耗的字节数差 1,
// 谁能让 tail 恰好为 0 谁就是对的。
func verdictTeleport(recs []Record) Verdict {
	v := Verdict{
		Key:      "teleport-prefix",
		Question: "2168 move_player 的 teleport 是 switch 还是 option(带 1 字节前缀)?",
	}

	okSwitch, okOption := 0, 0
	teleportSamples, total := 0, 0

	for _, r := range recs {
		if r.ID != PktMovePlayer {
			continue
		}
		total++

		c1 := NewCursor(r.Payload)
		mode, _, err1 := parseMovePlayer(c1, false)
		t1 := c1.Remaining()

		c2 := NewCursor(r.Payload)
		_, _, err2 := parseMovePlayer(c2, true)
		t2 := c2.Remaining()

		if err1 != nil && err2 != nil {
			continue
		}
		if mode != moveModeTeleport {
			continue
		}
		teleportSamples++

		if err1 == nil && t1 == 0 {
			okSwitch++
		}
		if err2 == nil && t2 == 0 {
			okOption++
		}

		if len(v.Evidence) < maxEvidence {
			v.Evidence = append(v.Evidence, fmt.Sprintf(
				"[%s] #%d len=%d mode=teleport | switch: tail=%d %s | option: tail=%d %s",
				r.Dir(), r.Index, len(r.Payload), t1, errTag(errText(err1)), t2, errTag(errText(err2))))
		}
	}

	v.Samples = total

	switch {
	case total == 0:
		v.Answer = "无样本"
		v.Confidence = "unknown"
		v.Evidence = append(v.Evidence, "没有抓到 move_player(19)。")
	case teleportSamples == 0:
		v.Answer = "无 teleport 样本(只有普通移动)"
		v.Confidence = "unknown"
		v.Evidence = append(v.Evidence,
			fmt.Sprintf("共 %d 个 move_player,但没有 mode==teleport 的。", total),
			"抓包时用 /tp 传送一次玩家。")
	case okSwitch > okOption:
		v.Answer = "switch(无前缀,无需翻译)"
		v.Confidence = "high"
	case okOption > okSwitch:
		v.Answer = "option(有 1 字节 bool 前缀,需要补 00/01)"
		v.Confidence = "high"
	default:
		v.Answer = "无法判定:两种语义都未对齐包尾"
		v.Confidence = "low"
	}

	v.Evidence = append(v.Evidence,
		fmt.Sprintf("对齐统计:switch=%d,option=%d,teleport 样本=%d,总 move_player=%d",
			okSwitch, okOption, teleportSamples, total))

	return v
}

func errText(err error) string {
	if err == nil {
		return ""
	}
	return err.Error()
}
