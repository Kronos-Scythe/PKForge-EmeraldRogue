using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.Rogue;

/// <summary>
/// One Pokémon in Emerald Rogue's storage: the 80-byte BoxPokemon or the 104-byte party Pokemon.
/// The 48 secure bytes are standard Gen 3 data (XOR PID^OTID, four 12-byte substructs ordered by
/// PID % 24) but the substructs follow pokeemerald-expansion's bit layouts. The mon is decrypted into
/// a working copy on construction; every setter updates it and re-encrypts into the backing
/// buffer, so the file never holds a half-written mon and unknown bytes are never touched.
/// Layouts: tools/rogue/verify_save.py and docs/rogue-support-notes.md.
/// </summary>
internal sealed class RogueMon
{
    public const int BoxSize = 80;
    public const int PartySize = 104;
    public const int MaxSpeciesId = 1574;

    private static readonly string[] Orders =
    [
        "GAEM", "GAME", "GEAM", "GEMA", "GMAE", "GMEA", "AGEM", "AGME", "AEGM", "AEMG", "AMGE", "AMEG",
        "EGAM", "EGMA", "EAGM", "EAMG", "EMGA", "EMAG", "MGAE", "MGEA", "MAGE", "MAEG", "MEGA", "MEAG",
    ];

    public readonly byte[] Buffer;
    public readonly int Offset;
    public readonly bool Party;
    private readonly byte[] _plain = new byte[48];
    private readonly bool _blank;

