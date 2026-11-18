import Foundation
import Testing

@testable import Convert

// MARK: - A small TrueType font, built by hand

/// One outline point, in font units.
private struct Pt: Equatable {
    var x: Int
    var y: Int
    var on = true
}

/// A glyph as the tests describe it. A nil box means "what the points say".
private enum TestGlyph {
    case empty
    /// Zero contours but a 10-byte header: bytes in the file, nothing to draw.
    case zeroContours
    case simple(contours: [[Pt]], code: [UInt8] = [], box: [Int]? = nil, overlap: Bool = false)
    case composite(components: [UInt8], code: [UInt8]?, box: [Int])
}

/// A glyph as read back from `glyf`: what has to survive every conversion.
private enum Parsed: Equatable {
    case empty
    case simple(ends: [Int], points: [Pt], code: [UInt8], box: [Int], overlap: Bool)
    case composite(components: [UInt8], code: [UInt8], box: [Int])
}

private func bounds(_ points: [Pt]) -> [Int] {
    [points.map(\.x).min() ?? 0, points.map(\.y).min() ?? 0, points.map(\.x).max() ?? 0, points.map(\.y).max() ?? 0]
}

private func expected(_ g: TestGlyph) -> Parsed {
    switch g {
    case .empty, .zeroContours: return .empty
    case .simple(let contours, let code, let box, let overlap):
        var ends: [Int] = []
        for c in contours { ends.append((ends.last ?? -1) + c.count) }
        let points = contours.flatMap { $0 }
        return .simple(ends: ends, points: points, code: code, box: box ?? bounds(points), overlap: overlap)
    case .composite(let components, let code, let box):
        return .composite(components: components, code: code ?? [], box: box)
    }
}

/// The left side bearing a glyph has (and the one WOFF2's hmtx transform rebuilds): its xMin.
private func leftEdge(_ g: TestGlyph) -> Int {
    switch expected(g) {
    case .empty: return 0
    case .simple(_, _, _, let box, _), .composite(_, _, let box): return box[0]
    }
}

/// A glyph the plain TrueType way: runs of identical flags use the repeat flag (at most 255 more).
private func encode(_ g: TestGlyph) -> [UInt8] {
    var out: [UInt8] = []
    switch g {
    case .empty: break
    case .zeroContours: out = [UInt8](repeating: 0, count: 10)
    case .composite(let components, let code, let box):
        out.put16(-1)
        for v in box { out.put16(v) }
        out += components
        if let code {
            out.put16(code.count)
            out += code
        }
    case .simple(let contours, let code, let box, let overlap):
        let points = contours.flatMap { $0 }
        out.put16(contours.count)
        for v in box ?? bounds(points) { out.put16(v) }
        var end = -1
        for c in contours {
            end += c.count
            out.put16(end)
        }
        out.put16(code.count)
        out += code
        var flags: [UInt8] = []
        var xs: [UInt8] = []
        var ys: [UInt8] = []
        var last = Pt(x: 0, y: 0)
        func delta(_ d: Int, short: UInt8, same: UInt8, _ bytes: inout [UInt8]) -> UInt8 {
            if d == 0 { return same }
            if abs(d) < 256 {
                bytes.append(UInt8(abs(d)))
                return short | (d > 0 ? same : 0)
            }
            bytes.put16(d)
            return 0
        }
        for (i, p) in points.enumerated() {
            var f: UInt8 = p.on ? 1 : 0
            if i == 0, overlap { f |= 0x40 }
            f |= delta(p.x - last.x, short: 0x02, same: 0x10, &xs)
            f |= delta(p.y - last.y, short: 0x04, same: 0x20, &ys)
            flags.append(f)
            last = p
        }
        var i = 0
        while i < flags.count {
            var n = 0
            while i + n + 1 < flags.count, flags[i + n + 1] == flags[i], n < 255 { n += 1 }
            out += n > 0 ? [flags[i] | 0x08, UInt8(n)] : [flags[i]]
            i += n + 1
        }
        out += xs + ys
    }
    return out
}

/// One composite component record: flags, glyph, arguments (words or bytes), then the transform words.
private func component(_ flags: Int, glyph: Int, args: [Int], transform: [Int] = []) -> [UInt8] {
    var out: [UInt8] = []
    out.put16(flags)
    out.put16(glyph)
    for a in args {
        if flags & 1 != 0 { out.put16(a) } else { out.append(UInt8(truncatingIfNeeded: a)) }
    }
    for t in transform { out.put16(t) }
    return out
}

/// Glyphs that between them hit the edge cases of the glyph transform.
private let glyphSet: [TestGlyph] = {
    // Every boundary of the triplet encoding, there and back, on and off the curve.
    var steps: [Pt] = [Pt(x: 0, y: 0)]
    for m in [1, 64, 65, 768, 769, 1279, 1280, 4095, 4096, 32767] {
        for (dx, dy) in [(0, m), (m, 0), (m, m), (m, 1), (1, m)] {
            for (sx, sy) in [(1, 1), (-1, 1), (1, -1), (-1, -1)] {
                steps.append(Pt(x: sx * dx, y: sy * dy, on: steps.count % 3 != 0))
                steps.append(Pt(x: 0, y: 0, on: steps.count % 2 == 0))
            }
        }
    }
    // -32768, the one step a 16-bit number can't take back.
    let extremes = [Pt(x: 16384, y: 16384), Pt(x: -16384, y: -16384, on: false), Pt(x: 16383, y: 16383)]
    func square(_ x: Int, _ y: Int, _ size: Int) -> [Pt] {
        [Pt(x: x, y: y), Pt(x: x + size, y: y), Pt(x: x + size, y: y + size, on: false), Pt(x: x, y: y + size)]
    }
    func line(_ n: Int) -> TestGlyph { .simple(contours: [(1...n).map { Pt(x: $0, y: 0) }]) }
    let scaled =
        component(0x0001 | 0x0002 | 0x0008 | 0x0020, glyph: 2, args: [-300, 1000], transform: [0x2000])
        + component(0x0002 | 0x0040, glyph: 3, args: [5, -7], transform: [0x4000, 0x1000])
    let matrix =
        component(0x0002 | 0x0080 | 0x0100 | 0x0020, glyph: 1, args: [1, 2], transform: [0x4000, 0, 0x0100, 0x4000])
        + component(0x0001 | 0x0200, glyph: 4, args: [0, 3])
    return [
        .empty,
        .simple(contours: [steps, extremes]),
        .simple(contours: [square(0, 0, 100)], code: [0xB0, 0x05, 0x2B, 0x00]),
        .simple(contours: [square(10, 20, 50)], box: [-5, 0, 300, 400]),
        .simple(contours: [square(0, 0, 200), square(50, 50, 100)], overlap: true),
        line(256),  // one flag and a repeat count of exactly 255
        line(257),
        line(600),
        .zeroContours,
        .composite(components: scaled, code: nil, box: [-300, 0, 1200, 1100]),
        .composite(components: matrix, code: [0x40, 0x01, 0x00, 0xB0], box: [0, 0, 100, 100]),
        .composite(components: component(0x0102, glyph: 2, args: [7, 7]), code: [], box: [7, 7, 107, 107]),
        .simple(contours: [[Pt(x: 3, y: 4)]]),
    ]
}()

