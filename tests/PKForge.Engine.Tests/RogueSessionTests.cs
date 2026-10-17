using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.Rogue;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Pokémon Emerald Rogue (EX v2.2.1): detection, the 4084-byte-window envelope, the expansion
/// substruct layouts, edits that round-trip, and the locked run-data boxes. The real save is
/// gitignored (.local-testdata/romhacks); every test returns early when it is absent.
/// </summary>
public sealed class RogueSessionTests
{
    private static readonly SaveEngine Engine = new();

    private static byte[]? Sample()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null
            : Path.Combine(directory.FullName, ".local-testdata", "romhacks", "Emerald_Rogue_v2.2.1-EX.sav");
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    [Fact]
    public void Sample_is_detected_and_described()
    {
        if (Sample() is not { } bytes) return;
        Assert.True(RogueFormat.IsEmeraldRogue(bytes.AsSpan(0, RogueFormat.FileSize)));
        Assert.True(Engine.Validate(bytes));
        var description = Engine.TryDescribe(bytes);
        Assert.NotNull(description);
        Assert.Equal("Emerald Rogue", description!.GameName);
    }

    [Fact]
    public void Standard_route_refuses_the_sample()
    {
        if (Sample() is not { } bytes) return;
        Assert.Throws<InvalidDataException>(() => Engine.OpenSession(bytes, "Rogue", SaveFormat.Standard));
    }

    [Fact]
    public void Sample_reads_party_and_boxes()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        Assert.Equal(9, session.Snapshot.Slots.Max(slot => slot.Box)); // boxes 11-14 hold run data and stay hidden

        var first = session.ReadEntity(-1, 0);
        Assert.False(first.IsEmpty);
        Assert.Equal(987, first.Species); // Flutter Mane
        Assert.Equal(36, first.Level);
        Assert.Equal(17, first.Nature);

        var second = session.ReadEntity(-1, 1);
        Assert.Equal(212, second.Species); // Scizor
        Assert.Equal(25, second.Level);

