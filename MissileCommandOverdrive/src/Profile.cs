using System.Text.Json;
using System.Text.Json.Serialization;
using Raylib_cs;
using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

public class ScoreEntry
{
    public string Initials { get; set; } = "AAA";
    public int Score { get; set; }
    public int Wave { get; set; }
    public int MaxCombo { get; set; }
    public string DateUtc { get; set; } = "";
    public bool Assisted { get; set; }
}

public class LifetimeStats
{
    public long Kills { get; set; }
    public int WavesCleared { get; set; }
    public int Runs { get; set; }
}

/// <summary>Unified persistent commander profile (§4.2/§5 3.2): one file, one schema.</summary>
public class ProfileData
{
    public int Version { get; set; } = 1;
    public string Initials { get; set; } = "ACE";
    public List<ScoreEntry> Top10 { get; set; } = [];
    public Settings Settings { get; set; } = new();
    public LifetimeStats Lifetime { get; set; } = new();
}

// §4.2: source-generated serializer ONLY — reflection JSON throws under PublishAot.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProfileData))]
public partial class ProfileJsonContext : JsonSerializerContext { }

/// <summary>Profile persistence + the game-over initials ceremony (§5 3.2).
/// Load at boot, save on game-over and shutdown; corrupt/missing files fall back
/// to defaults. Display strings for the game-over screen are cached here and
/// rebuilt only on profile events, so the renderer stays zero-alloc.</summary>
public static class Profile
{
    public static ProfileData Data { get; private set; } = new();

    // ----- initials-entry state (armed by a top-10 finish) -----
    public static bool PendingInitials;
    public static int PendingIndex = -1; // row to highlight in the table
    public static int SlotSel;
    public static readonly char[] Slots = ['A', 'A', 'A'];
    public static float RollT;  // 1 → 0 letter-roll animation on the active slot
    public static int RollDir;  // +1 rolled down, -1 rolled up

    // ----- cached game-over display strings (renderer reads these per frame) -----
    public static string SeedText = "";
    public static readonly string[] TableText = new string[10];
    public static int TableCount;

    // Single-letter strings so the renderer never allocates from a slot char.
    static readonly string[] _letters = BuildLetters();
    public static string Letter(char c) => _letters[c - 'A'];

    static string[] BuildLetters()
    {
        var a = new string[26];
        for (int i = 0; i < 26; i++) a[i] = ((char)('A' + i)).ToString();
        return a;
    }

    static readonly System.Text.StringBuilder _sb = new(64);

    // Demo/eval runs write to a sidecar so verification never pollutes the real profile.
    static string ProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MissileCommandOverdrive", DemoDriver.Active ? "profile.demo.json" : "profile.json");