private let noise: [UInt8] = {
    var rng = Seeded(7)
    return (0..<301).map { _ in UInt8(truncatingIfNeeded: rng.next()) }
}()

/// A fixed-seed generator, so every run tests the same bytes.
private struct Seeded {
    var state: UInt64
    init(_ seed: UInt64) { state = seed }
    mutating func next() -> Int {
        state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
        return Int(state >> 33)
    }
}

private struct NameRecord {
    var platform = 3
    var encoding = 1
    var language = 0x409
    var id = 6
    var text: String
}

private func nameTable(_ records: [NameRecord]) -> [UInt8] {
    var strings: [UInt8] = []
    var out: [UInt8] = []
    out.put16(0)
    out.put16(records.count)
    out.put16(6 + 12 * records.count)
    for r in records {
        let bytes = Array(r.text.data(using: r.platform == 1 ? .macOSRoman : .utf16BigEndian) ?? Data())
        for v in [r.platform, r.encoding, r.language, r.id, bytes.count, strings.count] { out.put16(v) }
        strings += bytes
    }
    return out + strings
}

private let customTag = FontTag(0x01FF_7F20)

/// A TrueType font holding `glyphs`, with metrics, names, a table of incompressible bytes under an
/// arbitrary tag, an empty table and a signature. Tables are stored in an order that isn't sorted.
private func makeFont(
    _ glyphs: [TestGlyph] = glyphSet, longLoca: Bool = false, pad: Int = 4, extraLoca: Int = 0,
    names: [NameRecord]? = [NameRecord(text: "Test-Regular")], flavor: UInt32 = SFNT.trueType
) -> SFNT {
    var glyf: [UInt8] = []
    var loca: [UInt8] = []
    for g in glyphs {
        if longLoca { loca.put32(glyf.count) } else { loca.put16(glyf.count / 2) }
        glyf += encode(g)
        while glyf.count % pad != 0 { glyf.append(0) }
    }
    if longLoca { loca.put32(glyf.count) } else { loca.put16(glyf.count / 2) }
    loca += [UInt8](repeating: 0, count: extraLoca)

    var head: [UInt8] = []
    for v in [0x0001_0000, 0x0002_8000, 0x1234_5678, 0x5F0F_3CF5] { head.put32(v) }
    head.put16(0x000B)
    head.put16(1000)
    head += [UInt8](repeating: 0, count: 16)
    for v in [-300, -200, 1300, 1200, 0, 8, 2, longLoca ? 1 : 0, 0] { head.put16(v) }
    let metrics = max(1, glyphs.count - 3)
    var hhea: [UInt8] = []
    hhea.put32(0x0001_0000)
    hhea += [UInt8](repeating: 0, count: 30)
    hhea.put16(metrics)
    var maxp: [UInt8] = []
    maxp.put32(0x0001_0000)
    maxp.put16(glyphs.count)
    maxp += [UInt8](repeating: 0, count: 26)
    var hmtx: [UInt8] = []
    for (i, g) in glyphs.enumerated() {
        if i < metrics { hmtx.put16(500 + i) }
        hmtx.put16(leftEdge(g))
    }
    var tables: [SFNT.Table] = [
        .init(tag: "head", data: head), .init(tag: "hhea", data: hhea), .init(tag: "maxp", data: maxp),
        .init(tag: "hmtx", data: hmtx),
    ]
    if let names { tables.append(.init(tag: "name", data: nameTable(names))) }
    tables += [
        .init(tag: customTag, data: noise), .init(tag: "Zero", data: []), .init(tag: "loca", data: loca),
        .init(tag: "glyf", data: glyf), .init(tag: "DSIG", data: [0, 0, 0, 1, 0, 0, 0, 0]),
    ]
    return SFNT(flavor: flavor, tables: tables)
}

/// A font with CFF outlines: no glyph tables, just bytes under `CFF ` this code never looks into.
private func makeCFFFont() -> SFNT {
    var font = makeFont([.empty], flavor: SFNT.openType)
    font.tables.removeAll { ["glyf", "loca", "DSIG"].contains($0.tag.description) }
    font.tables[2].data = [0, 0, 0x50, 0, 0, 1]  // maxp 0.5
    // Before the empty table, which would otherwise share its place in the file.
    let cff = SFNT.Table(tag: "CFF ", data: Array("not really CFF, but nobody reads it".utf8))
    font.tables.insert(cff, at: font.tables.firstIndex { $0.tag == "Zero" } ?? font.tables.count)
    return font
}

// MARK: - Reading back, independently of the code under test

private func sum32(_ data: [UInt8]) -> UInt32 {
    var sum: UInt32 = 0
    for i in stride(from: 0, to: data.count, by: 4) {
        var word: UInt32 = 0
        for k in 0..<4 { word = word << 8 | UInt32(i + k < data.count ? data[i + k] : 0) }
        sum &+= word
    }
    return sum
}

private func zeroedHead(_ t: SFNT.Table) -> [UInt8] {
    var d = t.data
    if t.tag == "head" { d.replaceSubrange(8..<12, with: [0, 0, 0, 0]) }
    return d
}

