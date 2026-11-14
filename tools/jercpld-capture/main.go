package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

const version = "0.1.0"

func usage() {
	fmt.Fprintf(os.Stderr, `jercpld-capture %s — 读取 bedrocktool 的 .pcap2,用 1001 / 2168 两套 schema 交叉解析

用法:
  jercpld-capture analyze [flags] <file.pcap2 | dir>...
  jercpld-capture verdict [flags] <file.pcap2 | dir>...
  jercpld-capture hex     [flags] <file.pcap2>...

命令:
  analyze   列出包分布,并对关注的包做双 spec 解析
  verdict   只输出 docs/PROTOCOL_1001.md 第八节三个待确认项的判定
  hex       打印指定包的原始十六进制

analyze flags:
  -id string     只分析这些包 ID,逗号分隔(默认 "12,13,15,19,31,32")
  -dir string    方向过滤:c2s / s2c / both(默认 "both")
  -limit int     每个包最多展示多少条样本(默认 8)
  -json          输出 JSON

verdict flags:
  -json          输出 JSON
  -out string    把 JSON 写到文件

hex flags:
  -id uint       只打印该包 ID
  -index int     只打印第 N 条(按文件内出现顺序,从 0 开始;负数表示不限)
  -max int       每个包最多打印多少字节(默认 256)

示例:
  jercpld-capture verdict captures/1.26.33-2026-11-14_10-00-00.pcap2
  jercpld-capture analyze captures/ -id 31 -dir s2c
  jercpld-capture hex captures/x.pcap2 -id 12 -index 0
`, version)
}

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	cmd := os.Args[1]
	args := os.Args[2:]

	switch cmd {
	case "analyze":
		os.Exit(cmdAnalyze(args))
	case "verdict":
		os.Exit(cmdVerdict(args))
	case "hex":
		os.Exit(cmdHex(args))
	case "help", "-h", "--help":
		usage()
		os.Exit(0)
	default:
		fmt.Fprintf(os.Stderr, "未知命令 %q\n\n", cmd)
		usage()
		os.Exit(2)
	}
}

// ---------------------------------------------------------------------------
// analyze
// ---------------------------------------------------------------------------

func cmdAnalyze(args []string) int {
	fs := flag.NewFlagSet("analyze", flag.ContinueOnError)
	idList := fs.String("id", "12,13,15,19,31,32", "packet ids")
	dirFilter := fs.String("dir", "both", "c2s|s2c|both")
	limit := fs.Int("limit", 8, "max samples per packet")
	asJSON := fs.Bool("json", false, "json output")

	if err := fs.Parse(args); err != nil {
		return 2
	}
	if fs.NArg() == 0 {
		fmt.Fprintln(os.Stderr, "analyze: 需要至少一个 .pcap2 文件或目录")
		return 2
	}

	want, err := parseIDList(*idList)
	if err != nil {
		fmt.Fprintf(os.Stderr, "analyze: %v\n", err)
		return 2
	}

	files, err := collectFiles(fs.Args())
	if err != nil {
		fmt.Fprintf(os.Stderr, "analyze: %v\n", err)
		return 1
	}
	if len(files) == 0 {
		fmt.Fprintln(os.Stderr, "analyze: 没有找到 .pcap2 文件")
		return 1
	}

	recs, warns := loadAll(files)
	for _, w := range warns {
		fmt.Fprintf(os.Stderr, "警告: %v\n", w)
	}

	filtered := filterRecords(recs, want, *dirFilter)

	if *asJSON {
		return emitJSON(buildAnalyzeReport(files, recs, filtered, *limit))
	}

	fmt.Printf("== 输入 ==\n")
	for _, f := range files {
		fmt.Printf("  %s\n", f)
	}
	fmt.Printf("  共 %d 帧(过滤后 %d 帧)\n\n", len(recs), len(filtered))

	if len(filtered) == 0 {
		fmt.Println("没有匹配的包。检查 -id / -dir,或确认抓包时确实产生了这些包。")
		return 0
	}

	fmt.Printf("== 包分布 ==\n")
	printDistribution(filtered)

	fmt.Printf("\n== 样本 ==\n")
	printSamples(filtered, *limit)

	return 0
}

