#!/usr/bin/env python3
"""Generate the Pokémon Emerald Rogue (EX) id tables PKForge reads.

Usage:
    gen_rogue_data.py --rogue <pokeemerald-rogue checkout> --pkhex-text <PKHeX.Core/Resources/text> \
                      --out src/PKForge.Engine/Rogue/Data

The Rogue source is run through clang's preprocessor (no ARM toolchain needed), so every macro in the
species / item tables is already expanded when we parse it. Every numeric expression is then evaluated by a
tiny generated C program, so nothing is guessed from the C text. The commit the tables were built from is
written into each file's header, which is what makes them regenerable (see docs/rogue-support-notes.md).
"""
import argparse
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path


def run(cmd, **kw):
    return subprocess.run(cmd, check=True, capture_output=True, text=True, **kw)


def preprocess(rogue: Path, source: str, stub: Path) -> str:
    out = run([
        "clang", "-E", "-P", "-w",
        "-DROGUE_EXPANSION=1", "-DROGUE_RELEASE=1", "-DMODERN=1", "-DTESTING=0", "-UNDEBUG",
        "-I", str(stub), "-iquote", "include", "-iquote", "src", "-iquote", "gflib", "-I", "include", "-I", "gflib",
        source,
    ], cwd=rogue)
    return out.stdout


def match_close(text: str, start: int) -> int:
    """Index of the bracket closing the one at text[start] ({ or ( )."""
    pairs = {"{": "}", "(": ")"}
    stack = [pairs[text[start]]]
    i = start + 1
    in_str = False
    while stack:
        c = text[i]
        if in_str:
            if c == "\\":
                i += 1
            elif c == '"':
                in_str = False
        elif c == '"':
            in_str = True
        elif c in pairs:
            stack.append(pairs[c])
        elif c == stack[-1]:
            stack.pop()
        i += 1
    return i - 1


def split_top_level(body: str):
    """Split a designated-initialiser body into (field, expr) pairs at depth 0."""
    items, i, n = [], 0, len(body)
    while i < n:
        m = re.compile(r"\s*\.(\w+)\s*=\s*").match(body, i)
        if not m:
            i += 1
            continue
        field, j = m.group(1), m.end()
        depth, in_str, k = 0, False, j
        while k < n:
            c = body[k]
            if in_str:
                if c == "\\":
                    k += 1
                elif c == '"':
                    in_str = False
            elif c == '"':
                in_str = True
            elif c in "({[":
                depth += 1
            elif c in ")}]":
                depth -= 1
            elif c == "," and depth == 0:
                break
            k += 1
        items.append((field, body[j:k].strip()))
        i = k + 1
    return items


def parse_array(text: str, header_regex: str):
    """{index_expr: {field: expr}} for the designated-initialiser array whose declaration matches."""
    m = re.search(header_regex, text)
    if not m:
        raise SystemExit(f"array not found: {header_regex}")
    open_brace = text.index("{", m.end() - 1) if text[m.end() - 1] != "{" else m.end() - 1
    end = match_close(text, open_brace)
    body = text[open_brace + 1:end]
    entries, i = {}, 0
    key_re = re.compile(r"\[([^\]]+)\]\s*=\s*\{")
    while True:
        km = key_re.search(body, i)
        if not km:
            break
        o = km.end() - 1
        c = match_close(body, o)
        entries[km.group(1).strip()] = body[o + 1:c]
        i = c + 1
    return entries


def evaluator(rogue: Path, exprs, extra_includes=()):
    """Evaluate integer C expressions with gcc; returns {expr: int}.

    Expressions that do not compile (aliases defined outside the included headers) are dropped from the
    result instead of aborting; callers skip rows whose key is missing.
    """
    uniq = sorted(set(exprs))
    while True:
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "eval.c"
            lines = ["#include <stdio.h>", '#include "constants/pokedex.h"']
            lines += [f'#include "{h}"' for h in extra_includes]
            lines.append("int main(void){")
            for e in uniq:
                lines.append(f'printf("%d\\n",(int)({e}));')
            lines.append("return 0;}")
            src.write_text("\n".join(lines))
            exe = Path(tmp) / "eval"
            proc = subprocess.run(["gcc", "-w", "-DROGUE_EXPANSION=1", "-DROGUE_RELEASE=1", "-I", str(rogue / "include"),
                                   str(src), "-o", str(exe)], capture_output=True, text=True)
            if proc.returncode == 0:
                values = run([str(exe)]).stdout.split()
                return dict(zip(uniq, (int(v) for v in values)))
            bad = {int(m.group(1)) for m in re.finditer(r"eval\.c:(\d+):\d+: error", proc.stderr)}
            offset = len(lines) - len(uniq) - 1  # line number of the first printf is offset + 1
            drop = {uniq[b - offset - 1] for b in bad if 0 <= b - offset - 1 < len(uniq)}
            if not drop:
                raise SystemExit(proc.stderr[:2000])
            uniq = [e for e in uniq if e not in drop]