/// The .ttf file for `font`, written the textbook way: directory sorted by tag, data in the font's order.
private func sfntFile(_ font: SFNT) -> [UInt8] {
    let n = font.tables.count
    let range = 16 << (Int.bitWidth - 1 - n.leadingZeroBitCount)
    var out: [UInt8] = []
    out.put32(Int(font.flavor))
    for v in [n, range, range.trailingZeroBitCount - 4, n * 16 - range] { out.put16(v) }
    var offsets: [FontTag: Int] = [:]
    var offset = 12 + 16 * n
    for t in font.tables {
        offsets[t.tag] = offset
        offset += (t.data.count + 3) / 4 * 4
    }
    for t in font.tables.sorted(by: { $0.tag < $1.tag }) {
        for v in [Int(t.tag.value), Int(sum32(zeroedHead(t))), offsets[t.tag] ?? 0, t.data.count] { out.put32(v) }
    }
    for t in font.tables {
        out += zeroedHead(t)
        while out.count % 4 != 0 { out.append(0) }
    }
    if let head = offsets["head"] {
        out.replaceSubrange(head + 8..<head + 12, with: be32(0xB1B0_AFBA &- sum32(out)))
    }
    return out
}

private func be32(_ v: UInt32) -> [UInt8] { [24, 16, 8, 0].map { UInt8(truncatingIfNeeded: v >> $0) } }

/// A .ttc of `fonts`; identical tables are stored once and shared, as real collections do.
private func collectionFile(_ fonts: [SFNT]) -> [UInt8] {
    var out: [UInt8] = Array("ttcf".utf8)
    out.put32(0x0001_0000)
    out.put32(fonts.count)
    var directories = 12 + 4 * fonts.count
    for f in fonts {
        out.put32(directories)
        directories += 12 + 16 * f.tables.count
    }
    var data: [UInt8] = []
    var stored: [[UInt8]: Int] = [:]
    for f in fonts {
        out.put32(Int(f.flavor))
        for v in [f.tables.count, 0, 0, 0] { out.put16(v) }
        for t in f.tables.sorted(by: { $0.tag < $1.tag }) {
            let at = stored[t.data] ?? (directories + data.count)
            if stored[t.data] == nil {
                stored[t.data] = at
                data += t.data
                while data.count % 4 != 0 { data.append(0) }
            }
            for v in [Int(t.tag.value), 0, at, t.data.count] { out.put32(v) }
        }
    }
    return out + data
}