        Assert.True(session.ReadEntity(-1, 2).IsEmpty);
        Assert.Equal(778, session.ReadEntity(0, 0).Species); // Mimikyu
        Assert.Equal(10, Enumerable.Range(0, 30).Count(slot => !session.ReadEntity(0, slot).IsEmpty));
    }

    [Fact]
    public void Untouched_edit_and_serialize_keep_every_byte()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        var detail = session.ReadEntity(-1, 1);
        session.ApplyEdit(-1, 1, new EntityEdit(Species: detail.Species, Level: detail.Level, Nature: detail.Nature));
        Assert.True(bytes.AsSpan().SequenceEqual(session.Serialize().Span));
    }

    [Fact]
    public void Edits_round_trip_and_leave_the_run_data_alone()
    {
        if (Sample() is not { } bytes) return;
        byte[] edited;
        using (var session = Engine.OpenSession(bytes, "Rogue"))
        {
            session.ApplyEdit(-1, 1, new EntityEdit(Nickname: "Zed", Level: 40, IVs: [31, 31, 31, 31, 31, 31]));
            session.ApplyEdit(0, 0, new EntityEdit(IsShiny: true));
            edited = session.Serialize().ToArray();
        }

        Assert.True(Engine.Validate(edited));
        using var reopened = Engine.OpenSession(edited, "Rogue");
        var scizor = reopened.ReadEntity(-1, 1);
        Assert.Equal("Zed", scizor.Nickname);
        Assert.Equal(40, scizor.Level);
        Assert.All(scizor.IVs, iv => Assert.Equal(31, iv));
        Assert.True(reopened.ReadEntity(0, 0).IsShiny);

        // Only the sections that hold the party (SaveBlock1) and the first box may differ:
        // PokemonStorage sections after the first box, which carry Rogue's run data, must not.
        var live = RogueFormat.LiveSections(bytes);
        for (var id = 6; id <= 13; id++)
        {
            var window = bytes.AsSpan(live[id], RogueFormat.SectorSize);
            Assert.True(window.SequenceEqual(edited.AsSpan(live[id], RogueFormat.SectorSize)), $"section {id} changed");
        }
    }

    [Fact]
    public void Moving_a_party_mon_into_a_box_keeps_its_data()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        session.MoveSlot(-1, 1, 1, 0);
        Assert.True(session.ReadEntity(-1, 1).IsEmpty);
        var moved = session.ReadEntity(1, 0);
        Assert.Equal(212, moved.Species);
        Assert.Equal(25, moved.Level);
    }

    [Fact]
    public void Bag_and_money_round_trip()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        var trainer = session.GetTrainer();
        Assert.Equal(33550u, trainer.Money);
        var before = session.GetBag().SelectMany(pouch => pouch.Items).ToList();
        Assert.Contains(before, item => item.Id == 39 && item.Count == 5);
        session.SetItemCount("Items", 39, 12);
        Assert.Contains(session.GetBag().SelectMany(pouch => pouch.Items), item => item.Id == 39 && item.Count == 12);
    }

    [Fact]
    public void Tables_bridge_rogue_ids_to_national_ids()
    {
        Assert.Equal(987, RogueData.NationalIdOf(1379));
        Assert.Equal("Scizor", RogueData.SpeciesName(212));
        Assert.Equal(1379, RogueData.SpeciesFromNational(987));
        Assert.Equal(300u, RogueData.ExperienceAtLevel(1));
        Assert.Equal(36, RogueData.LevelForExperience(10800));
    }

    [Fact]
    public void Held_item_edit_uses_picker_ids_and_is_write_safe()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        var held = session.GetItemNames().ToList().FindIndex(name => name == "Leftovers");
        Assert.True(held > 0);
        session.ApplyEdit(-1, 0, new EntityEdit(HeldItem: held));
        Assert.Equal(held, session.ReadEntity(-1, 0).HeldItem);
        var candidate = session.Serialize();
        Assert.True(Engine.CheckWriteSafety(bytes, candidate, WriteScope.Only(new SlotRef(-1, 0))) is null);
    }

    [Fact]
    public void Build_parts_are_listed_and_editable_in_key_items()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        var names = session.GetItemNames();
        Assert.Equal("Build Parts", names[834]);
        var key = session.GetBag().Single(p => p.Name == "Key Items");
        Assert.Contains(key.Items, i => i.Id == 834 && i.Count == 9);
        Assert.Contains(834, session.GetPouchLegalItems("Key Items"));
        Assert.Equal(250, session.SetItemCount("Key Items", 834, 250));
        var again = session.GetBag().Single(p => p.Name == "Key Items");
        Assert.Contains(again.Items, i => i.Id == 834 && i.Count == 250);
    }

    [Fact]
    public void Pokedex_reads_edits_and_completes_on_the_real_save()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        var before = session.GetDexProgress();
        Assert.True(before.Total > 100);
        Assert.True(before.Seen > 0 && before.Caught <= before.Seen);
        var target = Enumerable.Range(1, 900).First(n => session.IsDexSpecies(n) && !session.GetDexEntry(n).Caught);
        session.SetDexEntry(target, seen: true, caught: true);
        Assert.True(session.GetDexEntry(target).Caught);
        session.SetDexEntry(target, seen: false, caught: false);
        Assert.False(session.GetDexEntry(target).Seen);
        session.CompleteDex();
        var after = session.GetDexProgress();
        Assert.Equal(after.Total, after.Caught);
    }

    [Fact]
    public void Smogon_usage_stats_become_a_most_used_set()
    {
        const string json = """
            {"battles":1,"pokemon":{"Great Tusk":{"abilities":{"Protosynthesis":1},
            "items":{"Rocky Helmet":0.26,"Heavy-Duty Boots":0.25},
            "teraTypes":{"Steel":0.29,"Fighting":0.11},
            "spreads":{"Jolly:0/252/4/0/0/252":0.2,"Adamant:0/252/0/0/4/252":0.1},
            "moves":{"Rapid Spin":0.91,"Headlong Rush":0.88,"Ice Spinner":0.77,"Stealth Rock":0.43,"Knock Off":0.2}}}}
            """;
        var set = SmogonSets.FromUsage(json, "great tusk", "gen9ou");
        Assert.NotNull(set);
        Assert.Contains("Great Tusk @ Rocky Helmet", set.Showdown);
        Assert.Contains("EVs: 252 Atk / 4 Def / 252 Spe", set.Showdown);
        Assert.Contains("Jolly Nature", set.Showdown);
        Assert.Contains("- Stealth Rock", set.Showdown);
        Assert.DoesNotContain("Knock Off", set.Showdown);
        Assert.Null(SmogonSets.FromUsage(json, "Garchomp", "gen9ou"));
    }

    private sealed class FakeFolder(Dictionary<string, byte[]> files) : PKForge.Domain.IFolderFileAccess
    {
        public ValueTask<IReadOnlyList<PKForge.Domain.PickedDocument>> ListFilesAsync(string treeId, CancellationToken cancellationToken = default) =>
            new((IReadOnlyList<PKForge.Domain.PickedDocument>)[.. files.Keys.Select(k => new PKForge.Domain.PickedDocument(k, Path.GetFileName(k)))]);
        public ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(string documentId, CancellationToken cancellationToken = default) => new(files[documentId]);
        public ValueTask WriteFileAsync(string treeId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Folder_import_reads_nested_rogue_files_and_skips_duplicates()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        var one = session.ExportSlot(-1, 0).Data;
        var two = session.ExportSlot(-1, 1).Data;
        var folder = new FakeFolder(new()
        {
            ["content://t/a/Mon1.pk3ex"] = one,
            ["content://t/a/deep/er/Mon2.pk3ex"] = two,
            ["content://t/a/copy/Mon1b.pk3ex"] = one,
            ["content://t/a/notes.txt"] = "hello"u8.ToArray(),
        });
        var directory = Path.Combine(Path.GetTempPath(), "pkforge-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bank = new PKForge.Infrastructure.FileBankService(directory);
            var result = await PKForge.Infrastructure.BankArchive.ImportAsync(
                bank, (d, n) => Engine.TryDescribeEntity(d, n, n), folder, "tree");
            Assert.Equal(2, result.Imported);
            Assert.Equal(1, result.SkippedDuplicates);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Hub_buildings_and_upgrades_read_and_edit_on_the_real_save()
    {
        if (Sample() is not { } bytes) return;
        using var session = (PKForge.Engine.Rogue.RogueEngineSession)Engine.OpenSession(bytes, "Rogue");
        var parts = session.GetHubParts()!;
        Assert.NotNull(parts);
        Assert.Equal(11, parts.Count(p => p.IsArea));
        Assert.Equal(48, parts.Count(p => !p.IsArea));
        var berry = parts.Single(p => p.IsArea && p.Name == "Berry Fields");
        Assert.Equal(10, berry.Cost);
        Assert.False(berry.Built);
        Assert.True(session.SetHubArea(berry.Id, true).Success);
        Assert.True(session.GetHubParts()!.Single(p => p.IsArea && p.Id == berry.Id).Built);
        Assert.True(session.SetHubUpgrade(16, true).Success);
        Assert.True(session.GetHubParts()!.Single(p => !p.IsArea && p.Id == 16).Built);
        var reopened = (PKForge.Engine.Rogue.RogueEngineSession)Engine.OpenSession(session.Serialize(), "Rogue");
        Assert.True(reopened.GetHubParts()!.Single(p => p.IsArea && p.Id == berry.Id).Built);
        Assert.True(reopened.SetHubArea(berry.Id, false).Success);
        Assert.False(reopened.GetHubParts()!.Single(p => p.IsArea && p.Id == berry.Id).Built);
    }

    [Fact]
    public void Pokemon_from_another_game_import_into_rogue_through_their_set()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        var pk = new PKHeX.Core.PK9 { Species = 998, CurrentLevel = 50, Nature = PKHeX.Core.Nature.Adamant, Move1 = 14 };
        pk.PID = 0x12345678;
        var slot = Enumerable.Range(0, 30).First(i => session.ReadEntity(9, i).IsEmpty);
        Assert.True(session.ImportSlot(9, slot, pk.Data.ToArray(), "998 - Baxcalibur.pk9"));
        var landed = session.ReadEntity(9, slot);
        Assert.False(landed.IsEmpty);
        Assert.Equal(998, landed.Species);
        Assert.Equal(50, landed.Level);
    }

    [Fact]
    public void Box_tools_sort_swap_delete_and_batch()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        Assert.True(session.SupportsBoxTools);
        int Count(ISaveEngineSession s) => Enumerable.Range(0, 10).Sum(b => Enumerable.Range(0, 30).Count(sl => !s.ReadEntity(b, sl).IsEmpty));
        var before = Count(session);

        var sorted = session.SortBoxes(SortCriteria.DexNumber);
        Assert.Equal(before, sorted);
        Assert.Equal(before, Count(session));
        var dex = Enumerable.Range(0, 10).SelectMany(b => Enumerable.Range(0, 30).Select(sl => session.ReadEntity(b, sl)))
            .Where(d => !d.IsEmpty).Select(d => d.Species).ToList();
        Assert.True(dex.Count == before);

        var firstBefore = session.ReadEntity(0, 0).Species;
        session.SwapBoxes(0, 1);
        session.SwapBoxes(0, 1);
        Assert.Equal(firstBefore, session.ReadEntity(0, 0).Species);

        var touched = session.BatchApply([".Level=77", ".Friendship=200"], [0]);
        Assert.Equal(sorted == 0 ? 0 : touched, touched);
        if (touched > 0) Assert.Equal(77, session.ReadEntity(0, 0).Level);

        session.DeleteBox(0);
        Assert.Equal(before, Count(session));

        var candidate = session.Serialize();
        Assert.True(Engine.Validate(candidate));
        using var reopened = Engine.OpenSession(candidate.ToArray(), "Rogue");
        Assert.Equal(before, Count(reopened));
    }

    [Fact]
    public void Bank_export_import_and_qr_envelope_round_trip()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        var export = session.ExportSlot(-1, 0);
        Assert.Equal("PK3EX", export.Format!);
        Assert.Equal(80, export.Data.Length);

        var info = Engine.TryDescribeEntity(export.Data, "test", export.Format);
        Assert.NotNull(info);
        Assert.Equal(987, info!.Species); // Flutter Mane

        var payload = QrEntityCodec.MakePayload(export.Data, QrEntityCodec.RogueGeneration, "Flutter Mane");
        var parsed = QrEntityCodec.TryParse(payload);
        Assert.NotNull(parsed);
        Assert.Equal(QrEntityCodec.RogueGeneration, parsed!.Generation);

        // Land it in the first empty box slot and read it back.
        var slot = Enumerable.Range(0, 30).First(sl => session.ReadEntity(9, sl).IsEmpty);
        Assert.True(session.ImportSlot(9, slot, parsed.EntityBytes, "PK3EX"));
        Assert.Equal(987, session.ReadEntity(9, slot).Species);
        Assert.False(session.ImportSlot(9, slot, new byte[58], "PK3EX"));
        Assert.False(session.ImportSlot(9, slot, parsed.EntityBytes, "PK3"));
        Assert.True(Engine.Validate(session.Serialize()));
    }

    [Fact]
    public void Event_flags_and_vars_read_and_write()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        Assert.True(EventFlagService.IsSupported(session));
        var catalog = EventFlagService.Load(session);
        Assert.Equal(2400, catalog.Flags.Count);
        Assert.Equal(256, catalog.Work.Count);
        var runActive = catalog.Flags.First(f => f.Label == "Rogue Run Active");
        Assert.True(runActive.IsOn); // the sample was saved mid-run: cross-checks the flag offset

        Assert.True(EventFlagService.SetFlag(session, EventFlagBank.Event, 2000, true).Success);
        Assert.True(EventFlagService.SetWork(session, 5, 1234).Success);
        Assert.False(EventFlagService.SetWork(session, 5, 70000).Success);
        var after = EventFlagService.Load(session);
        Assert.True(after.Flags[2000].IsOn && after.Flags[2000].Changed);
        Assert.Equal(1234, (int)after.Work[5].Value);
        Assert.True(Engine.Validate(session.Serialize()));

        EventFlagService.SetFlag(session, EventFlagBank.Event, 2000, false);
        EventFlagService.SetWork(session, 5, catalog.Work[5].Original);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
    }

    [Fact]
    public void Zip_import_finds_rogue_files_in_nested_folders_and_skips_duplicates()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        var export = session.ExportSlot(-1, 1);
        using var zipStream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "team/box1/Scizor.pk3ex", "readme.txt" })
            {
                var entry = zip.CreateEntry(name);
                using var writer = entry.Open();
                writer.Write(name.EndsWith(".txt") ? "hello"u8 : export.Data);
            }
        }
        var directory = Path.Combine(Path.GetTempPath(), "pkforge-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bank = new PKForge.Infrastructure.FileBankService(directory);
            (BankEntryInfo? Info, bool _) Describe(byte[] data, string name) => (Engine.TryDescribeEntity(data, name, name), true);
            var first = PKForge.Infrastructure.BankArchive.ImportZip(bank, (d, n) => Describe(d, n).Info, zipStream.ToArray());
            Assert.Equal(1, first.Imported);
            var second = PKForge.Infrastructure.BankArchive.ImportZip(bank, (d, n) => Describe(d, n).Info, zipStream.ToArray());
            Assert.Equal(0, second.Imported);
            Assert.Equal(1, second.SkippedDuplicates);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Smogon_set_json_becomes_a_rogue_mon_with_item_ability_and_evs()
    {
        if (Sample() is not { } bytes) return;
        const string json = """
        {"Garchomp":{"ou":{"TankChomp":{"moves":["Earthquake","Dragon Tail","Stealth Rock","Spikes"],"ability":"Rough Skin","item":"Rocky Helmet","nature":"Impish","evs":{"hp":252,"def":216,"spe":40},"teratypes":["Ghost","Steel"]},
            "Swords Dance":{"moves":["Swords Dance","Scale Shot","Earthquake",["Fire Fang","Dragon Tail"]],"ability":"Rough Skin","item":"Loaded Dice","nature":"Jolly","evs":{"atk":252,"spd":4,"spe":252}}},
          "doublesou":{"Doubles":{"moves":["Protect","Dragon Claw"],"ability":["Rough Skin"],"item":"Life Orb","nature":"Adamant","evs":{"atk":252,"spe":252,"hp":4}}},
          "monotype":{"Swords Dance (Ground)":{"moves":["Swords Dance"],"item":"Life Orb"}}},
         "Pikachu":{"lc":{"Lead":{"moves":["Thunderbolt"]}}}}
        """;
        var all = SmogonSets.Find(json, "garchomp");
        Assert.Equal(4, all.Count);
        Assert.Equal("ou", all[0].Format); // mainstream ladders sort first
        Assert.Single(SmogonSets.Find(json, "Garchomp", doubles: true));
        Assert.Equal(3, SmogonSets.Find(json, "Garchomp", doubles: false).Count);
        Assert.Empty(SmogonSets.Find(json, "Pikachu"));
        Assert.Equal("Doubles OU", SmogonSets.FormatLabel("doublesou"));
        Assert.Equal("VGC 2025", SmogonSets.FormatLabel("vgc2025"));
        var singles = SmogonSets.Find(json, "Garchomp", doubles: false).First(set => set.Name == "Swords Dance");
        Assert.True(singles.Showdown.Contains("Garchomp @ Loaded Dice") && singles.Showdown.Contains("EVs: 252 Atk / 4 SpD / 252 Spe"));

        using var session = Engine.OpenSession(bytes, "Rogue");
        var slot = Enumerable.Range(0, 30).First(sl => session.ReadEntity(9, sl).IsEmpty);
        var outcome = ((ISaveEngineSession)session).GetType() == session.GetType()
            ? new LegalizerService().GenerateFromShowdown(session, 9, slot, singles.Showdown)
            : throw new Exception("unreachable");
        Assert.True(outcome.Success);
        var mon = session.ReadEntity(9, slot);
        Assert.Equal(445, mon.Species);
        Assert.Equal(252, mon.EVs[1]);
        Assert.Equal(252, mon.EVs[5]);
        Assert.True(mon.HeldItem > 0 && session.GetItemNames()[mon.HeldItem] == "Loaded Dice");
        Assert.True(new[] { mon.Move1, mon.Move2, mon.Move3, mon.Move4 }.Contains(14)); // Swords Dance
    }

    [Fact]
    public void Profile_edits_gender_and_play_time_round_trip()
    {
        if (Sample() is not { } bytes) return;
        using var session = Engine.OpenSession(bytes, "Rogue");
        var trainer = session.GetTrainer();
        var stats = session.GetTrainerStats();
        Assert.True(stats.SupportsPlayTime);
        session.SetTrainer(trainer with { Gender = trainer.Gender == 1 ? 0 : 1, TID = 4321 });
        session.SetTrainerStats(new TrainerStatsEdit(PlayTimeHours: 123, PlayTimeMinutes: 45));
        var candidate = session.Serialize().ToArray();
        Assert.True(Engine.Validate(candidate));
        using var reopened = Engine.OpenSession(candidate, "Rogue");
        Assert.Equal(trainer.Gender == 1 ? 0 : 1, reopened.GetTrainer().Gender);
        Assert.Equal(4321, reopened.GetTrainer().TID);
        Assert.Equal(123, reopened.GetTrainerStats().PlayTimeHours);
        Assert.Equal(45, reopened.GetTrainerStats().PlayTimeMinutes);
    }
}