type idCount struct {
	ID    uint32 `json:"id"`
	Name  string `json:"name"`
	C2S   int    `json:"c2s"`
	S2C   int    `json:"s2c"`
	Total int    `json:"total"`
}

func printDistribution(recs []Record) {
	byID := map[uint32]*idCount{}
	for _, r := range recs {
		ic, ok := byID[r.ID]
		if !ok {
			ic = &idCount{ID: r.ID, Name: pktName(r.ID)}
			byID[r.ID] = ic
		}
		if r.ToServer {
			ic.C2S++
		} else {
			ic.S2C++
		}
		ic.Total++
	}

	ids := make([]int, 0, len(byID))
	for id := range byID {
		ids = append(ids, int(id))
	}
	sort.Ints(ids)

	fmt.Printf("  %-4s %-22s %6s %6s\n", "id", "名称", "c2s", "s2c")
	for _, id := range ids {
		ic := byID[uint32(id)]
		name := ic.Name
		if name == "" {
			name = "(unknown)"
		}
		fmt.Printf("  %-4d %-22s %6d %6d\n", ic.ID, name, ic.C2S, ic.S2C)
	}
}

func printSamples(recs []Record, limit int) {
	shown := map[uint32]int{}
	for _, r := range recs {
		if shown[r.ID] >= limit {
			continue
		}
		shown[r.ID]++

		fmt.Printf("\n  [%s] #%d %s id=%d len=%d\n", r.Dir(), r.Index, pktName(r.ID), r.ID, len(r.Payload))

		switch r.ID {
		case PktMobEquipment, PktMobArmorEquipment:
			printEquipSample(r)
		case PktMovePlayer:
			printMoveSample(r)
		case PktAddItemEntity:
			printAddItemSample(r)
		default:
			fmt.Printf("    (无专用解析器)\n")
		}
	}
}

func printEquipSample(r Record) {
	for _, spec := range []ItemSpec{spec1001, spec2168} {
		c := NewCursor(r.Payload)
		il, ac, err := parseEquipByID(r.ID, c, spec)
		tail := c.Remaining()
		status := "OK"
		if err != nil {
			status = "ERR " + err.Error()
		}
		fmt.Printf("    %-5s item=%dB air=%d tail=%d %s\n", spec.Label+":", il, ac, tail, status)
	}
}

func printMoveSample(r Record) {
	for _, tc := range []struct {
		label    string
		optional bool
	}{
		{"switch", false},
		{"option", true},
	} {
		c := NewCursor(r.Payload)
		mode, _, err := parseMovePlayer(c, tc.optional)
		tail := c.Remaining()
		status := "OK"
		if err != nil {
			status = "ERR " + err.Error()
		}
		fmt.Printf("    %-7s mode=%d tail=%d %s\n", tc.label+":", mode, tail, status)
	}
}

func printAddItemSample(r Record) {
	for _, pair := range []struct {
		is ItemSpec
		ms MetaSpec
	}{
		{spec1001, meta1001},
		{spec2168, meta2168},
	} {
		c := NewCursor(r.Payload)
		il, air, err := parseAddItemEntity(c, pair.is, pair.ms)
		tail := c.Remaining()
		status := "OK"
		if err != nil {
			status = "ERR " + err.Error()
		}
		fmt.Printf("    %-5s item=%dB air=%v tail=%d %s\n", pair.is.Label+":", il, air, tail, status)
	}
}

type analyzeReport struct {
	Inputs  []string  `json:"inputs"`
	Frames  int       `json:"frames"`
	Filtered int      `json:"filtered"`
	Dist    []idCount `json:"distribution"`
}