private func parseGlyphs(_ font: SFNT) throws -> [Parsed] {
    let head = try #require(font["head"])
    let maxp = try #require(font["maxp"])
    let glyf = try #require(font["glyf"])
    let long = head[51] == 1
    let count = Int(maxp[4]) << 8 | Int(maxp[5])
    var l = try ByteReader(try #require(font["loca"]))
    let offsets = try (0...count).map { _ in long ? try l.u32() : try l.u16() * 2 }
    return try (0..<count).map { g in
        if offsets[g] == offsets[g + 1] { return .empty }
        var r = try ByteReader(glyf, from: offsets[g], count: offsets[g + 1] - offsets[g])
        let contours = try r.i16()
        let box = try (0..<4).map { _ in try r.i16() }
        if contours == 0 { return .empty }
        if contours < 0 {
            let first = r.pos
            var instructions = false
            while true {
                let f = try r.u16()
                try r.skip(2 + (f & 1 != 0 ? 4 : 2) + (f & 8 != 0 ? 2 : f & 0x40 != 0 ? 4 : f & 0x80 != 0 ? 8 : 0))
                if f & 0x100 != 0 { instructions = true }
                if f & 0x20 == 0 { break }
            }
            let components = Array(glyf[first..<r.pos])
            return .composite(
                components: components, code: instructions ? Array(try r.take(try r.u16())) : [], box: box)
        }
        let ends = try (0..<contours).map { _ in try r.u16() }
        let code = Array(try r.take(try r.u16()))
        var flags: [Int] = []
        while flags.count < (ends.last ?? -1) + 1 {
            let f = try r.u8()
            flags.append(f)
            if f & 8 != 0 { flags += [Int](repeating: f, count: try r.u8()) }
        }
        func coordinates(short: Int, same: Int) throws -> [Int] {
            var v = 0
            return try flags.map { f in
                if f & short != 0 {
                    let b = try r.u8()
                    v += f & same != 0 ? b : -b
                } else if f & same == 0 {
                    v += try r.i16()
                }
                return v
            }
        }
        let xs = try coordinates(short: 2, same: 0x10)
        let ys = try coordinates(short: 4, same: 0x20)
        let points = flags.indices.map { Pt(x: xs[$0], y: ys[$0], on: flags[$0] & 1 != 0) }
        return .simple(ends: ends, points: points, code: code, box: box, overlap: flags[0] & 0x40 != 0)
    }
}

/// `loca` agrees with `glyf`: numGlyphs + 1 offsets in the head's format, never going back, ending at the end.
private func locaIsValid(_ font: SFNT) throws -> Bool {
    let long = try #require(font["head"])[51] == 1
    let loca = try #require(font["loca"])
    let count = Int(try #require(font["maxp"])[4]) << 8 | Int(try #require(font["maxp"])[5])
    guard loca.count == (count + 1) * (long ? 4 : 2) else { return false }
    var r = try ByteReader(loca)
    let offsets = try (0...count).map { _ in long ? try r.u32() : try r.u16() * 2 }
    return zip(offsets, offsets.dropFirst()).allSatisfy { $0 <= $1 } && offsets.last == font["glyf"]?.count
}

private func tags(_ font: SFNT) -> [FontTag] { font.tables.map(\.tag) }

private func woff2File(_ font: SFNT) throws -> [UInt8] {
    let p = WOFF2.prepare(font)
    return WOFF2.assemble(p, compressed: try Brotli.encode(p.stream))
}

/// `p` written out with one table's stored bytes and transform version replaced, to build files by hand.
private func woff2File(
    _ p: WOFF2.Prepared, replacing tag: FontTag, version: Int, with data: [UInt8]? = nil, length: Int? = nil
)
    throws -> [UInt8]
{
    var p = p
    var chunks: [[UInt8]] = []
    var offset = 0
    for e in p.entries {
        chunks.append(Array(p.stream[offset..<offset + (e.stored ?? e.length)]))
        offset += e.stored ?? e.length
    }
    let i = try #require(p.entries.firstIndex { $0.tag == tag })
    if let data { chunks[i] = data }
    let transformed = tag == "glyf" || tag == "loca" ? version != 3 : version != 0
    p.entries[i] = (tag, version, length ?? p.entries[i].length, transformed ? chunks[i].count : nil)
    p.stream = chunks.flatMap { $0 }
    return WOFF2.assemble(p, compressed: try Brotli.encode(p.stream))
}

// MARK: - Tests

final class FontTests {
    private let dir: URL

    init() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: false)
    }

    deinit {
        try? FileManager.default.removeItem(at: dir)
    }

    private var contents: [String] { (try? FileManager.default.contentsOfDirectory(atPath: dir.path).sorted()) ?? [] }

    private func file(_ name: String, _ bytes: [UInt8]) throws -> URL {
        let url = dir.appendingPathComponent(name)
        try Data(bytes).write(to: url)
        return url
    }

    private func convert(_ input: URL, to id: String, runner: Runner = Runner()) async throws -> [URL] {
        try await FontConv.convert(input, try #require(Targets.byID(id)), runner: runner)
    }

    private func read(_ url: URL) throws -> [SFNT] { try FontConv.read([UInt8](try Data(contentsOf: url))) }

    /// Same tables in the same order; for WOFF2 (`glyphsOnly`), the glyph tables only as glyphs,
    /// and the order is the format's own.
    private func expectSame(_ got: SFNT, _ want: SFNT, glyphsOnly: Bool = false) throws {
        #expect(got.flavor == want.flavor)
        if glyphsOnly { #expect(Set(tags(got)) == Set(tags(want))) } else { #expect(tags(got) == tags(want)) }
        for t in want.tables where !(glyphsOnly && ["glyf", "loca"].contains(t.tag.description)) {
            #expect(got[t.tag] == t.data, "\(t.tag)")
        }
        if want["glyf"] != nil {
            #expect(try parseGlyphs(got) == parseGlyphs(want))
            #expect(try locaIsValid(got))
        }
    }

    // MARK: The test font itself

    @Test func theTestFontReadsBackAsBuilt() throws {
        for long in [false, true] {
            let font = makeFont(longLoca: long)
            #expect(try parseGlyphs(font) == glyphSet.map(expected))
            #expect(try locaIsValid(font))
        }
    }

    // MARK: SFNT

    @Test func wellFormedFontsComeOutByteForByte() throws {
        for font in [makeFont(), makeFont(longLoca: true), makeCFFFont(), makeFont(flavor: SFNT.appleTrueType)] {
            let bytes = sfntFile(font)
            let read = try SFNT.fonts(in: bytes)
            #expect(read.count == 1)
            #expect(tags(read[0]) == tags(font))
            #expect(read[0].data() == bytes)
        }
    }

    @Test func checksumsAndTheWholeFileChecksumAreRight() throws {
        let out = makeFont().data()
        #expect(sum32(out) == 0xB1B0_AFBA)
        var r = try ByteReader(out, from: 4)
        let count = try r.u16()
        try r.skip(6)
        var last: UInt32 = 0
        for _ in 0..<count {
            let tag = UInt32(try r.u32())
            let checksum = UInt32(try r.u32())
            let offset = try r.u32()
            let length = try r.u32()
            #expect(tag > last)
            #expect(offset % 4 == 0)
            last = tag
            let table = SFNT.Table(tag: FontTag(tag), data: Array(out[offset..<offset + length]))
            #expect(checksum == sum32(zeroedHead(table)), "\(FontTag(tag))")
            #expect(SFNT.checksum(table.data) == sum32(table.data))
        }
        #expect(makeFont().fileExtension == "ttf")
        #expect(makeCFFFont().fileExtension == "otf")
    }

    @Test func collectionsReadAsSeparateFontsSharingTables() throws {
        let a = makeFont(names: [NameRecord(text: "Alpha")])
        let b = makeFont(names: [NameRecord(text: "Beta")])
        let fonts = try SFNT.fonts(in: collectionFile([a, b]))
        #expect(fonts.map(\.name) == ["Alpha", "Beta"])
        #expect(fonts[0]["glyf"] == a["glyf"])
        #expect(fonts[1]["glyf"] == b["glyf"])
        #expect(Set(tags(fonts[1])) == Set(tags(b)))
        for t in b.tables { #expect(fonts[1][t.tag] == t.data, "\(t.tag)") }
    }

    @Test func namesPreferWindowsEnglishThenMacRoman() {
        func name(_ records: [NameRecord]?) -> String? { makeFont([.empty], names: records).name }
        #expect(name(nil) == nil)
        #expect(name([NameRecord(id: 1, text: "Family only")]) == nil)
        #expect(name([NameRecord(platform: 1, encoding: 0, language: 0, text: "Café-Mac")]) == "Café-Mac")
        #expect(
            name([
                NameRecord(platform: 1, encoding: 0, language: 0, text: "Mac"),
                NameRecord(language: 0x40C, text: "Français"), NameRecord(text: "English"),
            ]) == "English")
        #expect(name([NameRecord(platform: 0, encoding: 3, language: 0, text: "Unicode")]) == "Unicode")
        // The full name stands in for a missing PostScript name.
        #expect(name([NameRecord(id: 4, text: "Test Regular")]) == "Test Regular")
        #expect(name([NameRecord(id: 4, text: "Full"), NameRecord(id: 6, text: "PS")]) == "PS")
    }

    // MARK: WOFF

    @Test func woffRoundTripsToTheSameFile() throws {
        for font in [makeFont(), makeCFFFont()] {
            let bytes = sfntFile(font)
            let original = try SFNT.fonts(in: bytes)[0]
            let back = try WOFF.decode(WOFF.encode(original))
            #expect(tags(back) == tags(original))
            for t in original.tables { #expect(back[t.tag] == t.data, "\(t.tag)") }
            #expect(back.data() == bytes)
        }
    }

    @Test func woffHeaderAndDirectoryFollowTheSpec() throws {
        let font = makeFont()
        let w = WOFF.encode(font)
        var r = try ByteReader(w)
        #expect(try r.u32() == Int(WOFF.signature))
        #expect(try r.u32() == Int(SFNT.trueType))
        #expect(try r.u32() == w.count)
        #expect(try r.u16() == font.tables.count)
        #expect(try r.u16() == 0)
        #expect(try r.u32() == sfntFile(font).count)
        #expect(try r.u16() == 2)  // the font's revision, 2.5
        #expect(try r.u16() == 0x8000)
        for _ in 0..<5 { #expect(try r.u32() == 0) }
        var last = -1
        var first = Int.max
        for _ in font.tables {
            let tag = FontTag(UInt32(try r.u32()))
            let (offset, stored, length, checksum) = (try r.u32(), try r.u32(), try r.u32(), try r.u32())
            let table = try #require(font.tables.first { $0.tag == tag })
            #expect(Int(tag.value) > last)
            last = Int(tag.value)
            first = min(first, offset)
            #expect(offset % 4 == 0)
            #expect(offset + stored <= w.count)
            #expect(length == table.data.count)
            #expect(stored <= length)
            #expect(UInt32(checksum) == sum32(zeroedHead(table)))
            if tag == customTag {
                // Noise doesn't compress, so it's stored as it is.
                #expect(stored == length)
                #expect(Array(w[offset..<offset + stored]) == noise)
            }
            if tag == "glyf" { #expect(stored < length) }
        }
        #expect(first == 44 + 20 * font.tables.count)
        #expect(w.count % 4 == 0)
    }

    // MARK: WOFF2 glyph transform

    @Test func glyphsRepackAndUnpackToTheSameGlyphs() throws {
        for long in [false, true] {
            let font = makeFont(longLoca: long)
            let packed = try #require(try WOFF2.packGlyphs(font))
            let back = try WOFF2.unpackGlyphs(packed)
            var rebuilt = font
            rebuilt.tables = font.tables.map {
                $0.tag == "glyf"
                    ? .init(tag: $0.tag, data: back.glyf) : $0.tag == "loca" ? .init(tag: $0.tag, data: back.loca) : $0
            }
            #expect(try parseGlyphs(rebuilt) == glyphSet.map(expected))
            #expect(try locaIsValid(rebuilt))
            #expect(back.leftEdges == glyphSet.map(leftEdge))
        }
        #expect(try WOFF2.packGlyphs(makeCFFFont()) == nil)
    }

    @Test func woff2RoundTripsEveryTable() throws {
        for long in [false, true] {
            let font = makeFont(longLoca: long)
            let p = WOFF2.prepare(font)
            // `loca` straight after `glyf`, both transformed; the signature is gone.
            let order = p.entries.map(\.tag)
            #expect(order.firstIndex(of: "loca") == order.firstIndex(of: "glyf").map { $0 + 1 })
            #expect(p.entries.filter { ["glyf", "loca"].contains($0.tag.description) }.map(\.version) == [0, 0])
            #expect(!order.contains("DSIG"))

            let bytes = WOFF2.assemble(p, compressed: try Brotli.encode(p.stream))
            var r = try ByteReader(bytes)
            #expect(try r.u32() == Int(WOFF2.signature))
            #expect(try r.u32() == Int(SFNT.trueType))
            #expect(try r.u32() == bytes.count)
            #expect(try r.u16() == p.entries.count)
            #expect(bytes.count % 4 == 0)

            let back = try WOFF2.decode(bytes)
            var want = font
            want.tables = font.tables.filter { $0.tag != "DSIG" }
            #expect(Set(tags(back)) == Set(tags(want)))
            for t in want.tables where !["glyf", "loca", "head"].contains(t.tag.description) {
                #expect(back[t.tag] == t.data, "\(t.tag)")
            }
            // The head is marked as having been through a lossless transform (bit 11).
            var head = try #require(font["head"])
            head[16] |= 0x08
            #expect(back["head"] == head)
            #expect(back[customTag] == noise)
            #expect(try parseGlyphs(back) == glyphSet.map(expected))
            #expect(try locaIsValid(back))
            // And the .ttf made from it reads again.
            #expect(try parseGlyphs(try SFNT.fonts(in: back.data())[0]) == glyphSet.map(expected))
        }
    }

    @Test func glyphsThatCantBeRepackedGoInUntransformed() throws {
        // `loca` longer than the glyphs need, and short offsets that 4-byte padding would overflow.
        let tooLong = makeFont(extraLoca: 2)
        let tight = makeFont(Array(repeating: .simple(contours: [[Pt(x: 1, y: 1)]]), count: 7000), pad: 2)
        #expect(tight["glyf"]?.count ?? 0 <= 0x1FFFE)
        for font in [tooLong, tight] {
            #expect(throws: ConvertError.self) { try WOFF2.packGlyphs(font) }
            let p = WOFF2.prepare(font)
            #expect(p.entries.filter { ["glyf", "loca"].contains($0.tag.description) }.map(\.version) == [3, 3])
            let back = try WOFF2.decode(WOFF2.assemble(p, compressed: try Brotli.encode(p.stream)))
            #expect(back["glyf"] == font["glyf"])
            #expect(back["loca"] == font["loca"])
        }
        // Hand-packed glyphs that overflow short offsets are rejected on the way in, too.
        let glyf = tight["glyf"] ?? []
        let packed = try WOFF2.packGlyphs(glyf: glyf, loca: tight["loca"] ?? [], count: 7000, longOffsets: 0)
        #expect(throws: ConvertError.self) { try WOFF2.unpackGlyphs(packed) }
    }

    @Test func metricsTransformDecodesInEveryCombination() throws {
        let font = makeFont()
        let metrics = glyphSet.count - 3
        let p = WOFF2.prepare(font)
        let advances = (0..<metrics).flatMap { i -> [UInt8] in
            var b: [UInt8] = []
            b.put16(500 + i)
            return b
        }
        func bearings(_ range: Range<Int>) -> [UInt8] {
            var b: [UInt8] = []
            for g in range { b.put16(leftEdge(glyphSet[g])) }
            return b
        }
        let proportional = bearings(0..<metrics)
        let monospaced = bearings(metrics..<glyphSet.count)
        for (flags, rest) in [(1, monospaced), (2, proportional), (3, [])] {
            let bytes = try woff2File(p, replacing: "hmtx", version: 1, with: [UInt8(flags)] + advances + rest)
            #expect(try WOFF2.decode(bytes)["hmtx"] == font["hmtx"], "flags \(flags)")
        }
        // Nothing left out, a reserved bit, or cut short: rejected.
        for bad in [[0] + advances + proportional + monospaced, [4] + advances, [3] + advances.dropLast()] {
            let bytes = try woff2File(p, replacing: "hmtx", version: 1, with: bad)
            #expect(throws: ConvertError.self) { try WOFF2.decode(bytes) }
        }
        // Without transformed glyphs there are no left edges to rebuild bearings from.
        let plain = WOFF2.prepare(makeFont(extraLoca: 2))
        let bytes = try woff2File(plain, replacing: "hmtx", version: 1, with: [3] + advances)
        #expect(throws: ConvertError.self) { try WOFF2.decode(bytes) }
    }

    @Test func woff2RejectsWhatTheSpecRejects() throws {
        let p = WOFF2.prepare(makeFont())
        // Reserved glyph transform versions, a transformed `loca` of the wrong size, an unknown transform.
        for (tag, version, length) in [("glyf", 1, nil), ("glyf", 2, nil), ("loca", 0, 6), ("name", 1, nil)]
            as [(FontTag, Int, Int?)]
        {
            let bytes = try woff2File(p, replacing: tag, version: version, length: length)
            #expect(throws: ConvertError.self, "\(tag) \(version)") { try WOFF2.decode(bytes) }
        }
        // Several fonts in one file.
        var collection = try woff2File(makeFont())
        collection.replaceSubrange(4..<8, with: Array("ttcf".utf8))
        let error = #expect(throws: ConvertError.self) { try WOFF2.decode(collection) }
        #expect(error?.message == "WOFF2 files that hold several fonts aren't supported")
    }

    // MARK: Variable-length numbers

    @Test func base128RoundTripsAndRejectsBadEncodings() throws {
        for value in [0, 1, 127, 128, 16383, 16384, 0x1F_FFFF, 0x20_0000, 0xFFF_FFFF, 0x1000_0000, 0xFFFF_FFFF] {
            var bytes: [UInt8] = []
            WOFF2.putBase128(&bytes, value)
            var r = try ByteReader(bytes)
            #expect(try WOFF2.base128(&r) == value)
            #expect(r.remaining == 0)
        }
        // A leading zero, more than 32 bits, six bytes, cut short.
        for bad: [UInt8] in [
            [0x80, 0x01], [0x90, 0x80, 0x80, 0x80, 0x00], [0x81, 0x80, 0x80, 0x80, 0x80, 0x00], [0x81], [],
        ] {
            var r = try ByteReader(bad)
            #expect(throws: ConvertError.self, "\(bad)") { try WOFF2.base128(&r) }
        }
    }

    @Test func short255RoundTripsAndRejectsTruncation() throws {
        for value in [0, 1, 252, 253, 254, 255, 505, 506, 507, 761, 762, 763, 1000, 65535] {
            var bytes: [UInt8] = []
            WOFF2.putShort255(&bytes, value)
            var r = try ByteReader(bytes)
            #expect(try WOFF2.short255(&r) == value)
            #expect(r.remaining == 0)
        }
        var r = try ByteReader([253, 0x01, 0x00, 254, 0, 255, 0])
        #expect(try [WOFF2.short255(&r), WOFF2.short255(&r), WOFF2.short255(&r)] == [256, 506, 253])
        for bad: [UInt8] in [[253, 0x01], [254], [255], []] {
            var r = try ByteReader(bad)
            #expect(throws: ConvertError.self) { try WOFF2.short255(&r) }
        }
    }

    // MARK: Brotli

    @Test func brotliRoundTripsAndChecksTheSize() async throws {
        let data = Array(sfntFile(makeFont()))
        let small = try Brotli.encode(data)
        #expect(try Brotli.decode(small[...], to: data.count) == data)
        for size in [data.count - 1, data.count + 1, 0] {
            #expect(throws: ConvertError.self) { try Brotli.decode(small[...], to: size) }
        }
        #expect(throws: ConvertError.self) { try Brotli.decode(noise[...], to: 1000) }
        #expect(throws: ConvertError.self) { try Brotli.decode([UInt8]()[...], to: 10) }
        #expect(throws: ConvertError.self) { try Brotli.decode(small.dropLast(small.count / 2), to: data.count) }
        // The best this Mac can do (the brotli tool when it's installed) still unpacks to the input.
        let best = try await Brotli.best(data, runner: Runner())
        #expect(try Brotli.decode(best[...], to: data.count) == data)
        let stopped = Runner()
        stopped.cancel()
        await #expect(throws: StoppedError.self) { try await Brotli.best(data, runner: stopped) }
    }

    // MARK: Damaged and hostile files

    private static let samples: [(name: String, bytes: [UInt8])] = {
        let font = makeFont()
        let p = WOFF2.prepare(font)
        return [
            ("ttf", sfntFile(font)), ("woff", WOFF.encode(font)),
            ("woff2", WOFF2.assemble(p, compressed: (try? Brotli.encode(p.stream)) ?? [])),
            ("ttc", collectionFile([font, makeFont(names: nil)])),
        ]
    }()

    /// Reads `bytes` as a font and, when that works, writes every format: none may crash.
    private func survive(_ bytes: [UInt8]) {
        guard let fonts = try? FontConv.read(bytes) else { return }
        for font in fonts {
            _ = font.data()
            _ = WOFF.encode(font)
            _ = WOFF2.prepare(font)
            _ = font.name
        }
    }

    @Test(.timeLimit(.minutes(2)))
    func everyCutOffFileFailsCleanly() throws {
        for (name, bytes) in Self.samples {
            #expect(try FontConv.read(bytes).count == (name == "ttc" ? 2 : 1))
            for n in 0..<bytes.count { survive(Array(bytes[..<n])) }
        }
    }

    @Test(.timeLimit(.minutes(2)))
    func corruptedBytesFailCleanly() throws {
        var rng = Seeded(2024)
        func corrupt(_ bytes: [UInt8], within range: Range<Int>? = nil) -> [UInt8] {
            let range = range ?? 0..<bytes.count
            var b = bytes
            for _ in 0..<(1 + rng.next() % 3) {
                b[range.lowerBound + rng.next() % range.count] ^= UInt8(1 + rng.next() % 255)
            }
            return b
        }
        // Fewer for the desktop formats: most damaged ones still read, and are then written as WOFF2.
        let rounds = ["ttf": 1000, "woff": 1500, "woff2": 1500, "ttc": 500]
        for (name, bytes) in Self.samples {
            for _ in 0..<(rounds[name] ?? 0) { survive(corrupt(bytes)) }
        }
        // Inside the parts compression hides: the glyph tables, the transformed glyphs, the WOFF2 stream.
        let font = makeFont()
        let glyf = try #require(font.tables.firstIndex { $0.tag == "glyf" })
        for _ in 0..<800 {
            var damaged = font
            damaged.tables[glyf].data = corrupt(font.tables[glyf].data)
            _ = try? WOFF2.packGlyphs(damaged)
        }
        let packed = try #require(try WOFF2.packGlyphs(font))
        for _ in 0..<2000 { _ = try? WOFF2.unpackGlyphs(corrupt(packed)) }
        var p = WOFF2.prepare(font)
        let stream = p.stream
        for _ in 0..<500 {
            p.stream = corrupt(stream)
            survive(WOFF2.assemble(p, compressed: try Brotli.encode(p.stream)))
        }
    }

    @Test(.timeLimit(.minutes(1)))
    func hostileCountsSizesAndOffsetsAreRejected() throws {
        let (ttf, woff, woff2) = (Self.samples[0].bytes, Self.samples[1].bytes, Self.samples[2].bytes)
        func patched(_ bytes: [UInt8], at offset: Int, _ value: [UInt8]) -> [UInt8] {
            var b = bytes
            b.replaceSubrange(offset..<offset + value.count, with: value)
            return b
        }
        let hostile: [(String, [UInt8])] = [
            ("sfnt numTables", patched(ttf, at: 4, [0xFF, 0xFF])),
            ("sfnt offset past the end", patched(ttf, at: 12 + 8, [0xFF, 0xFF, 0xFF, 0xF0])),
            ("woff numTables", patched(woff, at: 12, [0xFF, 0xFF])),
            ("woff origLength", patched(woff, at: 44 + 12, [0xFF, 0xFF, 0xFF, 0xFF])),
            ("woff overlapping tables", patched(woff, at: 44 + 20 + 4, Array(woff[48..<52]))),
            ("woff2 numTables", patched(woff2, at: 12, [0xFF, 0xFF])),
            ("woff2 compressed size", patched(woff2, at: 20, [0xFF, 0xFF, 0xFF, 0x00])),
        ]
        for (name, bytes) in hostile {
            #expect(throws: ConvertError.self, "\(name)") { try FontConv.read(bytes) }
        }

        // A directory listing the whole file as a table, over and over.
        var copies: [UInt8] = []
        copies.put32(Int(SFNT.trueType))
        for v in [1024, 0, 0, 0] { copies.put16(v) }
        for i in 0..<1024 { for v in [0x4100_0000 + i, 0, 0, 12 + 16 * 1024] { copies.put32(v) } }
        #expect(throws: ConvertError.self) { try SFNT.fonts(in: copies) }
        // A collection naming the same font thousands of times.
        var many: [UInt8] = Array("ttcf".utf8)
        many.put32(0x0001_0000)
        many.put32(4096)
        for _ in 0..<4096 { many.put32(12 + 4 * 4096) }
        #expect(throws: ConvertError.self) { try SFNT.fonts(in: many + ttf) }

        // Tables claiming a gigabyte, with almost no data behind them.
        let started = Date()
        var p = WOFF2.prepare(makeCFFFont())
        p.entries.append(("Huge", 0, (1 << 30) - 2000, nil))
        #expect(throws: ConvertError.self) {
            try WOFF2.decode(WOFF2.assemble(p, compressed: try Brotli.encode(p.stream)))
        }
        #expect(Date().timeIntervalSince(started) < 5)

        // Transformed glyphs that claim more than their streams hold.
        var header: [UInt8] = []
        for v in [0, 0, 65535, 0] { header.put16(v) }
        for _ in 0..<7 { header.put32(0) }
        #expect(throws: ConvertError.self) { try WOFF2.unpackGlyphs(header) }
        var contours = header
        contours.replaceSubrange(4..<6, with: [0, 1])
        contours.replaceSubrange(8..<12, with: [0, 0, 0, 2])
        contours.replaceSubrange(28..<32, with: [0, 0, 0, 4])
        #expect(throws: ConvertError.self) { try WOFF2.unpackGlyphs(contours + [0x7F, 0xFF, 0, 0, 0, 0]) }
    }

    @Test(.timeLimit(.minutes(1)))
    func glyphsWithBillionsOfPointsAreRefused() throws {
        // 65,535 points whose steps are all zero take ~530 bytes as TrueType (repeated flags, no
        // coordinates); 300 of them would be 20 million points to repack.
        let one = encode(.simple(contours: [(0..<65535).map { _ in Pt(x: 0, y: 0) }]))
        var font = makeFont(Array(repeating: .empty, count: 300), longLoca: true)
        var loca: [UInt8] = []
        for g in 0...300 { loca.put32(g * one.count) }
        for (i, t) in font.tables.enumerated() where ["glyf", "loca"].contains(t.tag.description) {
            font.tables[i].data = t.tag == "glyf" ? Array([[UInt8]](repeating: one, count: 300).joined()) : loca
        }
        #expect(one.count % 2 == 0 && one.count < 600)
        #expect(throws: ConvertError.self) { try WOFF2.packGlyphs(font) }
        #expect(WOFF2.prepare(font).entries.contains { $0.tag == "glyf" && $0.version == 3 })
    }

    // MARK: Converting files

    @Test func desktopFontBecomesWebFontsNextToIt() async throws {
        let input = try file("Test.ttf", sfntFile(makeFont()))
        let woff2 = try await convert(input, to: "font-woff2")
        let woff = try await convert(input, to: "font-woff")
        #expect(woff2.map(\.lastPathComponent) == ["Test.woff2"])
        #expect(woff.map(\.lastPathComponent) == ["Test.woff"])
        #expect(contents == ["Test.ttf", "Test.woff", "Test.woff2"])
        let original = try read(input)[0]
        try expectSame(try read(woff[0])[0], original)
        // WOFF2 drops the signature and marks the head as transformed.
        var want = original
        want.tables.removeAll { $0.tag == "DSIG" }
        if let head = want.tables.firstIndex(where: { $0.tag == "head" }) { want.tables[head].data[16] |= 0x08 }
        try expectSame(try read(woff2[0])[0], want, glyphsOnly: true)
    }

    @Test func webFontsBecomeTTFOrOTFByOutlines() async throws {
        let ttf = try await convert(try file("Glyphs.woff2", try woff2File(makeFont())), to: "font-desktop")
        #expect(ttf.map(\.lastPathComponent) == ["Glyphs.ttf"])
        #expect(try parseGlyphs(try read(ttf[0])[0]) == glyphSet.map(expected))
        let otf = try await convert(try file("Curves.woff2", try woff2File(makeCFFFont())), to: "font-desktop")
        #expect(otf.map(\.lastPathComponent) == ["Curves.otf"])
        #expect(try read(otf[0])[0]["CFF "] == makeCFFFont()["CFF "])
        let fromWOFF = try await convert(try file("Old.woff", WOFF.encode(makeCFFFont())), to: "font-desktop")
        #expect(fromWOFF.map(\.lastPathComponent) == ["Old.otf"])
    }

    @Test func sameFormatIsWrittenAgainUnderTheNextFreeName() async throws {
        let font = try SFNT.fonts(in: sfntFile(makeFont()))[0]
        let cases: [(String, [UInt8], String)] = [
            ("a.ttf", sfntFile(makeFont()), "font-desktop"), ("b.otf", sfntFile(makeCFFFont()), "font-desktop"),
            ("c.woff", WOFF.encode(font), "font-woff"), ("d.woff2", try woff2File(font), "font-woff2"),
        ]
        for (name, bytes, id) in cases {
            let input = try file(name, bytes)
            let out = try await convert(input, to: id)
            let ext = (name as NSString).pathExtension
            #expect(out.map(\.lastPathComponent) == ["\((name as NSString).deletingPathExtension) 2.\(ext)"])
            #expect(try Data(contentsOf: input) == Data(bytes))
            try expectSame(try read(out[0])[0], try read(input)[0], glyphsOnly: ext == "woff2")
        }
        // A well-formed desktop font comes out as an exact copy.
        #expect(try Data(contentsOf: dir.appendingPathComponent("a 2.ttf")) == Data(sfntFile(makeFont())))
    }

    @Test func collectionBecomesAFolderOfFontsNamedAfterThem() async throws {
        let fonts = [
            makeFont(names: [NameRecord(text: "Alpha")]), makeFont(names: [NameRecord(text: "Alpha")]),
            makeFont(names: nil), makeFont(names: [NameRecord(text: "Beta/Bold:Italic")]),
        ]
        let input = try file("Family.ttc", collectionFile(fonts))
        let out = try await convert(input, to: "font-woff2")
        #expect(out.map(\.lastPathComponent) == ["Family fonts"])
        let files = try FileManager.default.contentsOfDirectory(atPath: out[0].path).sorted()
        #expect(files == ["Alpha 2.woff2", "Alpha.woff2", "Beta-Bold-Italic.woff2", "Family 3.woff2"])
        for f in files {
            #expect(try parseGlyphs(try read(out[0].appendingPathComponent(f))[0]) == glyphSet.map(expected))
        }

        // A name that another font's renaming would take is still never written twice.
        let clash = ["X 2", "X", "X"].map { makeFont(names: [NameRecord(text: $0)]) }
        let desktop = try await convert(try file("Clash.ttc", collectionFile(clash)), to: "font-desktop")
        #expect(
            try FileManager.default.contentsOfDirectory(atPath: desktop[0].path).sorted() == [
                "X 2.ttf", "X 3.ttf", "X.ttf",
            ])
    }

    @Test func stoppedConversionsLeaveNothingBehind() async throws {
        let single = try file("One.ttf", sfntFile(makeFont()))
        let family = try file("Two.ttc", collectionFile([makeFont(), makeFont(names: nil)]))
        let before = contents
        let runner = Runner()
        runner.cancel()
        for input in [single, family] {
            for id in ["font-woff2", "font-woff", "font-desktop"] {
                await #expect(throws: StoppedError.self, "\(id)") {
                    try await self.convert(input, to: id, runner: runner)
                }
            }
        }
        #expect(contents == before)
    }

    @Test func filesThatArentFontsSaySo() async throws {
        let text = try file("notes.ttf", Array("Just some notes, not a font.\n".utf8))
        let error = await #expect(throws: ConvertError.self) { try await self.convert(text, to: "font-woff2") }
        #expect(error?.message == FontError.notAFont.message)
        let damaged = try file("cut.ttf", Array(Self.samples[0].bytes.prefix(200)))
        let cut = await #expect(throws: ConvertError.self) { try await self.convert(damaged, to: "font-woff") }
        #expect(cut?.message == FontError.damaged.message)
        let gone = await #expect(throws: ConvertError.self) {
            try await self.convert(self.dir.appendingPathComponent("missing.ttf"), to: "font-woff")
        }
        #expect(gone?.message == "Couldn't open this font")
        #expect(contents == ["cut.ttf", "notes.ttf"])
    }

    // MARK: Recognising fonts

    @Test func fontSignaturesAreRecognised() throws {
        let ttf = Self.samples[0].bytes
        var apple = ttf
        apple.replaceSubrange(0..<4, with: Array("true".utf8))
        let cases: [([UInt8], String)] = [
            (ttf, "ttf"), (apple, "ttf"), (sfntFile(makeCFFFont()), "otf"), (Self.samples[3].bytes, "ttc"),
            (Self.samples[1].bytes, "woff"), (Self.samples[2].bytes, "woff2"),
        ]
        for (bytes, ext) in cases {
            let format = Sniff.format(Data(bytes))
            #expect(format?.ext == ext)
            #expect(format?.kinds == [.font], "\(ext)")
        }
        // Other binary data can start with 00 01 00 00; its "table count" doesn't add up.
        for other: [UInt8] in [
            [0, 1, 0, 0, 0x12, 0x34, 0, 0], [0, 1, 0, 0, 0, 5, 0, 0x41], [0, 1, 0, 0, 0, 0, 0, 0],
        ] {
            let bytes = other + [UInt8](repeating: 0x20, count: 56)
            #expect(Sniff.format(Data(bytes))?.kinds.contains(.font) != true, "\(other)")
        }
    }

    @Test func fontExtensionsAreFonts() throws {
        for ext in ["ttf", "otf", "ttc", "otc", "woff", "woff2", "TTF"] {
            #expect(MediaKind.of(dir.appendingPathComponent("font.\(ext)")) == .font, "\(ext)")
        }
        let renamed = try file("song.mp3", Self.samples[0].bytes)
        let error = #expect(throws: ConvertError.self) { try Sniff.verify(renamed, as: .audio) }
        #expect(
            error?.message
                == "This isn't really a .mp3 file: its contents are a TrueType font. Rename it to .ttf and convert it again."
        )
        try Sniff.verify(try file("web.woff", Self.samples[2].bytes), as: .font)
    }
}
