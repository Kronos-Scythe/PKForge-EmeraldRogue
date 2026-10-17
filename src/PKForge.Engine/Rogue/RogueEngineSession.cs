using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;
using static PKForge.Engine.Rogue.RogueFormat;

namespace PKForge.Engine.Rogue;

/// <summary>
/// A Pokémon Emerald Rogue (EX) save as a first-class PKForge session. The save keeps the GBA flash
/// envelope but checksums full 4084-byte windows (see <see cref="RogueFormat"/>), stores Pokémon in
/// pokeemerald-expansion's bit layouts (see <see cref="RogueMon"/>) and keeps the run's own data in
/// storage boxes 11-14, which are never exposed here and never rewritten. Legality is not
/// evaluated: Rogue's species, moves and encounters do not exist in PKHeX, so this session is
/// HaX-by-design like the other ROM-hack sessions.
/// </summary>
internal sealed class RogueEngineSession : ISaveEngineSession
{
    public const string SnapshotTag = "EMERALD_ROGUE";
    public const int Boxes = 10; // boxes 11-14 hold Rogue's run data and stay locked
    public const int LockedFirstBox = 10;
    private const int StorageBoxOffset = 4;
    private const int SlotsPerBox = 30;
    private const int BoxNamesOffset = 33604;
    private const int BoxNameLength = 9;
    private const int PartyCountOffset = 564;
    private const int PartyOffset = 568;
    private const int MoneyOffset = 1192;
    private const int BagOffset = 1400;
    private const int BagCapacity = 450;
    private const int EncryptionKeyOffset = 76;

    private readonly byte[] _data;
    private readonly byte[] _originalBytes;
    private readonly int[] _sections;
    private readonly byte[] _sb2;
    private readonly byte[] _sb1;
    private readonly byte[] _storage;
    private readonly string? _displayName;
    private bool _disposed;