func buildAnalyzeReport(files []string, all, filtered []Record, _ int) analyzeReport {
	byID := map[uint32]*idCount{}
	for _, r := range filtered {
		ic, ok := byID[r.ID]
		if !ok {
			ic = &idCount{ID: r.ID, Name: pktName(r.ID)}
			byID[r.ID] = ic
		}
		if r.ToServer {
			ic.C2S++
		} else {
			ic.S2C++
		}
		ic.Total++
	}
	ids := make([]int, 0, len(byID))
	for id := range byID {
		ids = append(ids, int(id))
	}
	sort.Ints(ids)

	dist := make([]idCount, 0, len(ids))
	for _, id := range ids {
		dist = append(dist, *byID[uint32(id)])
	}

	return analyzeReport{
		Inputs:   files,
		Frames:   len(all),
		Filtered: len(filtered),
		Dist:     dist,
	}
}

// ---------------------------------------------------------------------------
// verdict
// ---------------------------------------------------------------------------

func cmdVerdict(args []string) int {
	fs := flag.NewFlagSet("verdict", flag.ContinueOnError)
	asJSON := fs.Bool("json", false, "json output")
	outPath := fs.String("out", "", "write json to file")

	if err := fs.Parse(args); err != nil {
		return 2
	}
	if fs.NArg() == 0 {
		fmt.Fprintln(os.Stderr, "verdict: 需要至少一个 .pcap2 文件或目录")
		return 2
	}

	files, err := collectFiles(fs.Args())
	if err != nil {
		fmt.Fprintf(os.Stderr, "verdict: %v\n", err)
		return 1
	}
	if len(files) == 0 {
		fmt.Fprintln(os.Stderr, "verdict: 没有找到 .pcap2 文件")
		return 1
	}

	recs, warns := loadAll(files)
	for _, w := range warns {
		fmt.Fprintf(os.Stderr, "警告: %v\n", w)
	}

	verdicts := runVerdicts(recs)

	if *asJSON || *outPath != "" {
		data, err := json.MarshalIndent(verdicts, "", "  ")
		if err != nil {
			fmt.Fprintf(os.Stderr, "verdict: 序列化失败: %v\n", err)
			return 1
		}
		if *outPath != "" {
			if err := os.WriteFile(*outPath, data, 0o644); err != nil {
				fmt.Fprintf(os.Stderr, "verdict: 写入 %s 失败: %v\n", *outPath, err)
				return 1
			}
			fmt.Printf("已写入 %s\n", *outPath)
		}
		if *asJSON {
			os.Stdout.Write(data)
			fmt.Println()
		}
		if *outPath != "" && !*asJSON {
			return 0
		}
	}

	fmt.Printf("== 输入 ==\n")
	for _, f := range files {
		fmt.Printf("  %s\n", f)
	}
	fmt.Printf("  共 %d 帧\n\n", len(recs))

	for i, v := range verdicts {
		fmt.Printf("== 判定 %d/%d:%s ==\n", i+1, len(verdicts), v.Key)
		fmt.Printf("问题:%s\n", v.Question)
		fmt.Printf("结论:%s  [置信度 %s,样本 %d]\n", v.Answer, v.Confidence, v.Samples)
		if len(v.Evidence) > 0 {
			fmt.Printf("证据:\n")
			for _, e := range v.Evidence {
				fmt.Printf("  - %s\n", e)
			}
		}
		fmt.Println()
	}

	return 0
}

// ---------------------------------------------------------------------------
// hex
// ---------------------------------------------------------------------------

func cmdHex(args []string) int {
	fs := flag.NewFlagSet("hex", flag.ContinueOnError)
	idFilter := fs.Uint("id", 0, "only this packet id (0 = all)")
	index := fs.Int("index", -1, "only the Nth record (-1 = all)")
	maxBytes := fs.Int("max", 256, "max bytes per packet")

	if err := fs.Parse(args); err != nil {
		return 2
	}
	if fs.NArg() == 0 {
		fmt.Fprintln(os.Stderr, "hex: 需要一个 .pcap2 文件")
		return 2
	}

	recs, warns := loadAll([]string{fs.Arg(0)})
	for _, w := range warns {
		fmt.Fprintf(os.Stderr, "警告: %v\n", w)
	}

	printed := 0
	for _, r := range recs {
		if *idFilter != 0 && r.ID != uint32(*idFilter) {
			continue
		}
		if *index >= 0 && r.Index != *index {
			continue
		}
		fmt.Printf("[%s] #%d %s id=%d len=%d ts=%d\n", r.Dir(), r.Index, pktName(r.ID), r.ID, len(r.Payload), r.Timestamp)
		fmt.Print(hexDump(r.Payload, *maxBytes))
		fmt.Println()
		printed++
	}

	if printed == 0 {
		fmt.Println("没有匹配的包。")
	}
	return 0
}