    public static void Load()
    {
        try
        {
            string path = ProfilePath;
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize(File.ReadAllText(path), ProfileJsonContext.Default.ProfileData);
                if (data != null) Data = data;
            }
        }
        catch
        {
            Data = new ProfileData(); // corrupt → defaults; never block boot
        }
        Sanitize();
        RebuildTable();
    }

    public static void Save()
    {
        try
        {
            string path = ProfilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Atomic on APFS/NTFS: a crash mid-write can never destroy the profile
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, ProfileJsonContext.Default.ProfileData));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Non-fatal: persistence must never take the game down.
        }
    }

    // Deserialized JSON can carry nulls/garbage despite the nullable annotations —
    // coerce everything back into valid ranges before the game reads it.
    static void Sanitize()
    {
        if (Data.Settings is null) Data.Settings = new Settings();
        if (Data.Lifetime is null) Data.Lifetime = new LifetimeStats();
        if (Data.Top10 is null) Data.Top10 = [];

        var st = Data.Settings;
        st.Volume = MathH.Clamp(st.Volume, 0f, 1f);
        st.ShakeIntensity = MathH.Clamp(st.ShakeIntensity, 0f, 1f);
        st.UiScale = MathH.Clamp(st.UiScale, 0.8f, 1.3f);
        if (st.Theme is not ("modern" or "xbox" or "recharged")) st.Theme = "modern";

        Data.Initials = SanitizeInitials(Data.Initials);
        Data.Top10.RemoveAll(e => e is null);
        foreach (var e in Data.Top10) e.Initials = SanitizeInitials(e.Initials);
        Data.Top10.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (Data.Top10.Count > 10) Data.Top10.RemoveRange(10, Data.Top10.Count - 10);
    }

    static string SanitizeInitials(string? ini)
    {
        Span<char> c = ['A', 'A', 'A'];
        if (ini != null)
        {
            for (int i = 0; i < 3 && i < ini.Length; i++)
            {
                char ch = char.ToUpperInvariant(ini[i]);
                if (ch >= 'A' && ch <= 'Z') c[i] = ch;
            }
        }
        return new string(c);
    }

    /// <summary>Run ended (phase edge detected in Program.cs): tally the run,
    /// insert a top-10 entry, arm the initials ceremony, persist.</summary>
    public static void OnGameOver(GameState s)
    {
        Data.Lifetime.Runs++;
        SeedText = "SEED " + SeedUtil.Format(s.MasterSeed);

        PendingInitials = false;
        PendingIndex = -1;
        if (s.Score > 0)
        {
            var top = Data.Top10;
            int idx = 0;
            while (idx < top.Count && top[idx].Score >= s.Score) idx++;
            if (idx < 10)
            {
                top.Insert(idx, new ScoreEntry
                {
                    Initials = Data.Initials, // placeholder until the ceremony confirms
                    Score = s.Score,
                    Wave = s.Level,
                    MaxCombo = s.MaxCombo,
                    DateUtc = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    Assisted = s.AssistedRun
                });
                if (top.Count > 10) top.RemoveAt(10);
                PendingInitials = true;
                PendingIndex = idx;
                SlotSel = 0;
                for (int i = 0; i < 3; i++) Slots[i] = Data.Initials[i];
                RollT = 0;
            }
        }
        RebuildTable();
        Save();
    }

    /// <summary>Per-frame input while the initials ceremony is active. The caller
    /// (Program.HandleInput) returns right after, swallowing all other input.</summary>
    public static void UpdateInitialsEntry(GameState s, float rawDt)
    {
        if (RollT > 0) RollT = MathF.Max(0, RollT - rawDt * 7f);

        int rot = 0;
        if (Raylib.IsKeyPressed(KeyboardKey.Up)) rot = -1;
        if (Raylib.IsKeyPressed(KeyboardKey.Down)) rot = 1;
        if (rot != 0)
        {
            Slots[SlotSel] = (char)('A' + ((Slots[SlotSel] - 'A' + rot + 26) % 26));
            RollT = 1f;
            RollDir = rot;
            SynthAudio.Hit(0.5f, 0.16f); // low-volume letter-roll tick
            if (PendingIndex >= 0) BuildRow(PendingIndex); // live preview in the table
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Left)) { SlotSel = (SlotSel + 2) % 3; RollT = 0; SynthAudio.Hit(0.42f, 0.12f); }
        if (Raylib.IsKeyPressed(KeyboardKey.Right)) { SlotSel = (SlotSel + 1) % 3; RollT = 0; SynthAudio.Hit(0.58f, 0.12f); }

        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
            ConfirmInitials();
    }

    public static void ConfirmInitials()
    {
        if (!PendingInitials) return;
        string ini = new(Slots);
        Data.Initials = ini;
        if (PendingIndex >= 0 && PendingIndex < Data.Top10.Count)
            Data.Top10[PendingIndex].Initials = ini;
        PendingInitials = false;
        SynthAudio.Hit(0.5f, 0.45f); // confirm thunk
        RebuildTable();
        Save();
    }

    /// <summary>Defensive: a scripted/demo reset can land mid-ceremony — the entry
    /// keeps its placeholder initials (already persisted by OnGameOver).</summary>
    public static void CancelInitialsEntry()
    {
        PendingInitials = false;
        PendingIndex = -1;
    }

    public static void RebuildTable()
    {
        TableCount = Math.Min(Data.Top10.Count, TableText.Length);
        for (int i = 0; i < TableCount; i++) BuildRow(i);
    }

    // Row layout (mono font): " 1  ACE    123456  W 8  x12  A"
    // — must stay column-aligned with Renderer.GoTableHeader.
    static void BuildRow(int i)
    {
        var e = Data.Top10[i];
        _sb.Clear();
        int rank = i + 1;
        if (rank < 10) _sb.Append(' ');
        _sb.Append(rank).Append("  ");
        if (PendingInitials && i == PendingIndex)
            _sb.Append(Slots[0]).Append(Slots[1]).Append(Slots[2]);
        else
            _sb.Append(e.Initials);
        _sb.Append("  ");
        AppendPadded(_sb, e.Score, 8);
        _sb.Append("  W");
        if (e.Wave < 10) _sb.Append(' ');
        _sb.Append(e.Wave);
        _sb.Append("  x");
        if (e.MaxCombo < 10) _sb.Append(' ');
        _sb.Append(e.MaxCombo);
        if (e.Assisted) _sb.Append("  A");
        TableText[i] = _sb.ToString();
    }

    static void AppendPadded(System.Text.StringBuilder sb, int v, int width)
    {
        int digits = 1;
        for (int t = v / 10; t > 0; t /= 10) digits++;
        for (int p = digits; p < width; p++) sb.Append(' ');
        sb.Append(v);
    }
}
