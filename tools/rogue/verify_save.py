#!/usr/bin/env python3
"""Reference decoder for Pokemon Emerald Rogue (EX) saves. The C# RogueFormat/RogueMon mirror this file.

    verify_save.py <save.sav> [data-dir]
"""
import struct, sys
from pathlib import Path

SECTOR, DATA, NSEC = 0x1000, 4084, 32
ORDERS = "GAEM GAME GEAM GEMA GMAE GMEA AGEM AGME AEGM AEMG AMGE AMEG EGAM EGMA EAGM EAMG EMGA EMAG MGAE MGEA MAGE MAEG MEGA MEAG".split()

def checksum(buf):
    t = sum(struct.unpack_from("<%dI" % (len(buf) // 4), buf)) & 0xFFFFFFFF
    return (t + (t >> 16)) & 0xFFFF

def slots(data):
    out = {}
    for s in range(NSEC):
        o = s * SECTOR
        sid, ck, sec, cnt = struct.unpack_from("<HHII", data, o + 0xFF4)
        if sid > 13 or sec != 0x08012025: continue
        ok = checksum(data[o:o + DATA]) == ck
        out.setdefault(s // 14 if s < 28 else 2, []).append((sid, o, cnt, ok))
    return out

def live(data):
    best = {}
    for s in range(28):
        o = s * SECTOR
        sid, ck, sec, cnt = struct.unpack_from("<HHII", data, o + 0xFF4)
        if sec != 0x08012025 or sid > 13: continue
        if sid not in best or cnt >= best[sid][1]: best[sid] = (o, cnt, checksum(data[o:o + DATA]) == ck)
    return best

def decode_box(raw):
    pid, otid = struct.unpack_from("<II", raw, 0)
    if pid == 0 and otid == 0: return None
    key = pid ^ otid
    sec = bytearray(raw[0x20:0x50])
    for i in range(0, 48, 4):
        struct.pack_into("<I", sec, i, struct.unpack_from("<I", sec, i)[0] ^ key)
    order = ORDERS[pid % 24]
    sub = {t: bytes(sec[i * 12:i * 12 + 12]) for i, t in enumerate(order)}
    if sum(struct.unpack_from("<24H", bytes(sec))) & 0xFFFF != struct.unpack_from("<H", raw, 0x1C)[0]: return None
    w0, w1 = struct.unpack_from("<II", sub["G"], 0)
    g = sub["G"]; a = sub["A"]; e = sub["E"]; m = sub["M"]
    iv = struct.unpack_from("<I", m, 4)[0]; flags = struct.unpack_from("<I", m, 8)[0]
    modifier = raw[0x12] >> 3
    return dict(pid=pid, otid=otid, species=w0 & 0x7FF, item=((w0 >> 11) & 0x3FF) | (((w0 >> 26) & 1) << 10),
                tera=(w0 >> 21) & 31, exp=w1 & 0x1FFFFF, friend=g[9], ball=struct.unpack_from("<H", g, 10)[0] & 31,
                moves=struct.unpack_from("<4H", a), pp=list(a[8:12]), evs=list(e[:6]),
                ivs=[(iv >> s) & 31 for s in (0, 5, 10, 15, 20, 25)], egg=(iv >> 30) & 1,
                shiny=flags & 7, gender=(flags >> 3) & 7, ability=(flags >> 29) & 3,
                nature=(pid % 25) ^ modifier, name=raw[8:18].hex(), lang=raw[0x12] & 7)

def main():
    data = Path(sys.argv[1]).read_bytes()[:0x20000]
    lv = live(data)
    print("sections", sorted(lv), "all checksums valid:", all(v[2] for v in lv.values()))
    sb1 = b"".join(data[lv[i][0]:lv[i][0] + DATA] for i in range(1, 5))
    st = b"".join(data[lv[i][0]:lv[i][0] + DATA] for i in range(5, 14))
    species = {}
    d = Path(sys.argv[2] if len(sys.argv) > 2 else Path(__file__).parents[2] / "src/PKForge.Engine/Rogue/Data")
    for l in (d / "rogue_species.tsv").read_text(encoding="utf-8").splitlines():
        if l and not l.startswith("#"):
            p = l.split("\t"); species[int(p[0])] = p[1]
    moves = {}
    for l in (d / "rogue_moves.tsv").read_text(encoding="utf-8").splitlines():
        if l and not l.startswith("#"):
            p = l.split("\t"); moves[int(p[0])] = p[1]
    n = sb1[564]
    print("party", n)
    def show(label, m, extra=""):
        print(label, species.get(m["species"], m["species"]), "nat", m["nature"], "ab", m["ability"], "shiny", m["shiny"],
              "gender", m["gender"], "exp", m["exp"], [moves.get(x, x) for x in m["moves"]], "ivs", m["ivs"], extra)
    for i in range(n):
        raw = sb1[568 + i * 104:568 + i * 104 + 104]
        m = decode_box(raw[:80])
        if m: show(f"P{i}", m, f"lvl {raw[84]} hp {struct.unpack_from('<H', raw, 86)[0]}/{struct.unpack_from('<H', raw, 88)[0]}")
    for b in range(10):
        for s in range(30):
            m = decode_box(st[4 + (b * 30 + s) * 80:4 + (b * 30 + s) * 80 + 80])
            if m: show(f"B{b + 1}:{s + 1}", m)

main()