    public RogueMon(byte[] buffer, int offset, bool party)
    {
        Buffer = buffer;
        Offset = offset;
        Party = party;
        _blank = Pid == 0 && Otid == 0;
        if (_blank) return;
        var key = Pid ^ Otid;
        for (var i = 0; i < 48; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(_plain.AsSpan(i), BinaryPrimitives.ReadUInt32LittleEndian(Raw[(0x20 + i)..]) ^ key);
    }

    /// <summary>Zeroes the decrypted substructs (a freshly written header decrypts to key noise).</summary>
    public void Reset()
    {
        Array.Clear(_plain);
        Flush();
    }

    public int Size => Party ? PartySize : BoxSize;
    private Span<byte> Raw => Buffer.AsSpan(Offset, Size);

    // ── header ──
    public uint Pid
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Raw);
        set => BinaryPrimitives.WriteUInt32LittleEndian(Raw, value);
    }

    public uint Otid
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Raw[4..]);
        set { BinaryPrimitives.WriteUInt32LittleEndian(Raw[4..], value); Flush(); }
    }

    public string Nickname
    {
        get => StringConverter3.GetString(Raw[8..18], jp: false);
        set
        {
            var span = Raw[8..18];
            span.Fill(0xFF);
            StringConverter3.SetString(span, value, 10, jp: false);
        }
    }

    public string OriginalTrainerName => StringConverter3.GetString(Raw[0x14..0x1B], jp: false);

    public void SetOriginalTrainerName(string value)
    {
        var span = Raw[0x14..0x1B];
        span.Fill(0xFF);
        StringConverter3.SetString(span, value, 7, jp: false);
    }

    public int Language => Raw[0x12] & 7;

    /// <summary>Nature is stored: PID % 25 XOR the 5-bit hiddenNatureModifier at 0x12 bits 3-7.</summary>
    public int Nature
    {
        get => (int)((Pid % 25) ^ (uint)(Raw[0x12] >> 3)) % 25;
        set => Raw[0x12] = (byte)((Raw[0x12] & 7) | ((int)((Pid % 25) ^ (uint)Math.Clamp(value, 0, 24)) << 3));
    }

    // ── substruct access ──
    private int Sub(char type) => Orders[Pid % 24].IndexOf(type) * 12;

    private uint G0
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_plain.AsSpan(Sub('G')));
        set { BinaryPrimitives.WriteUInt32LittleEndian(_plain.AsSpan(Sub('G')), value); Flush(); }
    }

    private uint G1
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_plain.AsSpan(Sub('G') + 4));
        set { BinaryPrimitives.WriteUInt32LittleEndian(_plain.AsSpan(Sub('G') + 4), value); Flush(); }
    }

    private uint MIvs
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_plain.AsSpan(Sub('M') + 4));
        set { BinaryPrimitives.WriteUInt32LittleEndian(_plain.AsSpan(Sub('M') + 4), value); Flush(); }
    }

    private uint MFlags
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_plain.AsSpan(Sub('M') + 8));
        set { BinaryPrimitives.WriteUInt32LittleEndian(_plain.AsSpan(Sub('M') + 8), value); Flush(); }
    }

    /// <summary>Re-encrypts the working copy into the buffer and refreshes the substruct checksum.</summary>
    private void Flush()
    {
        if (_blank) return;
        ushort sum = 0;
        for (var i = 0; i < 48; i += 2)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(i));
        BinaryPrimitives.WriteUInt16LittleEndian(Raw[0x1C..], sum);
        var key = Pid ^ Otid;
        for (var i = 0; i < 48; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(Raw[(0x20 + i)..], BinaryPrimitives.ReadUInt32LittleEndian(_plain.AsSpan(i)) ^ key);
    }

    public bool ChecksumOk
    {
        get
        {
            ushort sum = 0;
            for (var i = 0; i < 48; i += 2)
                sum += BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(i));
            return sum == BinaryPrimitives.ReadUInt16LittleEndian(Raw[0x1C..]);
        }
    }

    // ── growth ──
    public int Species
    {
        get => (int)(G0 & 0x7FF);
        set => G0 = (G0 & ~0x7FFu) | ((uint)value & 0x7FF);
    }

    public int HeldItem
    {
        get => (int)(((G0 >> 11) & 0x3FF) | (((G0 >> 26) & 1) << 10));
        set => G0 = (G0 & ~((0x3FFu << 11) | (1u << 26))) | (((uint)value & 0x3FF) << 11) | ((((uint)value >> 10) & 1) << 26);
    }

    public uint Experience
    {
        get => G1 & 0x1F_FFFF;
        set => G1 = (G1 & ~0x1F_FFFFu) | (value & 0x1F_FFFF);
    }

    public int Friendship
    {
        get => _plain[Sub('G') + 9];
        set { _plain[Sub('G') + 9] = (byte)value; Flush(); }
    }

    public int Ball
    {
        get => _plain[Sub('G') + 10] & 31;
        set { _plain[Sub('G') + 10] = (byte)((_plain[Sub('G') + 10] & ~31) | (value & 31)); Flush(); }
    }

    public int PpBonuses
    {
        get => _plain[Sub('G') + 8];
        set { _plain[Sub('G') + 8] = (byte)value; Flush(); }
    }

    // ── attacks ──
    public int[] Moves
    {
        get
        {
            var a = Sub('A');
            return [BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(a)), BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(a + 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(a + 4)), BinaryPrimitives.ReadUInt16LittleEndian(_plain.AsSpan(a + 6))];
        }
        set
        {
            var a = Sub('A');
            for (var i = 0; i < 4; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(_plain.AsSpan(a + i * 2), (ushort)(i < value.Length ? value[i] : 0));
            Flush();
        }
    }

    public int[] MovePp
    {
        get { var a = Sub('A') + 8; return [_plain[a], _plain[a + 1], _plain[a + 2], _plain[a + 3]]; }
        set
        {
            var a = Sub('A') + 8;
            for (var i = 0; i < 4; i++) _plain[a + i] = (byte)(i < value.Length ? value[i] : 0);
            Flush();
        }
    }

    // ── EVs (substruct order: HP, Atk, Def, Spe, SpA, SpD) -> app order HP, Atk, Def, SpA, SpD, Spe ──
    public int[] EVs
    {
        get { var e = Sub('E'); return [_plain[e], _plain[e + 1], _plain[e + 2], _plain[e + 4], _plain[e + 5], _plain[e + 3]]; }
        set
        {
            var e = Sub('E');
            _plain[e] = (byte)value[0]; _plain[e + 1] = (byte)value[1]; _plain[e + 2] = (byte)value[2];
            _plain[e + 3] = (byte)value[5]; _plain[e + 4] = (byte)value[3]; _plain[e + 5] = (byte)value[4];
            Flush();
        }
    }

    // ── misc ──
    private static readonly int[] IvShifts = [0, 5, 10, 20, 25, 15]; // app order HP, Atk, Def, SpA, SpD, Spe

    public int[] IVs
    {
        get
        {
            var word = MIvs;
            return [.. IvShifts.Select(shift => (int)((word >> shift) & 31))];
        }
        set
        {
            var word = MIvs & 0xC000_0000u; // keep the egg and gigantamax bits
            for (var i = 0; i < 6; i++)
                word |= (uint)(value[i] & 31) << IvShifts[i];
            MIvs = word;
        }
    }

    public bool IsEgg => (MIvs & 0x4000_0000u) != 0;

    public bool IsShiny
    {
        get => (MFlags & 7) != 0;
        set => MFlags = (MFlags & ~7u) | (value ? 1u : 0u);
    }

    /// <summary>Stored gender flag (1 = female); only meaningful for species with both genders.</summary>
    public int GenderFlag
    {
        get => (int)((MFlags >> 3) & 7);
        set => MFlags = (MFlags & ~(7u << 3)) | (((uint)value & 7) << 3);
    }

    public int AbilityNum
    {
        get => (int)((MFlags >> 29) & 3);
        set => MFlags = (MFlags & ~(3u << 29)) | (((uint)value & 3) << 29);
    }

    public int Gender
    {
        get
        {
            var ratio = RogueData.GenderRatio(Species);
            return ratio switch { 255 => 2, 254 => 1, 0 => 0, _ => GenderFlag == 0 ? 0 : 1 };
        }
    }

    // ── validity ──
    public bool IsEmpty => _blank || Species == 0;

    public bool LooksValid => !_blank && ChecksumOk && Species is > 0 and <= MaxSpeciesId && Experience <= 100 * 300 + 299;

    public int Level => Party && Raw[84] is > 0 and <= 100 ? Raw[84] : RogueData.LevelForExperience(Experience);

    // ── party tail: status @80, level @84, hp @86, maxHp @88, atk @90, def @92, spe @94, spa @96, spd @98 ──
    public int CurrentHp => Party ? BinaryPrimitives.ReadUInt16LittleEndian(Raw[86..]) : 0;

    private static readonly int[] PartyStatOffsets = [88, 90, 92, 96, 98, 94]; // app order HP, Atk, Def, SpA, SpD, Spe

    public int[]? PartyStats => Party
        ? [.. PartyStatOffsets.Select(offset => (int)BinaryPrimitives.ReadUInt16LittleEndian(Raw[offset..]))]
        : null;

    public void SetPartyLevelAndStats(int level, bool fullHeal)
    {
        if (!Party) return;
        Raw[84] = (byte)level;
        var stats = RogueData.ComputeStats(Species, level, Nature, IVs, EVs);
        for (var i = 0; i < 6; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(Raw[PartyStatOffsets[i]..], (ushort)stats[i]);
        var hp = fullHeal ? stats[0] : Math.Min(CurrentHp, stats[0]);
        BinaryPrimitives.WriteUInt16LittleEndian(Raw[86..], (ushort)hp);
    }

    /// <summary>Raw copy of the stored mon (box bytes only; a party tail is rebuilt, never copied across).</summary>
    public byte[] BoxBytes() => Buffer.AsSpan(Offset, BoxSize).ToArray();
}