    public RogueEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        // Emulator padding and mGBA's RTC footer sit past the flash chip; Serialize re-appends them.
        _data = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span));
        if (_data.Length != FileSize || !IsEmeraldRogue(_data))
            throw new InvalidDataException("These bytes are not a Pokémon Emerald Rogue save.");
        _sections = LiveSections(_data);
        _sb2 = ReadBlock(_data, _sections, 0, 1);
        _sb1 = ReadBlock(_data, _sections, 1, 4);
        _storage = ReadBlock(_data, _sections, 5, 9);
        _eventBaseline = _sb1.AsSpan(RogueEvents.FlagsOffset, RogueEvents.RegionLength).ToArray();
        _displayName = displayName;
    }

    // ── event flags and script vars (served to the shared event editor) ──
    private readonly byte[] _eventBaseline;

    internal bool GetFlag(int id, bool asOpened = false)
    {
        if ((uint)id >= RogueEvents.FlagCount) return false;
        var source = asOpened ? _eventBaseline.AsSpan() : _sb1.AsSpan(RogueEvents.FlagsOffset, RogueEvents.RegionLength);
        return (source[id >> 3] & (1 << (id & 7))) != 0;
    }

    internal void SetFlag(int id, bool value)
    {
        if ((uint)id >= RogueEvents.FlagCount) throw new ArgumentOutOfRangeException(nameof(id));
        var offset = RogueEvents.FlagsOffset + (id >> 3);
        var mask = (byte)(1 << (id & 7));
        _sb1[offset] = value ? (byte)(_sb1[offset] | mask) : (byte)(_sb1[offset] & ~mask);
        CommitSb1();
    }

    internal int GetVar(int index, bool asOpened = false)
    {
        if ((uint)index >= RogueEvents.VarCount) return 0;
        var source = asOpened ? _eventBaseline.AsSpan() : _sb1.AsSpan(RogueEvents.FlagsOffset, RogueEvents.RegionLength);
        return BinaryPrimitives.ReadUInt16LittleEndian(source[(RogueEvents.FlagBytes + index * 2)..]);
    }

    internal void SetVar(int index, int value)
    {
        if ((uint)index >= RogueEvents.VarCount) throw new ArgumentOutOfRangeException(nameof(index));
        BinaryPrimitives.WriteUInt16LittleEndian(_sb1.AsSpan(RogueEvents.VarsOffset + index * 2), (ushort)value);
        CommitSb1();
    }

    public SaveSnapshot Snapshot => BuildSnapshot(_displayName);

    public int Generation => 3;
    public IReadOnlyList<string> GameNames => ["Emerald Rogue"];
    public int MaxSpeciesId => 1025;
    public int BoxCount => Boxes;
    public int BoxSlotCount => SlotsPerBox;

    /// <summary>Trainer name, for the library card (SaveEngine.TryDescribe).</summary>
    public string TrainerName => StringConverter3.GetString(_sb2.AsSpan(0, 8), jp: false);

    public string PlayTime => $"{BinaryPrimitives.ReadUInt16LittleEndian(_sb2.AsSpan(14))}:{_sb2[16]:00}";

    // ── commits ──
    private void CommitStorage() => WriteBlock(_data, _sections, 5, _storage);
    private void CommitSb1() => WriteBlock(_data, _sections, 1, _sb1);
    private void CommitSb2() => WriteBlock(_data, _sections, 0, _sb2);
    private void Commit(int box) { if (box == -1) CommitSb1(); else CommitStorage(); }

    // ── slots ──
    private int PartyCount => Math.Min((int)_sb1[PartyCountOffset], 6);

    private static int BoxMonOffset(int box, int slot) => StorageBoxOffset + (box * SlotsPerBox + slot) * RogueMon.BoxSize;

    private RogueMon? TryMon(int box, int slot)
    {
        if (box == -1)
            return (uint)slot < (uint)PartyCount ? new RogueMon(_sb1, PartyOffset + slot * RogueMon.PartySize, party: true) : null;
        if ((uint)box >= Boxes || (uint)slot >= SlotsPerBox) return null;
        return new RogueMon(_storage, BoxMonOffset(box, slot), party: false);
    }

    /// <summary>Raw bytes of every slot (party first, then the PC) for the write-safety structural diff.</summary>
    internal IEnumerable<SlotImage> SlotImages()
    {
        for (var box = -1; box < Boxes; box++)
        for (var slot = 0; slot < (box == -1 ? 6 : SlotsPerBox); slot++)
        {
            var mon = TryMon(box, slot);
            yield return mon is null
                ? new SlotImage(new SlotRef(box, slot), [], Empty: true, Valid: true)
                : new SlotImage(new SlotRef(box, slot), mon.Buffer.AsSpan(mon.Offset, mon.Size).ToArray(),
                    mon.IsEmpty, mon.IsEmpty || mon.LooksValid);
        }
    }

    public EntityDetail ReadEntity(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return Empty(box, slot);

        var species = mon.Species;
        var moves = mon.Moves;
        var ability = RogueData.AbilityOf(species, mon.AbilityNum);
        var ivs = mon.IVs;
        var evs = mon.EVs;
        return new EntityDetail(
            box, slot, false,
            RogueData.NationalIdOf(species),
            RogueData.SpeciesName(species),
            RogueData.FormOf(species),
            mon.Nickname,
            mon.Level,
            mon.Nature,
            RogueData.AbilityToNational(ability),
            mon.HeldItem,
            RogueData.MoveToNational(moves[0]), RogueData.MoveToNational(moves[1]),
            RogueData.MoveToNational(moves[2]), RogueData.MoveToNational(moves[3]),
            ivs, evs,
            mon.IsShiny,
            RogueData.BallToDisplay(mon.Ball),
            mon.OriginalTrainerName,
            RogueData.TypesOf(species),
            mon.Gender,
            mon.Friendship,
            mon.PartyStats ?? RogueData.ComputeStats(species, mon.Level, mon.Nature, ivs, evs),
            mon.CurrentHp);

        static EntityDetail Empty(int box, int slot) => new(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);
    }

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        var mon = TryMon(box, slot) ?? throw new InvalidOperationException("Cannot edit an empty slot.");
        var before = mon.Buffer.AsSpan(mon.Offset, mon.Size).ToArray();

        // Edits arrive in PKHeX ids (the editor echoes every field it was shown), so a field only
        // writes when it differs from the bridged current value, and then through the reverse bridge.
        if (edit.Species is { } national && national != RogueData.NationalIdOf(mon.Species)
            && RogueData.SpeciesFromNational(national) is > 0 and var species)
            mon.Species = species;
        if (edit.Nickname is { Length: > 0 } nickname && nickname != mon.Nickname)
            mon.Nickname = nickname;
        if (edit.Level is { } level && Math.Clamp(level, 1, 100) != mon.Level)
        {
            mon.Experience = RogueData.ExperienceAtLevel(level);
            mon.SetPartyLevelAndStats(Math.Clamp(level, 1, 100), fullHeal: false);
        }
        if (edit.Nature is { } nature && nature != mon.Nature && nature is >= 0 and < 25)
            mon.Nature = nature;
        if (edit.Ability is { } ability && ability != RogueData.AbilityToNational(RogueData.AbilityOf(mon.Species, mon.AbilityNum))
            && RogueData.AbilityFromNational(ability) is > 0 and var romAbility)
            SetAbility(mon, romAbility);
        // Held items travel as ROM ids end to end, matching GetItemNames() (the picker index).
        if (edit.HeldItem is { } item && item != mon.HeldItem && item is >= 0 and <= ushort.MaxValue)
            mon.HeldItem = item;
        int?[] wanted = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        var stored = mon.Moves;
        var pp = mon.MovePp;
        var movesChanged = false;
        for (var i = 0; i < 4; i++)
        {
            if (wanted[i] is not { } move || move == RogueData.MoveToNational(stored[i])) continue;
            var romMove = move == 0 ? 0 : RogueData.MoveFromNational(move);
            if (move != 0 && romMove == 0) continue; // not a move this build knows
            stored[i] = romMove;
            pp[i] = RogueData.MoveBasePp(romMove);
            movesChanged = true;
        }
        if (movesChanged)
        {
            mon.Moves = stored;
            mon.MovePp = pp;
        }
        if (edit.IVs is { Count: 6 } ivs)
            mon.IVs = [.. ivs.Select(v => Math.Clamp(v, 0, 31))];
        if (edit.EVs is { Count: 6 } evs)
            mon.EVs = [.. evs.Select(v => Math.Clamp(v, 0, 255))];
        if (edit.IsShiny is { } shiny && mon.IsShiny != shiny)
            mon.IsShiny = shiny;
        if (edit.Ball is { } ball && RogueData.TryStoreBall(ball, out var romBall))
            mon.Ball = romBall;
        if (edit.OriginalTrainer is { Length: > 0 } ot && ot != mon.OriginalTrainerName)
            mon.SetOriginalTrainerName(ot);
        if (edit.Gender is 0 or 1 && RogueData.GenderRatio(mon.Species) is not (0 or 254 or 255) && edit.Gender != mon.Gender)
            mon.GenderFlag = edit.Gender.Value;
        if (edit.Friendship is { } friendship && friendship != mon.Friendship)
            mon.Friendship = Math.Clamp(friendship, 0, 255);

        if (mon.Buffer.AsSpan(mon.Offset, mon.Size).SequenceEqual(before)) return;
        // Stats depend on level, nature, IVs and EVs: keep the party tail consistent.
        if (mon.Party)
            mon.SetPartyLevelAndStats(mon.Level, fullHeal: false);
        Commit(box);
    }

    /// <summary>The ability lives in a 2-bit slot number; pick the slot that holds the requested ability.</summary>
    private static void SetAbility(RogueMon mon, int abilityId)
    {
        for (var slotNumber = 0; slotNumber < 3; slotNumber++)
        {
            if (RogueData.AbilityOf(mon.Species, slotNumber) != abilityId) continue;
            mon.AbilityNum = slotNumber;
            return;
        }
    }

    // ── slot movement ──
    private static void CopyBox(RogueMon source, RogueMon target)
    {
        // Box <-> box takes every byte; box -> party also needs the computed tail.
        source.Buffer.AsSpan(source.Offset, RogueMon.BoxSize).CopyTo(target.Buffer.AsSpan(target.Offset, RogueMon.BoxSize));
    }

    private void Place(RogueMon source, RogueMon target)
    {
        if (source.Party == target.Party)
        {
            source.Buffer.AsSpan(source.Offset, source.Size).CopyTo(target.Buffer.AsSpan(target.Offset, target.Size));
            return;
        }
        CopyBox(source, target);
        if (!target.Party) return;
        target.Buffer.AsSpan(target.Offset + RogueMon.BoxSize, RogueMon.PartySize - RogueMon.BoxSize).Clear();
        var rebuilt = new RogueMon(target.Buffer, target.Offset, party: true);
        rebuilt.SetPartyLevelAndStats(RogueData.LevelForExperience(rebuilt.Experience), fullHeal: true);
    }

    private void SetPartyCount(int count) => _sb1[PartyCountOffset] = (byte)count;

    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = TryMon(fromBox, fromSlot);
        if (source is null || !source.LooksValid) return false;
        RogueMon target;
        if (toBox == -1)
        {
            if (PartyCount >= 6) return false;
            target = new RogueMon(_sb1, PartyOffset + PartyCount * RogueMon.PartySize, party: true);
        }
        else
        {
            if (TryMon(toBox, toSlot) is not { } slotMon || !slotMon.IsEmpty) return false;
            target = slotMon;
        }
        Place(source, target);
        if (toBox == -1) SetPartyCount(PartyCount + 1);
        Commit(toBox);
        return true;
    }

    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        var source = TryMon(fromBox, fromSlot) ?? throw new InvalidOperationException("The source slot is empty.");
        if (fromBox == toBox && fromSlot == toSlot) return;

        if (fromBox == -1 && toBox == -1 && (uint)toSlot < (uint)PartyCount)
        {
            // Inside the party, onto another Pokémon: the two swap places, as in the games.
            var from = _sb1.AsSpan(PartyOffset + fromSlot * RogueMon.PartySize, RogueMon.PartySize);
            var to = _sb1.AsSpan(PartyOffset + toSlot * RogueMon.PartySize, RogueMon.PartySize);
            var held = from.ToArray();
            to.CopyTo(from);
            held.CopyTo(to);
            CommitSb1();
            return;
        }

        var sourceCopy = source.Buffer.AsSpan(source.Offset, source.Size).ToArray();
        var sourceView = new RogueMon(sourceCopy, 0, source.Party);

        if (toBox == -1)
        {
            // Moving into the party appends, exactly like the games.
            if (PartyCount >= 6) throw new InvalidOperationException("The party is full.");
            var party = new RogueMon(_sb1, PartyOffset + PartyCount * RogueMon.PartySize, party: true);
            Place(sourceView, party);
            SetPartyCount(PartyCount + 1);
            if (fromBox == -1) CompactPartyFrom(fromSlot);
            else ClearSlot(fromBox, fromSlot);
            CommitSb1();
            if (fromBox != -1) CommitStorage();
            return;
        }

        var destination = TryMon(toBox, toSlot) ?? throw new InvalidOperationException("The destination slot does not exist.");
        var destinationCopy = destination.Buffer.AsSpan(destination.Offset, destination.Size).ToArray();
        var occupied = !destination.IsEmpty;
        Place(sourceView, destination);
        if (source.Party)
        {
            if (occupied) Place(new RogueMon(destinationCopy, 0, party: false), source);
            else CompactPartyFrom(fromSlot);
        }
        else
        {
            destinationCopy.AsSpan(0, RogueMon.BoxSize).CopyTo(source.Buffer.AsSpan(source.Offset, RogueMon.BoxSize));
        }
        CommitSb1();
        CommitStorage();
    }

    private void CompactPartyFrom(int removedSlot)
    {
        var count = PartyCount;
        for (var i = removedSlot; i < count - 1; i++)
            _sb1.AsSpan(PartyOffset + (i + 1) * RogueMon.PartySize, RogueMon.PartySize).CopyTo(_sb1.AsSpan(PartyOffset + i * RogueMon.PartySize));
        _sb1.AsSpan(PartyOffset + (count - 1) * RogueMon.PartySize, RogueMon.PartySize).Clear();
        SetPartyCount(count - 1);
    }

    public void ReleaseSlot(int box, int slot)
    {
        if (box == -1)
        {
            if ((uint)slot >= (uint)PartyCount) return;
            CompactPartyFrom(slot);
            CommitSb1();
            return;
        }
        ClearSlot(box, slot);
        CommitStorage();
    }

    private void ClearSlot(int box, int slot)
    {
        if (box == -1) return;
        if ((uint)box >= Boxes || (uint)slot >= SlotsPerBox) return;
        _storage.AsSpan(BoxMonOffset(box, slot), RogueMon.BoxSize).Clear();
    }

    public void ClearBox(int box)
    {
        if ((uint)box >= Boxes) return;
        for (var slot = 0; slot < SlotsPerBox; slot++)
            ClearSlot(box, slot);
        CommitStorage();
    }

    /// <summary>The Bank's facts for an exported mon, read through Emerald Rogue's tables.</summary>
    internal static BankEntryInfo Describe(RogueMon mon, string sourceName) =>
        new(RogueData.NationalIdOf(mon.Species), RogueData.FormOf(mon.Species), mon.IsShiny, mon.Nickname, mon.Level, 3, sourceName,
            RadicalRed.CfruEntity.Rogue, RogueData.ItemToNational(mon.HeldItem), default(SpriteTraits));

    public SlotExport ExportSlot(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            throw new InvalidOperationException("That slot is empty.");
        // The 80-byte record exactly as the game boxes it, so the Bank keeps every byte.
        var bytes = mon.Buffer.AsSpan(mon.Offset, RogueMon.BoxSize).ToArray();
        return new SlotExport(bytes, $"{RogueData.SpeciesName(mon.Species)}.pk3ex", RadicalRed.CfruEntity.Rogue);
    }

    /// <summary>Takes back an Emerald Rogue record (Bank, QR, shared file). Other games' formats are not converted yet.</summary>
    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (EntityBytes.Normalize(format) != RadicalRed.CfruEntity.Rogue)
            return ImportForeign(box, slot, fileBytes, format);
        if (fileBytes.Length != RogueMon.BoxSize) return false;
        if (!new RogueMon(fileBytes.ToArray(), 0, party: false).LooksValid) return false;

        if (box == -1)
        {
            if (PartyCount >= 6) return false;
            var party = new RogueMon(_sb1, PartyOffset + PartyCount * RogueMon.PartySize, party: true);
            party.Buffer.AsSpan(party.Offset, RogueMon.PartySize).Clear();
            fileBytes.CopyTo(party.Buffer.AsSpan(party.Offset, RogueMon.BoxSize));
            var rebuilt = new RogueMon(party.Buffer, party.Offset, party: true);
            rebuilt.SetPartyLevelAndStats(RogueData.LevelForExperience(rebuilt.Experience), fullHeal: true);
            SetPartyCount(PartyCount + 1);
            CommitSb1();
            return true;
        }
        if (TryMon(box, slot) is null) return false;
        fileBytes.CopyTo(_storage.AsSpan(BoxMonOffset(box, slot), RogueMon.BoxSize));
        CommitStorage();
        return true;
    }

    /// <summary>
    /// A Pokémon from another game (.pk9, .pk8, .pk3, ...) enters Rogue the way a Showdown set does: its species, level,
    /// nature, ability, item, moves, EVs/IVs and shininess are rebuilt from Rogue's own tables. Box slots only.
    /// </summary>
    private bool ImportForeign(int box, int slot, byte[] fileBytes, string? format)
    {
        if (box < 0 || TryMon(box, slot) is null) return false;
        PKM? pk;
        try { pk = EntityBytes.Parse(fileBytes, format); }
        catch (Exception) { return false; }
        if (pk is null || pk.Species == 0) return false;
        try
        {
            var text = ShowdownParsing.GetShowdownText(pk);
            if (!string.IsNullOrWhiteSpace(pk.Nickname) && pk.IsNicknamed) { /* the set text already carries the nickname */ }
            return GenerateFromShowdownText(box, slot, text).Success;
        }
        catch (Exception) { return false; }
    }

    // ── generation (the wizard) ──
    internal GenerationOutcome GenerateInto(int box, int slot, GenerationRequest request) =>
        Generate(box, slot, request.Species, request.Level, request.Shiny, request.Nature, request.Ball, [.. request.Moves ?? []]);

    internal GenerationOutcome GenerateFromShowdownText(int box, int slot, string text)
    {
        var set = new ShowdownSet(text);
        if (set.Species == 0)
            return new GenerationOutcome(false, "Could not read the set (no species).");
        int? nature = set.Nature is Nature.Random ? null : (int)set.Nature;
        return Generate(box, slot, set.Species, set.Level, set.Shiny, nature, null, [.. set.Moves.Select(move => (int)move)], set);
    }

    private GenerationOutcome Generate(int box, int slot, int nationalSpecies, int? level, bool shiny, int? nature, int? ball, int[] nationalMoves, ShowdownSet? set = null)
    {
        var species = RogueData.SpeciesFromNational(nationalSpecies);
        if (species <= 0)
            return new GenerationOutcome(false, "That Pokémon is not in Emerald Rogue's species table.");
        RogueMon target;
        var party = box == -1;
        if (party)
        {
            if (PartyCount >= 6) return new GenerationOutcome(false, "The party is full.");
            target = new RogueMon(_sb1, PartyOffset + PartyCount * RogueMon.PartySize, party: true);
        }
        else
        {
            target = TryMon(box, slot) ?? throw new InvalidOperationException("That slot does not exist in this box.");
        }
        target.Buffer.AsSpan(target.Offset, target.Size).Clear();

        var trainer = GetTrainer();
        var rnd = Random.Shared;
        var pid = (uint)rnd.NextInt64(1, uint.MaxValue);
        var otid = (uint)((trainer.SID << 16) | (trainer.TID & 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(target.Buffer.AsSpan(target.Offset), pid);
        BinaryPrimitives.WriteUInt32LittleEndian(target.Buffer.AsSpan(target.Offset + 4), otid);
        var mon = new RogueMon(target.Buffer, target.Offset, target.Party);
        mon.Reset();
        mon.Buffer[mon.Offset + 0x12] = 2; // language English, no hidden nature modifier
        mon.Buffer[mon.Offset + 0x13] = 2; // hasSpecies
        mon.Species = species;
        mon.Experience = RogueData.ExperienceAtLevel(level ?? 5);
        mon.Nickname = RogueData.SpeciesName(species);
        mon.SetOriginalTrainerName(trainer.Name.Length > 0 ? trainer.Name : "PKForge");
        mon.Friendship = 70;
        mon.Ball = ball is { } wanted && RogueData.TryStoreBall(wanted, out var romBall) ? romBall : 0;
        var moves = nationalMoves.Where(move => move > 0).Select(RogueData.MoveFromNational).Where(move => move > 0).Take(4).ToArray();
        mon.Moves = moves;
        mon.MovePp = [.. moves.Select(RogueData.MoveBasePp)];
        mon.IVs = [31, 31, 31, 31, 31, 31];
        var ratio = RogueData.GenderRatio(species);
        mon.GenderFlag = ratio is 0 or 255 ? 0 : ratio == 254 ? 1 : rnd.Next(256) < ratio ? 1 : 0;
        mon.IsShiny = shiny;
        if (nature is { } wantedNature and >= 0 and < 25)
            mon.Nature = wantedNature;
        if (set is not null)
        {
            // A Showdown/Smogon set also names the item, ability, EVs and IVs; PKHeX lists stats HP/Atk/Def/Spe/SpA/SpD.
            if (set.HeldItem > 0 && RogueData.ItemFromNational(set.HeldItem) is > 0 and var romItem)
                mon.HeldItem = romItem;
            if (set.Ability > 0 && RogueData.AbilityFromNational(set.Ability) is > 0 and var romAbility)
                SetAbility(mon, romAbility);
            static int[] ToAppOrder(IReadOnlyList<int> values) => [values[0], values[1], values[2], values[4], values[5], values[3]];
            if (set.EVs is { Length: 6 } evs && evs.Sum() > 0)
                mon.EVs = [.. ToAppOrder(evs).Select(v => Math.Clamp(v, 0, 255))];
            if (set.IVs is { Length: 6 } ivs)
                mon.IVs = [.. ToAppOrder(ivs).Select(v => Math.Clamp(v, 0, 31))];
        }
        if (party)
        {
            mon.SetPartyLevelAndStats(Math.Clamp(level ?? 5, 1, 100), fullHeal: true);
            SetPartyCount(PartyCount + 1);
            CommitSb1();
        }
        else
        {
            CommitStorage();
        }
        return new GenerationOutcome(true,
            $"{RogueData.SpeciesName(species)} generated for Emerald Rogue from the game's own tables (no legality data exists for this hack).");
    }

    // ── snapshot / serialize ──
    private SaveSnapshot BuildSnapshot(string? displayName)
    {
        var slots = new List<SlotSummary>(Boxes * SlotsPerBox + 6);
        for (var box = -1; box < Boxes; box++)
        for (var slot = 0; slot < (box == -1 ? 6 : SlotsPerBox); slot++)
        {
            var mon = TryMon(box, slot);
            slots.Add(mon is { LooksValid: true } valid
                ? new SlotSummary(box, slot, RogueData.NationalIdOf(valid.Species), valid.Nickname, valid.IsShiny, true,
                    RogueData.FormOf(valid.Species), valid.IsEgg, HeldItemId(valid))
                : new SlotSummary(box, slot, null, null, false, true));
        }
        return new SaveSnapshot(SnapshotTag, 3, _originalBytes.ToArray(), slots, displayName);
    }

    /// <summary>The grid's held-item flag: the bridged national id, or -1 when the item has no national twin.</summary>
    private static int HeldItemId(RogueMon mon) =>
        mon.HeldItem;

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Every mutation commits to _data immediately; serialization is a plain copy.
        return SramPadding.Restore(_data, _originalBytes);
    }

    // ── reference data for the editor ──
    public IReadOnlyList<int> GetAbilityChoices(int species, int form)
    {
        var rogue = RogueData.SpeciesFromNational(species, form);
        var (a1, a2, hidden) = RogueData.AbilityIds(rogue);
        var choices = new List<int>();
        foreach (var ability in new[] { a1, a2, hidden })
            if (RogueData.AbilityToNational(ability) is > 0 and var national && !choices.Contains(national))
                choices.Add(national);
        return choices;
    }

    // The UI asks for this on every picker row and binding, so build it once.
    private static readonly Lazy<string[]> ItemNameList = new(() =>
    {
        var names = new string[RogueData.ItemCount];
        for (var id = 0; id < names.Length; id++)
            names[id] = RogueData.ItemName(id);
        return names;
    });

    public IReadOnlyList<string> GetItemNames() => ItemNameList.Value;

    public IReadOnlyList<string> GetFormChoices(int species) => [""];
    public IReadOnlyList<int> GetSpeciesTypes(int species) => RogueData.TypesOf(RogueData.SpeciesFromNational(species));

    public BaseStats GetBaseStats(int species)
    {
        var s = RogueData.BaseStats(RogueData.SpeciesFromNational(species));
        return new BaseStats(s[0], s[1], s[2], s[3], s[4], s[5]);
    }

    private static readonly string[] NatureNames =
    [
        "Hardy", "Lonely", "Brave", "Adamant", "Naughty", "Bold", "Docile", "Relaxed", "Impish", "Lax",
        "Timid", "Hasty", "Serious", "Jolly", "Naive", "Modest", "Mild", "Quiet", "Bashful", "Rash",
        "Calm", "Gentle", "Sassy", "Careful", "Quirky",
    ];

    public string GetShowdownText(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid) return string.Empty;
        var text = $"{RogueData.SpeciesName(mon.Species)}\nLevel: {mon.Level}\n{NatureNames[Math.Clamp(mon.Nature, 0, 24)]} Nature\nAbility: {RogueData.AbilityName(RogueData.AbilityOf(mon.Species, mon.AbilityNum))}";
        if (mon.HeldItem != 0)
            text += $"\nItem: {RogueData.ItemName(mon.HeldItem)}";
        foreach (var move in mon.Moves)
            if (move != 0)
                text += $"\n- {RogueData.MoveName(move)}";
        return text;
    }

    public string ExportBoxShowdown(int box)
    {
        var parts = new List<string>();
        for (var slot = 0; slot < SlotsPerBox; slot++)
        {
            var text = GetShowdownText(box, slot);
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join("\n\n", parts);
    }

    public RngInfo GetRngInfo(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            return new RngInfo(0, null, 0, false, false, [0, 0, 0, 0, 0, 0], 0, 2);
        return new RngInfo(mon.Pid, null, mon.Nature, mon.IsShiny, NatureRerollSupported: true, mon.IVs,
            RogueData.AbilityToNational(RogueData.AbilityOf(mon.Species, mon.AbilityNum)), mon.Gender);
    }

    public TrainingCaps GetTrainingCaps() => new(31, 255);

    public bool RerollNatureKeepShiny(int box, int slot, int nature)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid || nature is < 0 or >= 25) return false;
        mon.Nature = nature; // the nature is a stored field: no personality change needed
        if (mon.Party) mon.SetPartyLevelAndStats(mon.Level, fullHeal: false);
        Commit(box);
        return true;
    }

    public string GetBoxName(int box)
    {
        if ((uint)box >= Boxes) return $"BOX {box + 1}";
        var name = StringConverter3.GetString(_storage.AsSpan(BoxNamesOffset + box * BoxNameLength, BoxNameLength), jp: false);
        return name.Length > 0 ? name : $"BOX {box + 1}";
    }

    // ── trainer ──
    public TrainerInfo GetTrainer()
    {
        var key = BinaryPrimitives.ReadUInt32LittleEndian(_sb2.AsSpan(EncryptionKeyOffset));
        var money = BinaryPrimitives.ReadUInt32LittleEndian(_sb1.AsSpan(MoneyOffset)) ^ key;
        return new TrainerInfo(TrainerName, BinaryPrimitives.ReadUInt16LittleEndian(_sb2.AsSpan(10)),
            BinaryPrimitives.ReadUInt16LittleEndian(_sb2.AsSpan(12)), money, _sb2[8] == 1 ? 1 : 0);
    }

    public void SetTrainer(TrainerInfo trainer)
    {
        var key = BinaryPrimitives.ReadUInt32LittleEndian(_sb2.AsSpan(EncryptionKeyOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(_sb1.AsSpan(MoneyOffset), Math.Min(trainer.Money, 999_999u) ^ key);
        BinaryPrimitives.WriteUInt16LittleEndian(_sb2.AsSpan(10), (ushort)trainer.TID);
        BinaryPrimitives.WriteUInt16LittleEndian(_sb2.AsSpan(12), (ushort)trainer.SID);
        if (trainer.Gender is 0 or 1) _sb2[8] = (byte)trainer.Gender; // playerGender: 0 male, 1 female
        if (trainer.Name.Length > 0 && trainer.Name != TrainerName)
        {
            var span = _sb2.AsSpan(0, 8);
            span.Fill(0xFF);
            StringConverter3.SetString(span, trainer.Name, 7, jp: false);
        }
        CommitSb1();
        CommitSb2();
    }

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            return new GenerationOutcome(false, "That slot is empty.");
        var trainer = GetTrainer();
        var name = profile?.OriginalTrainer ?? trainer.Name;
        mon.Otid = profile is null
            ? (uint)((trainer.SID << 16) | (trainer.TID & 0xFFFF))
            : (uint)((profile.SID << 16) | (profile.TID & 0xFFFF));
        mon.SetOriginalTrainerName(name);
        Commit(box);
        return new GenerationOutcome(true, $"{mon.Nickname} now belongs to {name}.");
    }

    // ── bag: one flat list of 450 (item u16, quantity u16 XOR key low 16) slots ──
    private static readonly string[] PouchNames = ["Items", "Balls", "TMs", "Berries", "Key Items"];

    private static string PouchOf(int itemId) => RogueData.ItemPocket(itemId) switch
    {
        5 => "Balls", 6 => "TMs", 7 => "Berries", 9 => "Key Items", _ => "Items",
    };

    private ushort QuantityKey => (ushort)BinaryPrimitives.ReadUInt32LittleEndian(_sb2.AsSpan(EncryptionKeyOffset));

    private List<BagItem> ReadBag()
    {
        var items = new List<BagItem>();
        for (var i = 0; i < BagCapacity; i++)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(_sb1.AsSpan(BagOffset + i * 4));
            if (id == 0) continue;
            var count = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(_sb1.AsSpan(BagOffset + i * 4 + 2)) ^ QuantityKey);
            items.Add(new BagItem(id, count));
        }
        return items;
    }

    private void WriteBag(IReadOnlyList<BagItem> items)
    {
        for (var i = 0; i < BagCapacity; i++)
        {
            var span = _sb1.AsSpan(BagOffset + i * 4, 4);
            if (i < items.Count)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)items[i].Id);
                BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)(items[i].Count ^ QuantityKey));
            }
            else
            {
                span.Clear();
            }
        }
        CommitSb1();
    }

    public IReadOnlyList<BagPouch> GetBag()
    {
        var all = ReadBag();
        return [.. PouchNames.Select(name => new BagPouch(name, [.. all.Where(item => PouchOf(item.Id) == name)]))];
    }

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) =>
        [.. RogueData.ItemIds.Where(id => PouchOf(id).Equals(pouchName, StringComparison.OrdinalIgnoreCase) && RogueData.ItemName(id) != "????????")
            .OrderBy(id => id)];

    public int SetItemCount(string pouchName, int itemId, int count)
    {
        if (!PouchNames.Contains(pouchName, StringComparer.OrdinalIgnoreCase) || itemId <= 0) return 0;
        var items = ReadBag();
        var index = items.FindIndex(item => item.Id == itemId);
        if (count <= 0)
        {
            if (index < 0) return 0;
            items.RemoveAt(index);
        }
        else if (index >= 0)
        {
            items[index] = new BagItem(itemId, Math.Min(count, 999));
        }
        else
        {
            if (items.Count >= BagCapacity) return 0;
            items.Add(new BagItem(itemId, Math.Min(count, 999)));
        }
        WriteBag(items);
        return items.FirstOrDefault(item => item.Id == itemId) is { } found ? found.Count : 0;
    }


    // ── hub buildings and upgrades (cost Build Parts) ──
    public IReadOnlyList<HubPart>? GetHubParts()
    {
        if (RogueHub.Locate(_storage) is not { } layout) return null;
        return [.. RogueHub.Parts.Select(p => new HubPart(p.IsArea, p.Id, p.Name, p.Cost, p.Target,
            RogueHub.Get(_storage, p.IsArea ? layout.AreaFlags : layout.UpgradeFlags, p.Id)))];
    }

    /// <summary>Builds or removes a hub area. A newly built area is placed on a free grid cell next to a built one.</summary>
    public GenerationOutcome SetHubArea(int area, bool built)
    {
        if (RogueHub.Locate(_storage) is not { } layout || (uint)area >= (uint)layout.AreaCount)
            return new GenerationOutcome(false, "This save's hub data could not be located.");
        if (RogueHub.Get(_storage, layout.AreaFlags, area) == built) return new GenerationOutcome(true, "No change.");
        var coordsAt = layout.Coords + area * layout.CoordSize;
        if (built)
        {
            var taken = new HashSet<(int, int)>();
            for (var other = 0; other < layout.AreaCount; other++)
                if (RogueHub.Get(_storage, layout.AreaFlags, other))
                    taken.Add(((sbyte)_storage[layout.Coords + other * layout.CoordSize], (sbyte)_storage[layout.Coords + other * layout.CoordSize + 1]));
            (int, int)? spot = null;
            foreach (var (x, y) in taken.OrderBy(c => Math.Abs(c.Item1) + Math.Abs(c.Item2)).ThenBy(c => c.Item1).ThenBy(c => c.Item2))
            {
                foreach (var (dx, dy) in new[] { (0, -1), (-1, 0), (1, 0), (0, 1) })
                    if (!taken.Contains((x + dx, y + dy))) { spot = (x + dx, y + dy); break; }
                if (spot is not null) break;
            }
            if (spot is not { } cell) return new GenerationOutcome(false, "No free hub cell was found.");
            _storage[coordsAt] = (byte)(sbyte)cell.Item1;
            _storage[coordsAt + 1] = (byte)(sbyte)cell.Item2;
        }
        else
        {
            _storage[coordsAt] = 0;
            _storage[coordsAt + 1] = 0;
        }
        RogueHub.Set(_storage, layout.AreaFlags, area, built);
        CommitStorage();
        return new GenerationOutcome(true, built ? "Area built." : "Area removed.");
    }

    public GenerationOutcome SetHubUpgrade(int upgrade, bool bought)
    {
        if (RogueHub.Locate(_storage) is not { } layout || (uint)upgrade >= (uint)layout.UpgradeCount)
            return new GenerationOutcome(false, "This save's hub data could not be located.");
        RogueHub.Set(_storage, layout.UpgradeFlags, upgrade, bought);
        CommitStorage();
        return new GenerationOutcome(true, bought ? "Upgrade unlocked." : "Upgrade removed.");
    }

    // ── potential / moves ──
    public PotentialInfo GetPotential(int box, int slot)
    {
        var mon = TryMon(box, slot);
        return new PotentialInfo(
            SupportsTera: false, TeraType: 0, TeraTypeName: "", TeraTypeOriginalName: "", TeraLocked: false,
            SupportsHyperTrain: false, HyperTrained: [false, false, false, false, false, false],
            SupportsAbilitySlot: true,
            AbilitySlot: mon is { LooksValid: true } valid ? valid.AbilityNum : 0,
            AbilitySlots: AbilitySlotChoices(mon), SupportsAwakening: false, Awakening: [],
            SupportsGanbaru: false, Ganbaru: [], GanbaruMaximums: []);
    }

    private static IReadOnlyList<NamedChoice> AbilitySlotChoices(RogueMon? mon)
    {
        if (mon is not { LooksValid: true }) return [];
        var (a1, a2, hidden) = RogueData.AbilityIds(mon.Species);
        var choices = new List<NamedChoice> { new(0, RogueData.AbilityName(a1)) };
        if (a2 != 0) choices.Add(new NamedChoice(1, RogueData.AbilityName(a2)));
        if (hidden != 0) choices.Add(new NamedChoice(2, RogueData.AbilityName(hidden)));
        return choices;
    }

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid || edit.AbilitySlot is not { } want) return;
        mon.AbilityNum = Math.Clamp(want, 0, 2);
        Commit(box);
    }

    public MoveDetails GetMoveDetails(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            return new MoveDetails([], false, []);
        var moves = mon.Moves;
        var pp = mon.MovePp;
        var ups = mon.PpBonuses;
        var slots = new List<MoveSlotDetail>(4);
        for (var i = 0; i < 4; i++)
        {
            var bonus = (ups >> (i * 2)) & 3;
            var basePp = RogueData.MoveBasePp(moves[i]);
            slots.Add(new MoveSlotDetail(RogueData.MoveToNational(moves[i]), pp[i], basePp + basePp * bonus / 5, bonus));
        }
        return new MoveDetails(slots, true, []);
    }

    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid) return;
        if (edit.PPUps is { Count: 4 } ups)
        {
            var packed = 0;
            for (var i = 0; i < 4; i++)
                packed |= Math.Clamp(ups[i], 0, 3) << (i * 2);
            mon.PpBonuses = packed;
        }
        if (edit.PP is { Count: 4 } pp)
            mon.MovePp = [.. pp.Select(value => Math.Clamp(value, 0, 255))];
        Commit(box);
    }

    // ── features not available for this hack yet ──
    // Pokédex: two parallel bit arrays in SaveBlock1, one bit per species id. Bit pair = state:
    // 1 seen, 2 caught, 3 caught-shiny (flags1 = low bit, flags2 = high bit).
    private const int DexFlags1Offset = 12176;
    private const int DexFlags2Offset = 12373;
    private const int DexFlagBytes = 197;

    private static int DexSpecies(int national) => RogueData.SpeciesFromNational(national, 0);

    private int DexState(int rogueSpecies)
    {
        if (rogueSpecies <= 0 || rogueSpecies >= DexFlagBytes * 8) return 0;
        var index = rogueSpecies / 8; var mask = 1 << (rogueSpecies % 8);
        return ((_sb1[DexFlags1Offset + index] & mask) != 0 ? 1 : 0) | ((_sb1[DexFlags2Offset + index] & mask) != 0 ? 2 : 0);
    }

    private void SetDexState(int rogueSpecies, int state)
    {
        if (rogueSpecies <= 0 || rogueSpecies >= DexFlagBytes * 8) return;
        var index = rogueSpecies / 8; var mask = (byte)(1 << (rogueSpecies % 8));
        _sb1[DexFlags1Offset + index] = (byte)((state & 1) != 0 ? _sb1[DexFlags1Offset + index] | mask : _sb1[DexFlags1Offset + index] & ~mask);
        _sb1[DexFlags2Offset + index] = (byte)((state & 2) != 0 ? _sb1[DexFlags2Offset + index] | mask : _sb1[DexFlags2Offset + index] & ~mask);
    }

    public IReadOnlyList<int> GetMissingSpecies()
    {
        var owned = new HashSet<int>();
        for (var box = 0; box < Boxes; box++)
            for (var slot = 0; slot < SlotsPerBox; slot++)
                if (TryMon(box, slot) is { LooksValid: true } mon) owned.Add(RogueData.NationalIdOf(mon.Species));
        return [.. RogueData.BaseSpeciesIds.Select(RogueData.NationalIdOf).Distinct().Where(n => !owned.Contains(n)).OrderBy(n => n)];
    }

    public DexEntryState GetDexEntry(int species)
    {
        var state = DexState(DexSpecies(species));
        return new(state >= 1, state >= 2);
    }

    public void SetDexEntry(int species, bool seen, bool caught)
    {
        var id = DexSpecies(species);
        if (id <= 0) return;
        var current = DexState(id);
        var next = caught ? (current == 3 ? 3 : 2) : seen ? 1 : 0;
        if (next == current) return;
        SetDexState(id, next);
        CommitSb1();
    }

    public bool IsDexSpecies(int species) => DexSpecies(species) > 0 && RogueData.NationalIdOf(DexSpecies(species)) == species;

    public DexProgress GetDexProgress()
    {
        int seen = 0, caught = 0;
        foreach (var id in RogueData.BaseSpeciesIds)
        {
            var state = DexState(id);
            if (state >= 1) seen++;
            if (state >= 2) caught++;
        }
        return new(seen, caught, RogueData.BaseSpeciesIds.Count);
    }

    public void CompleteDex()
    {
        foreach (var id in RogueData.BaseSpeciesIds)
            SetDexState(id, DexState(id) == 3 ? 3 : 2);
        CommitSb1();
    }

    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];

    // Emerald Rogue's custom maps and encounter tables are not in the pinned Core's
    // database, so vanilla Emerald encounters would be misinformation.
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        throw NotYet("Emerald Rogue encounter cards");

    public bool SupportsBoxTools => true;

    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var targets = (boxes ?? Enumerable.Range(0, Boxes).ToList()).Where(b => (uint)b < Boxes).Distinct().OrderBy(b => b).ToList();
        var mons = new List<byte[]>();
        foreach (var box in targets)
            for (var slot = 0; slot < SlotsPerBox; slot++)
                if (TryMon(box, slot) is { LooksValid: true } mon)
                    mons.Add(_storage.AsSpan(BoxMonOffset(box, slot), RogueMon.BoxSize).ToArray());

        RogueMon View(byte[] raw) => new(raw, 0, party: false);
        int Dex(byte[] raw) => RogueData.NationalIdOf(View(raw).Species);
        int Form(byte[] raw) => RogueData.FormOf(View(raw).Species);
        string Name(byte[] raw) => RogueData.SpeciesName(View(raw).Species);
        int Type(byte[] raw) => RogueData.TypesOf(View(raw).Species) is { Count: > 0 } t ? t[0] : 99;
        var ordered = criteria switch
        {
            SortCriteria.Alphabetical => mons.OrderBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(Dex).ToList(),
            SortCriteria.LevelDesc => mons.OrderByDescending(raw => View(raw).Level).ThenBy(Dex).ToList(),
            SortCriteria.IvTotalDesc => mons.OrderByDescending(raw => View(raw).IVs.Sum()).ThenBy(Dex).ToList(),
            SortCriteria.Type => mons.OrderBy(Type).ThenBy(Dex).ThenBy(Form).ToList(),
            SortCriteria.ShinyFirst => mons.OrderByDescending(raw => View(raw).IsShiny ? 1 : 0).ThenBy(Dex).ThenBy(Form).ToList(),
            SortCriteria.AgeOldest => mons, // Gen 3 mons carry no met date: keep storage order
            _ => mons.OrderBy(Dex).ThenBy(Form).ToList(),
        };
        if (reverse) ordered.Reverse();

        var placed = 0;
        foreach (var box in targets)
            for (var slot = 0; slot < SlotsPerBox; slot++)
            {
                var target = _storage.AsSpan(BoxMonOffset(box, slot), RogueMon.BoxSize);
                if (placed < ordered.Count) ordered[placed++].CopyTo(target);
                else target.Clear();
            }
        CommitStorage();
        return ordered.Count;
    }

    public int PlaceLivingDex(byte[] compressedBundle) =>
        throw NotYet("The Emerald Rogue living dex (its species table needs Emerald Rogue-legal templates)");

    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var touched = 0;
        foreach (var box in boxes ?? Enumerable.Range(-1, Boxes + 1).ToList())
        {
            if (box != -1 && (uint)box >= Boxes) continue;
            for (var slot = 0; slot < (box == -1 ? 6 : SlotsPerBox); slot++)
                if (ApplyBatchToSlot(box, slot, instructions)) touched++;
        }
        return touched;
    }

    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var touched = 0;
        foreach (var (box, slot) in slots)
            if (ApplyBatchToSlot(box, slot, instructions)) touched++;
        return touched;
    }

    /// <summary>PKHeX-style ".Prop=Value" lines against one mon; false when empty or nothing changed.</summary>
    private bool ApplyBatchToSlot(int box, int slot, IReadOnlyList<string> instructions)
    {
        if (TryMon(box, slot) is not { LooksValid: true } mon) return false;
        var before = mon.Buffer.AsSpan(mon.Offset, mon.Size).ToArray();
        var rnd = Random.Shared;
        foreach (var raw in instructions)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            var prop = (eq > 0 ? line[..eq] : line).Trim().TrimStart('.').ToLowerInvariant();
            var value = eq > 0 ? line[(eq + 1)..].Trim() : "1";
            int Number() => value == "$rand" ? rnd.Next(0, 32) : value == "$shiny" ? 1 : int.TryParse(value, out var n) ? n : 0;
            switch (prop)
            {
                case "level" or "lv":
                {
                    var level = Math.Clamp(Number(), 1, 100);
                    mon.Experience = RogueData.ExperienceAtLevel(level);
                    break;
                }
                case "nature": mon.Nature = Math.Clamp(Number(), 0, 24); break;
                case "friendship": mon.Friendship = Math.Clamp(Number(), 0, 255); break;
                case "ball" when RogueData.TryStoreBall(Number(), out var romBall): mon.Ball = romBall; break;
                case "helditem" or "item": mon.HeldItem = Math.Clamp(Number(), 0, ushort.MaxValue); break;
                case "move1" or "move2" or "move3" or "move4":
                {
                    var index = prop[^1] - '1';
                    var national = Number();
                    var rom = national == 0 ? 0 : RogueData.MoveFromNational(national);
                    if (national != 0 && rom == 0) break;
                    var moves = mon.Moves; var pp = mon.MovePp;
                    moves[index] = rom; pp[index] = RogueData.MoveBasePp(rom);
                    mon.Moves = moves; mon.MovePp = pp;
                    break;
                }
                case "iv_hp" or "iv_atk" or "iv_def" or "iv_spa" or "iv_spd" or "iv_spe":
                {
                    var ivs = mon.IVs;
                    ivs[StatIndex(prop)] = Math.Clamp(Number(), 0, 31);
                    mon.IVs = ivs;
                    break;
                }
                case "ev_hp" or "ev_atk" or "ev_def" or "ev_spa" or "ev_spd" or "ev_spe":
                {
                    var evs = mon.EVs;
                    evs[StatIndex(prop)] = Math.Clamp(Number(), 0, 255);
                    mon.EVs = evs;
                    break;
                }
                case "shiny":
                    mon.IsShiny = value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || Number() == 1;
                    break;
                case "nickname" when value.Length > 0: mon.Nickname = value; break;
                case "ot" or "trainer" when value.Length > 0: mon.SetOriginalTrainerName(value); break;
                case "heal" or "healpokemon" or "healpp":
                {
                    var moves = mon.Moves;
                    mon.MovePp = [.. moves.Select(RogueData.MoveBasePp)];
                    break;
                }
            }
        }
        if (mon.Buffer.AsSpan(mon.Offset, mon.Size).SequenceEqual(before)) return false;
        if (mon.Party)
        {
            var heal = instructions.Any(i => i.TrimStart('.').StartsWith("heal", StringComparison.OrdinalIgnoreCase));
            mon.SetPartyLevelAndStats(mon.Level, fullHeal: heal);
        }
        Commit(box);
        return true;
    }

    private static int StatIndex(string prop) => prop[^3..] switch
    {
        "_hp" => 0, "atk" => 1, "def" => 2, "spa" => 3, "spd" => 4, _ => 5,
    };

    public void SwapBoxes(int a, int b)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (a == b) return;
        if ((uint)a >= Boxes || (uint)b >= Boxes)
            throw new InvalidOperationException("Boxes 11-14 hold Emerald Rogue's run data and cannot move.");
        SwapBoxBytes(a, b);
        CommitStorage();
    }

    private void SwapBoxBytes(int a, int b)
    {
        var size = SlotsPerBox * RogueMon.BoxSize;
        var first = _storage.AsSpan(BoxMonOffset(a, 0), size).ToArray();
        _storage.AsSpan(BoxMonOffset(b, 0), size).CopyTo(_storage.AsSpan(BoxMonOffset(a, 0), size));
        first.CopyTo(_storage.AsSpan(BoxMonOffset(b, 0), size));
        var nameA = _storage.AsSpan(BoxNamesOffset + a * BoxNameLength, BoxNameLength).ToArray();
        _storage.AsSpan(BoxNamesOffset + b * BoxNameLength, BoxNameLength).CopyTo(_storage.AsSpan(BoxNamesOffset + a * BoxNameLength, BoxNameLength));
        nameA.CopyTo(_storage.AsSpan(BoxNamesOffset + b * BoxNameLength, BoxNameLength));
    }

    /// <summary>Merges the box into the first free slots of the others (anything left over is released), then closes the gap.</summary>
    public void DeleteBox(int box)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)box >= Boxes) return;
        for (var slot = 0; slot < SlotsPerBox; slot++)
        {
            if (TryMon(box, slot) is not { LooksValid: true }) continue;
            var raw = _storage.AsSpan(BoxMonOffset(box, slot), RogueMon.BoxSize).ToArray();
            var landed = false;
            for (var target = 0; target < Boxes && !landed; target++)
            {
                if (target == box) continue;
                for (var free = 0; free < SlotsPerBox; free++)
                {
                    if (TryMon(target, free) is { LooksValid: true }) continue;
                    raw.CopyTo(_storage.AsSpan(BoxMonOffset(target, free), RogueMon.BoxSize));
                    landed = true;
                    break;
                }
            }
            _storage.AsSpan(BoxMonOffset(box, slot), RogueMon.BoxSize).Clear();
        }
        for (var next = box; next < Boxes - 1; next++)
            SwapBoxBytes(next, next + 1);
        CommitStorage();
    }
    public IReadOnlyList<CountedEntry> GetPokeBeans() => [];
    public int SetPokeBeanCount(int index, int count) => 0;
    public IReadOnlyList<UndergroundItem> GetGrandUndergroundItems() => [];
    public int SetGrandUndergroundItemCount(int itemId, int count) => 0;
    public DaycareInfo GetDaycare() => new(false, []);
    public DaycareWithdrawal WithdrawDaycareToFirstEmptyBox(int facility, int slot) => throw NotYet("The Emerald Rogue Day Care");
    public bool SupportsLegalFashionUnlock => false;
    public void UnlockAllLegalFashion() { }
    public bool SupportsStylePoints => false;
    public int GetStylePoints() => 0;
    public void SetStylePoints(int value) { }
    public MysteryGiftInbox GetMysteryGiftInbox() => new(false, []);
    public TrainerRecordsInfo GetTrainerRecords() => new(false, []);
    private const int PlayTimeHoursMax = 999; // the game stops counting at 999:59
    public TrainerStats GetTrainerStats() => new(true,
        BinaryPrimitives.ReadUInt16LittleEndian(_sb2.AsSpan(14)), _sb2[16], PlayTimeHoursMax,
        false, 0, 0, false, 0, 0);

    public void SetTrainerStats(TrainerStatsEdit edit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (edit.PlayTimeHours is { } hours)
            BinaryPrimitives.WriteUInt16LittleEndian(_sb2.AsSpan(14), (ushort)Math.Clamp(hours, 0, PlayTimeHoursMax));
        if (edit.PlayTimeMinutes is { } minutes)
            _sb2[16] = (byte)Math.Clamp(minutes, 0, 59);
        CommitSb2();
    }
    public bool SupportsRTCRepair => false;
    public void RepairRTC() { }

    public MetInfo GetMetInfo(int box, int slot) => throw NotYet("Emerald Rogue met/origin editing");
    public void ApplyMetEdit(int box, int slot, MetEdit edit) => throw NotYet("Emerald Rogue met/origin editing");
    public IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg) => [];
    public IReadOnlyList<NamedChoice> GetVersionChoices() => [];
    public IReadOnlyList<NamedChoice> GetLanguageChoices(int box, int slot) => [];
    public IReadOnlyList<NamedChoice> GetTeraTypeChoices() => [];

    public MoveShopInfo GetMoveShop(int box, int slot) => new(false, []);
    public void ApplyMoveShopEdit(int box, int slot, MoveShopEdit edit) { }

    public CosmeticInfo GetCosmetics(int box, int slot) => new(
        [], [], false, 0, 0, false, 0, false, 0, 0, false, 0, 0, false, false,
        false, 0, false, false, false, false, 0);

    public void ApplyCosmeticEdit(int box, int slot, CosmeticEdit edit) { }
    public PokerusInfo GetPokerus(int box, int slot) => new(false, PokerusStatus.Susceptible, 0, 0);
    public void SetPokerus(int box, int slot, PokerusStatus status) { }
    public IReadOnlyList<RibbonEntry> GetRibbons(int box, int slot) => [];
    public void SetRibbon(int box, int slot, string id, int value) { }
    public AffixedRibbonInfo GetAffixedRibbon(int box, int slot) => new(false, -1, string.Empty, []);
    public void SetAffixedRibbon(int box, int slot, int ribbonIndex) { }
    public IReadOnlyDictionary<string, int> GetObtainableRibbonMaxima(int box, int slot) =>
        new Dictionary<string, int>();
    public int AwardAllObtainableRibbons(int box, int slot) => 0;
    public bool SupportsLegalityAnalysis => false;

    public bool SupportsCompassSettings => false;
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;

    public void Dispose() => _disposed = true;
    private static NotSupportedException NotYet(string what) =>
        new($"{what} is not available for Pokémon Emerald Rogue yet.");
}