def cstr(expr: str) -> str:
    m = re.search(r'_\("((?:[^"\\]|\\.)*)"\)', expr)
    return m.group(1).replace("\\n", " ").replace("\\'", "'") if m else ""


def int_list(expr: str):
    return [x.strip() for x in expr.strip().strip("{}").split(",") if x.strip()]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rogue", required=True, type=Path)
    ap.add_argument("--pkhex-text", required=True, type=Path)
    ap.add_argument("--out", required=True, type=Path)
    args = ap.parse_args()
    rogue = args.rogue.resolve()
    commit = run(["git", "log", "-1", "--format=%H %ad"], cwd=rogue).stdout.strip()
    branch_note = os.environ.get("ROGUE_REF", "")
    args.out.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory() as tmp:
        stub = Path(tmp)
        (stub / "string.h").write_text(
            "#pragma once\ntypedef unsigned int size_t;\n"
            "void *memcpy(void*,const void*,size_t);void *memset(void*,int,size_t);"
            "int memcmp(const void*,const void*,size_t);size_t strlen(const char*);\n")
        pokemon_c = preprocess(rogue, "src/pokemon.c", stub)
        item_c = preprocess(rogue, "src/item.c", stub)
        text_pp = {}
        for name, src_file in (("moves", "src/data/text/move_names.h"), ("abilities", "src/data/text/abilities.h"),
                               ("moveinfo", "src/data/battle_moves.h")):
            tu = stub / f"{name}.c"
            tu.write_text('#include "global.h"\n#include "constants/moves.h"\n#include "constants/abilities.h"\n'
                          f'#include "{src_file[len("src/"):]}"\n')
            text_pp[name] = preprocess(rogue, str(tu), stub)
        tu = stub / "rogueitems.c"
        tu.write_text('#include "global.h"\n#include "constants/items.h"\n#include "rogue.h"\n'
                      '#include "data/rogue_items.h"\n')
        try:
            rogue_items_c = preprocess(rogue, str(tu), stub)
        except Exception:
            rogue_items_c = ""

    # ---- species -----------------------------------------------------------------------------
    species_raw = parse_array(pokemon_c, r"const struct SpeciesInfo gSpeciesInfo\[\]\s*=\s*\{")
    spec_fields = {}
    exprs = set()
    for key, body in species_raw.items():
        f = dict(split_top_level(body))
        spec_fields[key] = f
        exprs.add(key)
        for fld in ("baseHP", "baseAttack", "baseDefense", "baseSpeed", "baseSpAttack", "baseSpDefense",
                    "genderRatio", "growthRate", "natDexNum"):
            if fld in f:
                exprs.add(f[fld])
        for fld in ("types", "abilities"):
            if fld in f:
                exprs.update(int_list(f[fld]))
    ev = evaluator(rogue, exprs)

    # forms: the position inside the family's form table is the form index
    forms_pp = pokemon_c
    form_index = {}
    for m in re.finditer(r"static const u16 s\w+FormSpeciesIdTable\[\]\s*=\s*\{", forms_pp):
        o = m.end() - 1
        body = forms_pp[o + 1:match_close(forms_pp, o)]
        ids = [x.strip() for x in body.split(",") if x.strip() and "0xffff" not in x.lower()]
        vals = evaluator(rogue, ids)
        for pos, sid in enumerate(ids):
            form_index.setdefault(vals[sid], (vals[ids[0]], pos))

    species_rows = []
    for key, f in spec_fields.items():
        sid = ev[key]
        def v(name, default=0):
            return ev[f[name]] if name in f else default
        types = [ev[t] for t in int_list(f["types"])] if "types" in f else [0, 0]
        abil = [ev[a] for a in int_list(f["abilities"])] if "abilities" in f else [0, 0, 0]
        base_sid, form = form_index.get(sid, (sid, 0))
        species_rows.append((
            sid, cstr(f.get("speciesName", "")), v("natDexNum"), base_sid, form,
            v("baseHP"), v("baseAttack"), v("baseDefense"), v("baseSpeed"), v("baseSpAttack"), v("baseSpDefense"),
            types[0], types[1] if len(types) > 1 else types[0],
            abil[0], abil[1] if len(abil) > 1 else 0, abil[2] if len(abil) > 2 else 0,
            v("genderRatio"), v("growthRate"),
        ))
    species_rows.sort()

    # ---- moves / abilities (id -> name) -------------------------------------------------------
    def names(text, header_regex):
        arr = parse_named(text, header_regex)
        return arr

    def parse_named(text, header_regex):
        m = re.search(header_regex, text)
        if not m:
            raise SystemExit(f"table not found: {header_regex}")
        o = text.index("{", m.end() - 1)
        body = text[o + 1:match_close(text, o)]
        out = {}
        for km in re.finditer(r"\[([^\]]+)\]\s*=\s*_\(\"((?:[^\"\\]|\\.)*)\"\)", body):
            out[km.group(1).strip()] = km.group(2)
        return out

    move_names_raw = parse_named(text_pp["moves"], r"const u8 gMoveNames\[[^\]]*\]\[[^\]]*\]\s*=\s*\{")
    ability_names_raw = parse_named(text_pp["abilities"], r"const u8 gAbilityNames\[[^\]]*\]\[[^\]]*\]\s*=\s*\{")
    mv = evaluator(rogue, move_names_raw.keys(), ("constants/moves.h",)) if False else None
    # the keys are already numeric after preprocessing, but may be expressions like (1 + 2)
    mev = evaluator(rogue, move_names_raw.keys())
    aev = evaluator(rogue, ability_names_raw.keys())

    pk = args.pkhex_text
    def read_lines(path):
        return [l.rstrip("\r\n") for l in path.read_text(encoding="utf-8").split("\n")]
    pk_moves = {n: i for i, n in enumerate(read_lines(pk / "other/en/text_Moves_en.txt")) if n}
    pk_abil = {n: i for i, n in enumerate(read_lines(pk / "other/en/text_Abilities_en.txt")) if n}
    pk_items = {}
    for i, n in enumerate(read_lines(pk / "items/text_Items_en.txt")):
        if n:
            pk_items.setdefault(n, i)
    import re as _re
    def _norm(t):
        return _re.sub(r"[^a-z0-9]", "", t.lower())
    pk_items_norm = {}
    for n, i in pk_items.items():
        pk_items_norm.setdefault(_norm(n), i)

    move_rows = sorted((mev[k], n, pk_moves.get(n, 0)) for k, n in move_names_raw.items())
    ability_rows = sorted((aev[k], n, pk_abil.get(n, 0)) for k, n in ability_names_raw.items())

    move_info = parse_array(text_pp["moveinfo"], r"const struct BattleMove gBattleMoves_Mainline\[[^\]]*\]\s*=\s*\{")
    pp_exprs = {}
    for k, body in move_info.items():
        f = dict(split_top_level(body))
        if "pp" in f:
            pp_exprs[k] = f["pp"]
    ppv = evaluator(rogue, set(pp_exprs.values()) | set(pp_exprs.keys()))
    move_pp = {ppv[k]: ppv[e] for k, e in pp_exprs.items() if k in ppv and e in ppv}
    move_rows = [(i, n, nat, move_pp.get(i, 0)) for i, n, nat in move_rows]

    # ---- items ------------------------------------------------------------------------------------
    items_raw = parse_array(item_c, r"const struct Item gItems\[\]\s*=\s*\{")
    item_fields = {k: dict(split_top_level(b)) for k, b in items_raw.items()}
    if rogue_items_c:
        for k, b in parse_array(rogue_items_c, r"const struct RogueItem gRogueItems\[[^\]]*\]\s*=\s*\{").items():
            f = dict(split_top_level(b))
            if "itemId" in f:
                item_fields["(" + f["itemId"] + ")"] = f
    ie = set(item_fields)
    for f in item_fields.values():
        if "pocket" in f:
            ie.add(f["pocket"])
    iev = evaluator(rogue, ie, ("constants/global.h", "constants/items.h", "constants/item.h"))
    item_rows = []
    for k, f in item_fields.items():
        name = cstr(f.get("name", ""))
        if k not in iev or ("pocket" in f and f["pocket"] not in iev):
            continue
        item_rows.append((iev[k], name, iev[f["pocket"]] if "pocket" in f else 0, pk_items.get(name, pk_items_norm.get(_norm(name), 0))))
    item_rows.sort()

    header = f"# generated by tools/rogue/gen_rogue_data.py from pokeemerald-rogue {commit} {branch_note}".strip()
    def dump(name, cols, rows):
        with open(args.out / name, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(header + "\n# " + "\t".join(cols) + "\n")
            for r in rows:
                fh.write("\t".join(str(x) for x in r) + "\n")

    dump("rogue_species.tsv",
         ["id", "name", "natdex", "baseId", "form", "hp", "atk", "def", "spe", "spa", "spd",
          "type1", "type2", "ability1", "ability2", "ability3", "genderRatio", "growth"], species_rows)
    dump("rogue_moves.tsv", ["id", "name", "national", "pp"], move_rows)
    dump("rogue_abilities.tsv", ["id", "name", "national"], ability_rows)
    dump("rogue_items.tsv", ["id", "name", "pocket", "national"], item_rows)
    print(f"species {len(species_rows)} moves {len(move_rows)} abilities {len(ability_rows)} items {len(item_rows)}")


if __name__ == "__main__":
    main()
