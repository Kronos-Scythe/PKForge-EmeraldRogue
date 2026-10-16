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
        _displayName = displayName;
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
            RogueData.ItemToNational(mon.HeldItem),
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
        if (edit.HeldItem is { } item && item != RogueData.ItemToNational(mon.HeldItem))
        {
            var romItem = item == 0 ? 0 : RogueData.ItemFromNational(item);
            if (item == 0 || romItem > 0)
                mon.HeldItem = romItem;
        }
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

    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null) => false;

    public SlotExport ExportSlot(int box, int slot) =>
        throw NotYet("Exporting a Pokémon to the Bank");

    // ── generation (the wizard) ──
    internal GenerationOutcome GenerateInto(int box, int slot, GenerationRequest request) =>
        Generate(box, slot, request.Species, request.Level, request.Shiny, request.Nature, request.Ball, [.. request.Moves ?? []]);

    internal GenerationOutcome GenerateFromShowdownText(int box, int slot, string text)
    {
        var set = new ShowdownSet(text);
        if (set.Species == 0)
            return new GenerationOutcome(false, "Could not read the set (no species).");
        int? nature = set.Nature is Nature.Random ? null : (int)set.Nature;
        return Generate(box, slot, set.Species, set.Level, set.Shiny, nature, null, [.. set.Moves.Select(move => (int)move)]);
    }

    private GenerationOutcome Generate(int box, int slot, int nationalSpecies, int? level, bool shiny, int? nature, int? ball, int[] nationalMoves)
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
        mon.HeldItem == 0 ? 0 : RogueData.ItemToNational(mon.HeldItem) is > 0 and var national ? national : -1;

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

    public IReadOnlyList<string> GetItemNames()
    {
        var names = new string[RogueData.ItemCount];
        for (var id = 0; id < names.Length; id++)
            names[id] = RogueData.ItemName(id);
        return names;
    }

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
            BinaryPrimitives.ReadUInt16LittleEndian(_sb2.AsSpan(12)), money, 0);
    }

    public void SetTrainer(TrainerInfo trainer)
    {
        var key = BinaryPrimitives.ReadUInt32LittleEndian(_sb2.AsSpan(EncryptionKeyOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(_sb1.AsSpan(MoneyOffset), Math.Min(trainer.Money, 999_999u) ^ key);
        BinaryPrimitives.WriteUInt16LittleEndian(_sb2.AsSpan(10), (ushort)trainer.TID);
        BinaryPrimitives.WriteUInt16LittleEndian(_sb2.AsSpan(12), (ushort)trainer.SID);
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
    public IReadOnlyList<int> GetMissingSpecies() => [];
    public DexEntryState GetDexEntry(int species) => new(false, false);
    public void SetDexEntry(int species, bool seen, bool caught) { }
    public bool IsDexSpecies(int species) => species >= 1;
    public DexProgress GetDexProgress() => new(0, 0, 1);
    public void CompleteDex() => throw NotYet("The Emerald Rogue Pokédex editor");
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];

    // Emerald Rogue's custom maps and encounter tables are not in the pinned Core's
    // database, so vanilla Emerald encounters would be misinformation.
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        throw NotYet("Emerald Rogue encounter cards");

    public bool SupportsBoxTools => false;
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) =>
        throw NotYet("Emerald Rogue box sorting");

    public int PlaceLivingDex(byte[] compressedBundle) =>
        throw NotYet("The Emerald Rogue living dex (its species table needs Emerald Rogue-legal templates)");

    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) =>
        throw NotYet("The Emerald Rogue batch editor");

    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) =>
        throw NotYet("The Emerald Rogue batch editor");
    public void SwapBoxes(int a, int b) => throw NotYet("Emerald Rogue box swapping");
    public void DeleteBox(int box) => throw NotYet("Emerald Rogue box management");
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
    public TrainerStats GetTrainerStats() => new(false, 0, 0, 0, false, 0, 0, false, 0, 0);
    public void SetTrainerStats(TrainerStatsEdit edit) => throw NotYet("Emerald Rogue trainer statistics");
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
