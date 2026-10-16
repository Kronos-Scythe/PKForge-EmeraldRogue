using System.Buffers.Binary;

namespace PKForge.Engine.Rogue;

/// <summary>
/// Pokémon Emerald Rogue (Pokabbie/pokeemerald-rogue, EX build) save envelope.
///
/// Ground truth: a real v2.2.1-EX save (tools/rogue/verify_save.py decodes the same bytes and
/// docs/rogue-support-notes.md lists every offset). The file is a GBA 128 KB flash image: 32 sectors
/// of 0x1000 whose footer holds the section id (u16 @0xFF4), checksum (u16 @0xFF6), the retail
/// signature 0x08012025 and a save counter (u32 @0xFFC). Unlike retail Emerald the game
/// checksums the FULL 4084-byte data window of every sector, so the vanilla window table cannot
/// verify the file. Sections 0 = SaveBlock2, 1-4 = SaveBlock1, 5-13 = PokemonStorage.
/// Two rotating copies exist; the highest counter is live. Sectors 28-31 are blank.
/// </summary>
internal static class RogueFormat
{
    public const int FileSize = 0x20_000;
    public const int SectorSize = 0x1000;
    public const int DataSize = 4084;
    public const int LiveSectors = 28;
    public const int SectionCount = 14;
    public const uint Signature = 0x0801_2025;

    /// <summary>Vanilla Emerald's per-section checksum windows. A Rogue save fails some of them.</summary>
    private static readonly int[] VanillaWindows =
        [3884, 3968, 3968, 3968, 3848, 3968, 3968, 3968, 3968, 3968, 3968, 3968, 3968, 2000];

    public static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint total = 0;
        for (var i = 0; i + 4 <= data.Length; i += 4)
            total += BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
        return (ushort)(total + (total >> 16));
    }

    /// <summary>Physical offset of the live (highest counter) copy of each section id, -1 when absent.</summary>
    public static int[] LiveSections(ReadOnlySpan<byte> data)
    {
        var offsets = new int[SectionCount];
        Array.Fill(offsets, -1);
        var counters = new uint[SectionCount];
        for (var sector = 0; sector < LiveSectors; sector++)
        {
            var off = sector * SectorSize;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 0xFF8)..]) != Signature) continue;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 0xFF4)..]);
            if (id >= SectionCount) continue;
            var counter = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 0xFFC)..]);
            if (offsets[id] < 0 || counter >= counters[id])
            {
                offsets[id] = off;
                counters[id] = counter;
            }
        }
        return offsets;
    }

    /// <summary>
    /// True for an Emerald Rogue save: all 14 live sections verify over the full 4084-byte window
    /// while retail Emerald's windows do not (a retail save verifies under both, since its unused
    /// tails are zero), and the Radical Red / CFRU window table does not fit either.
    /// </summary>
    public static bool IsEmeraldRogue(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileSize) return false;
        var live = LiveSections(data);
        var vanillaValid = 0;
        for (var id = 0; id < SectionCount; id++)
        {
            if (live[id] < 0) return false;
            var sector = data.Slice(live[id], SectorSize);
            var stored = BinaryPrimitives.ReadUInt16LittleEndian(sector[0xFF6..]);
            if (Checksum(sector[..DataSize]) != stored) return false;
            if (Checksum(sector[..(VanillaWindows[id] & ~3)]) == stored) vanillaValid++;
        }
        return vanillaValid < SectionCount && !RadicalRed.RadicalRedFormat.IsRadicalRed(data);
    }

    /// <summary>The concatenated data windows of <paramref name="count"/> consecutive section ids.</summary>
    public static byte[] ReadBlock(ReadOnlySpan<byte> data, int[] sections, int firstId, int count)
    {
        var block = new byte[count * DataSize];
        for (var i = 0; i < count; i++)
            data.Slice(sections[firstId + i], DataSize).CopyTo(block.AsSpan(i * DataSize));
        return block;
    }

    /// <summary>
    /// Writes a block back, touching only sections whose bytes changed (an untouched section keeps
    /// its exact bytes and checksum). Returns the number of sections rewritten.
    /// </summary>
    public static int WriteBlock(byte[] data, int[] sections, int firstId, ReadOnlySpan<byte> block)
    {
        var rewritten = 0;
        for (var i = 0; i * DataSize < block.Length; i++)
        {
            var window = block.Slice(i * DataSize, DataSize);
            var target = data.AsSpan(sections[firstId + i], DataSize);
            if (window.SequenceEqual(target)) continue;
            window.CopyTo(target);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(sections[firstId + i] + 0xFF6), Checksum(target));
            rewritten++;
        }
        return rewritten;
    }
}
