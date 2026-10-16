# Pokémon Emerald Rogue (EX, v2.2.1) support: verified save layout and plan

Sources: Pokabbie/pokeemerald-rogue (branches `expansion` a6adfcf1, `expansion-dev` 853b671b; tags
EX-v2.0, EX-v2.1; no v2.2.1 tag was visible) and a real save, `Emerald_Rogue_v2.2.1-EX.sav`.
Everything marked VERIFIED was checked against that save. Offsets computed by compiling the headers
with clang are NOT reliable past the first few hundred bytes (the release build has fields the public
headers lack: SaveBlock2 and SaveBlock1 are bigger than the 3792 / 13112 bytes clang reports), so every
offset must be confirmed on real saves.

## Envelope (VERIFIED)

- File: 131088 bytes = 0x20000 + a 16-byte RTC tail (mGBA); PKForge's existing SramPadding handling applies.
- 32 sectors of 0x1000. Sector data is 4084 bytes; footer at +0xFF4: id u16, checksum u16,
  security u32 = 0x08012025, save counter u32.
- Slot A = sectors 0-13 (counter 98), slot B = 14-27 (counter 97); the newest counter wins. Sectors
  inside a slot are rotated (slot B has id 0 in file sector 27). Sectors 28-31 are blank (0xFF).
- Checksum is the vanilla algorithm (sum of u32 words, fold to 16 bits). Using the full 4084-byte data
  window verifies for all 28 sectors, because the unused tail is zero. Do not use vanilla's 3968 window,
  and do not trust the per-block sizes from the headers.
- SaveBlock2 = id 0. SaveBlock1 = ids 1-4 concatenated (4 x 4084). PokemonStorage = ids 5-13
  concatenated (9 x 4084).

## Pokémon (VERIFIED, 12 mons decoded, all checksums valid)

- `BoxPokemon` 80 bytes, `Pokemon` 104 bytes (party adds status @80, level @84, HP @86, MaxHP @88,
  5 stats, then 4 Rogue bytes @100).
- Standard Gen 3 encryption: 48 secure bytes XOR PID XOR OTID, substruct order by PID % 24
  (GAEM, GAME, GEAM, ... table, letters give the type stored at each position).
- Substruct G: species 11 bits, item1 10 bits, tera type 5 bits, item2 1 bit; exp 21 bits; ppBonuses @8;
  friendship @9; ball 5 bits @10. A: 4 x u16 moves + PP. M: IV word at +4 (5 bits each), egg bit 30.
- Species ids are the `expansion` branch constants (e.g. 1379 = Flutter Mane, 993 = Hisuian Growlithe,
  1068 = Rotom Mow). Forms live above 1025, and the Z-A megas sit at the end of the table.

## Offsets (VERIFIED on the sample unless noted)

- SaveBlock1: party count u8 @564, party @568 (6 x 104), money u32 @1192 XOR key, PC items @1200,
  bag @1400 (item u16, quantity u16 XOR low 16 bits of key), dex arrays @12468 and @12665
  (197 bytes each, bit index = species id; which one is "seen" vs "caught" is still to confirm),
  data reaches at least offset 13385. Flags and vars offsets are unverified.
- SaveBlock2: player name @0 (8 bytes, Gen 3 charset), TID/SID @10, play time @14 (hours u16, min,
  sec, frames), encryption key u32 @76.
- PokemonStorage: currentBox @0, boxes[14][30] @4 (80 bytes each), box names @33604 (9 bytes each),
  wallpapers @33730, fusion storage @33744 (offsets from headers; box data itself verified).

## Boxes 11-14 are not user boxes (VERIFIED)

Boxes 0-9 hold normal mons (10 valid in the sample). Boxes 10-13 hold Rogue's own run data (57 blobs
failed the Pokémon checksum). PKForge must expose 10 boxes and keep 10-13 byte-for-byte untouched.
Four fusion mons are stored in a separate area.

## Further verified facts (implementation)

- Nature is a stored field: `PID % 25 XOR hiddenNatureModifier` (5 bits at box offset 0x12, bits 3-7). Shiny is a stored
  flag (substruct M word @8, bits 0-2), gender flag bits 3-5 (1 = female), ability slot bits 29-30 (0-2).
  No PID rerolling is needed for nature/shiny/gender/ability edits.
- Experience is a flat 300 per level for every species (`Rogue_ModifyExperienceTables`): level = exp / 300.
- Ball ids are `BALL_POKE = 0 ... BALL_ROGUE = 27` (pokeball.h), not item ids.
- Bag: one flat list of 450 slots at SaveBlock1+1400 (item u16, quantity u16 XOR low 16 bits of the key); pockets come
  from the item table. PC items at +1200 are NOT XORed.
- SaveBlock2: TID u16 @10, SID u16 @12, play hours u16 @14, minutes @16. Box names (9 bytes) @33604 of storage, verified.
- Engine/UI tables are generated: `tools/rogue/gen_rogue_data.py` (species, moves incl. base PP, abilities, items).
- `tools/rogue/verify_save.py` is the Python reference decoder; `RogueSessionTests` mirrors it in C#.

## Status

Implemented on branch `rogue-support`: `RogueFormat`, `RogueMon`, `RogueData`, `RogueEngineSession`, detection ahead of
the stock and CFRU routes (`SaveFormat.EmeraldRogue`, `SaveLayoutFamily.EmeraldRogue`), write-safety and generator routing.
Not implemented yet: Bank import/export, Pokédex editing (which of the two 197-byte arrays is seen vs caught is
unconfirmed), flags/vars, form-index mapping for Rogue-only forms, legality.

## Original plan (branch `rogue-support` of the local fork)

1. `src/PKForge.Engine/Rogue/`: `RogueFormat` (envelope, slot choice, 4084 windows), `RogueMon`
   (80/104-byte codec), `RogueData` (generated tables), `RogueEngineSession` (modelled on
   `UnboundEngineSession`).
2. Detection: footer security is retail, so detect structurally (14 consecutive ids, all 4084-window
   checksums valid, 128 KB size, valid decoded mon in box 1), ahead of the vanilla Emerald path.
3. Generate species, move, item and ability tables from the Rogue source at a pinned commit; map
   forms and Z-A megas to PKHeX species + form where one exists, and mark Rogue-only forms as
   not transferable.
4. Lock boxes 10-13; no legality verdict.
5. Tests: this sample (read, edit, write, re-read byte-identical), plus a fresh and a late save.