func hexDump(b []byte, max int) string {
	origLen := len(b)
	truncated := false
	if len(b) > max {
		b = b[:max]
		truncated = true
	}
	var sb strings.Builder
	for i := 0; i < len(b); i += 16 {
		end := i + 16
		if end > len(b) {
			end = len(b)
		}
		fmt.Fprintf(&sb, "  %04x  ", i)
		for j := i; j < end; j++ {
			fmt.Fprintf(&sb, "%02x ", b[j])
		}
		for j := end; j < i+16; j++ {
			sb.WriteString("   ")
		}
		sb.WriteString(" |")
		for j := i; j < end; j++ {
			ch := b[j]
			if ch < 0x20 || ch > 0x7e {
				ch = '.'
			}
			sb.WriteByte(ch)
		}
		sb.WriteString("|\n")
	}
	if truncated {
		fmt.Fprintf(&sb, "  ... 已截断(原长 %d 字节,用 -max 调整)\n", origLen)
	}
	return sb.String()
}

// ---------------------------------------------------------------------------
// 通用工具
// ---------------------------------------------------------------------------

func collectFiles(args []string) ([]string, error) {
	var out []string
	seen := map[string]bool{}

	add := func(p string) {
		abs, err := filepath.Abs(p)
		if err != nil {
			abs = p
		}
		if !seen[abs] {
			seen[abs] = true
			out = append(out, p)
		}
	}

	for _, a := range args {
		info, err := os.Stat(a)
		if err != nil {
			return nil, err
		}
		if !info.IsDir() {
			add(a)
			continue
		}
		err = filepath.WalkDir(a, func(p string, d os.DirEntry, err error) error {
			if err != nil {
				return err
			}
			if d.IsDir() {
				return nil
			}
			if strings.HasSuffix(strings.ToLower(d.Name()), ".pcap2") {
				add(p)
			}
			return nil
		})
		if err != nil {
			return nil, err
		}
	}

	sort.Strings(out)
	return out, nil
}

// loadAll 读取所有文件,把每个文件的错误收集起来但不中断。
// ReadPcap2 在版本不符时会同时返回记录和错误,这里保留记录。
func loadAll(files []string) ([]Record, []error) {
	var recs []Record
	var warns []error

	for _, f := range files {
		r, err := ReadPcap2(f)
		if err != nil {
			warns = append(warns, fmt.Errorf("%s: %w", f, err))
		}
		recs = append(recs, r...)
	}
	return recs, warns
}

func filterRecords(recs []Record, want map[uint32]bool, dir string) []Record {
	var out []Record
	for _, r := range recs {
		if len(want) > 0 && !want[r.ID] {
			continue
		}
		switch dir {
		case "c2s":
			if !r.ToServer {
				continue
			}
		case "s2c":
			if r.ToServer {
				continue
			}
		}
		out = append(out, r)
	}
	return out
}

func parseIDList(s string) (map[uint32]bool, error) {
	out := map[uint32]bool{}
	s = strings.TrimSpace(s)
	if s == "" {
		return out, nil
	}
	for _, part := range strings.Split(s, ",") {
		part = strings.TrimSpace(part)
		if part == "" {
			continue
		}
		n, err := strconv.ParseUint(part, 10, 32)
		if err != nil {
			return nil, fmt.Errorf("无效包 ID %q: %w", part, err)
		}
		out[uint32(n)] = true
	}
	return out, nil
}

func emitJSON(v any) int {
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		fmt.Fprintf(os.Stderr, "json: %v\n", err)
		return 1
	}
	os.Stdout.Write(data)
	fmt.Println()
	return 0
}
