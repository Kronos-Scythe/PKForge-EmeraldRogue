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
}
