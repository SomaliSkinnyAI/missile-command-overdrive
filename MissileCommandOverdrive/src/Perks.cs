using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Util;

namespace MissileCommandOverdrive;

public enum Rarity { Common, Rare, Epic }

/// <summary>§5 4.3 perk definition. Delegates are AOT-safe (no reflection).
/// Apply runs ONCE at pick time and only writes s.Perks / one-shot state —
/// per-frame effects read the PerkFlags struct at the fixed hooks documented
/// on <see cref="PerkFlags"/>; there is no delegate dispatch in hot paths.</summary>
public record Perk(string Id, Rarity Rarity, string Name, string Desc,
    Func<GameState, bool> CanOffer, Action<GameState> Apply);

/// <summary>§5 4.3 THE central perk-effect struct (one per run, lives on
/// GameState; multipliers default to 1 via <see cref="Defaults"/>).
/// GREP CONTRACT — `s.Perks` is read at these fixed hooks ONLY:
///   Combat.LaunchPlayer            BlastMult, ReloadMult
///   Combat.RegKill                 ScrapPerKill, ComboTimeBonus, ComboBonusMult
///   Combat.SpawnExpl               EmpChain (schedules the echo pulse)
///   Combat.KillCity                CityShield / CityShieldUsed
///   GameUpdate.UpdateAll           ChainT/X/Y echo timer; SalvageMult (wave clear)
///   GameUpdate.UpdPlayer           MirvInterceptor (split trigger)
///   PhalanxSystem.Update           PhalanxRangeMult, PhalanxRateMult
///   HellRaiserSystem.FireBarrage   HrRateMult
///   WaveSystem.StartWave           HrAmmoMult (+ CityShieldUsed re-arm, draft clear)
///   VariantStats.InterceptorSpeed  InterceptorSpeedMult (Combat + AutoDefense share it)
///   GameState.EmpMax               EmpMaxBonus
/// </summary>
public struct PerkFlags
{
    public float BlastMult;            // interceptor blast radius ×
    public float ReloadMult;           // base reload speed × (divides cooldown)
    public int EmpMaxBonus;            // extra EMP slots
    public bool EmpChain;              // EMP fires a follow-up echo pulse
    public float PhalanxRangeMult;
    public float PhalanxRateMult;
    public float HrAmmoMult;           // HellRaiser magazine ×
    public float HrRateMult;           // HellRaiser fire rate ×
    public int ScrapPerKill;           // flat bonus scrap per kill
    public float SalvageMult;          // end-of-wave salvage ×
    public float ComboTimeBonus;       // seconds added to the 4 s combo window
    public float ComboBonusMult;       // combo score-bonus ×
    public float InterceptorSpeedMult;
    public bool CityShield;            // absorbs one city hit per wave
    public bool MirvInterceptor;       // interceptors split into 3 homing children

    // Runtime scratch (not perk choices): the pending CHAIN PULSE echo and the
    // per-wave AEGIS DOME latch (re-armed in WaveSystem.StartWave).
    public float ChainT, ChainX, ChainY;
    public bool CityShieldUsed;

    public static PerkFlags Defaults => new()
    {
        BlastMult = 1f, ReloadMult = 1f,
        PhalanxRangeMult = 1f, PhalanxRateMult = 1f,
        HrAmmoMult = 1f, HrRateMult = 1f,
        SalvageMult = 1f, ComboBonusMult = 1f, InterceptorSpeedMult = 1f
    };
}

/// <summary>§5 4.3 perk-draft armory: 3 seeded cards at every shop-open; keys
/// 1-3 install one, R rerolls once for scrap (the seven scrap buys moved to
/// 4-0). The draft draws from its OWN stream (MasterSeed ^ Level ^ 0xD12AF7)
/// so plan-stream determinism (§4.3 / MCOD_SELFTEST) is untouched.</summary>
public static class PerkSystem
{
    public const int RerollCost = 150;

    // 15-perk v1 pool: 10 Common / 4 Rare / 1 Epic. One copy each per run —
    // CanOffer hides a perk once its own flag is set. Desc strings are
    // pre-wrapped (\n) for the ~165 px shop cards.
    public static readonly Perk[] Pool =
    [
        new("blast", Rarity.Common, "BIG-BORE WARHEADS", "Interceptor blast\nradius +20%",
            s => s.Perks.BlastMult <= 1f, s => s.Perks.BlastMult = 1.2f),
        new("reload", Rarity.Common, "RAPID CYCLER", "Silo reload\n25% faster",
            s => s.Perks.ReloadMult <= 1f, s => s.Perks.ReloadMult = 1.25f),
        new("phxRange", Rarity.Common, "LONG-BARREL CIWS", "Phalanx range\n+30%",
            s => s.Perks.PhalanxRangeMult <= 1f, s => s.Perks.PhalanxRangeMult = 1.3f),
        new("phxRate", Rarity.Common, "DOUBLE FEED", "Phalanx fire\nrate +35%",
            s => s.Perks.PhalanxRateMult <= 1f, s => s.Perks.PhalanxRateMult = 1.35f),
        new("hrAmmo", Rarity.Common, "DEEP MAGAZINES", "HellRaiser\nammo +40%",
            s => s.HellRaiser != null && s.Perks.HrAmmoMult <= 1f, s => s.Perks.HrAmmoMult = 1.4f),
        new("hrRate", Rarity.Common, "HOT LOADER", "HellRaiser fire\nrate +30%",
            s => s.HellRaiser != null && s.Perks.HrRateMult <= 1f, s => s.Perks.HrRateMult = 1.3f),
        new("scrap", Rarity.Common, "SCRAP MAGNET", "+2 scrap on\nevery kill",
            s => s.Perks.ScrapPerKill == 0, s => s.Perks.ScrapPerKill = 2),
        new("salvage", Rarity.Common, "SALVAGE RIGS", "End-of-wave\nsalvage +50%",
            s => s.Perks.SalvageMult <= 1f, s => s.Perks.SalvageMult = 1.5f),
        new("intSpeed", Rarity.Common, "AFTERBURNERS", "Interceptors fly\n25% faster",
            s => s.Perks.InterceptorSpeedMult <= 1f, s => s.Perks.InterceptorSpeedMult = 1.25f),
        new("comboTime", Rarity.Common, "COMBO CAPACITOR", "Combo window\n+1.5 s",
            s => s.Perks.ComboTimeBonus <= 0f, s => s.Perks.ComboTimeBonus = 1.5f),
        new("empSlot", Rarity.Rare, "EMP RESERVE", "+1 EMP slot,\n+1 charge now",
            s => s.Perks.EmpMaxBonus == 0, s => { s.Perks.EmpMaxBonus = 1; s.Emp++; }),
        new("empChain", Rarity.Rare, "CHAIN PULSE", "EMP fires a free\necho pulse",
            s => !s.Perks.EmpChain, s => s.Perks.EmpChain = true),
        new("comboMult", Rarity.Rare, "OVERDRIVE SCORING", "Combo score\nbonus x1.5",
            s => s.Perks.ComboBonusMult <= 1f, s => s.Perks.ComboBonusMult = 1.5f),
        new("cityShield", Rarity.Rare, "AEGIS DOME", "Absorbs one city\nhit per wave",
            s => !s.Perks.CityShield, s => s.Perks.CityShield = true),
        new("mirv", Rarity.Epic, "MIRV INTERCEPTOR", "Interceptors split\ninto 3 homing\nwarheads",
            s => !s.Perks.MirvInterceptor, s => s.Perks.MirvInterceptor = true),
    ];

    /// <summary>Seed + roll the 3-card offer. Called at the real wave-clear
    /// shop-open (GameUpdate) and by the demo's forced shop.</summary>
    public static void BuildDraft(GameState s)
    {
        // §4.3: a dedicated stream — the draft must never consume plan draws
        s.DraftRng = new Xoshiro(s.MasterSeed ^ (ulong)s.Level ^ 0xD12AF7UL);
        s.DraftPicked = -1;
        s.DraftRerolled = false;
        Roll(s);
    }

    static readonly List<Perk> _cands = []; // shop-path scratch

    static void Roll(GameState s)
    {
        for (int slot = 0; slot < 3; slot++)
        {
            s.Draft[slot] = null;
            // Rarity weights: Common .70 / Rare .25 / Epic .05.
            // Phase 6 hook: boss waves (Level % 5 == 0) guarantee ≥ Rare —
            // clamp `rar` up for slot 0 when the boss framework lands.
            float r = s.DraftRng.NextSingle();
            Rarity rar = r < 0.70f ? Rarity.Common : r < 0.95f ? Rarity.Rare : Rarity.Epic;
            // Rolled tier first, then the others (the pool can run dry late-run)
            if (!FillSlot(s, slot, rar) && !FillSlot(s, slot, Rarity.Common)
                && !FillSlot(s, slot, Rarity.Rare))
                FillSlot(s, slot, Rarity.Epic);
        }
    }

    static bool FillSlot(GameState s, int slot, Rarity rar)
    {
        _cands.Clear();
        foreach (var p in Pool)
        {
            if (p.Rarity != rar || !p.CanOffer(s)) continue;
            if (ReferenceEquals(s.Draft[0], p) || ReferenceEquals(s.Draft[1], p)
                || ReferenceEquals(s.Draft[2], p)) continue;
            _cands.Add(p);
        }
        if (_cands.Count == 0) return false;
        s.Draft[slot] = _cands[s.DraftRng.Next(_cands.Count)];
        return true;
    }

    /// <summary>Shop keys 1-3: install the card and lock the draft.</summary>
    public static void TryPick(GameState s, int slot)
    {
        if (s.DraftPicked >= 0)
        { s.Msg = "Armory draft expended"; s.MsgT = 1.0f; SynthAudio.UiDeny(); return; }
        var p = s.Draft[slot];
        if (p == null)
        { s.Msg = "Slot out of stock"; s.MsgT = 1.0f; SynthAudio.UiDeny(); return; }
        p.Apply(s);
        s.OwnedPerks.Add(p);
        s.DraftPicked = slot;
        s.Msg = $"PERK INSTALLED: {p.Name}"; // key-press path — alloc OK
        s.MsgT = 1.6f;
        SynthAudio.UiConfirm(); // §5 4.5 purchase-confirm arp
    }

    /// <summary>Shop key R: one reroll per shop for scrap. Program.cs's shop
    /// block returns before the global R-restart handler, so this can never
    /// restart the run.</summary>
    public static void TryReroll(GameState s)
    {
        if (s.DraftPicked >= 0)
        { s.Msg = "Armory draft expended"; s.MsgT = 1.0f; SynthAudio.UiDeny(); return; }
        if (s.DraftRerolled)
        { s.Msg = "Reroll already used"; s.MsgT = 1.0f; SynthAudio.UiDeny(); return; }
        // Pool exhausted (all 3 slots OUT OF STOCK): a reroll is a provable
        // no-op — bail before charging. No DraftRng draw happens on this path.
        if (s.Draft[0] == null && s.Draft[1] == null && s.Draft[2] == null)
        { s.Msg = "Armory depleted"; s.MsgT = 1.0f; SynthAudio.UiDeny(); return; }
        if (s.Scrap < RerollCost)
        { s.Msg = "Insufficient scrap to reroll"; s.MsgT = 1.0f; SynthAudio.UiDeny(); return; }
        s.Scrap -= RerollCost;
        s.DraftRerolled = true;
        Roll(s);
        s.Msg = "Armory restocked";
        s.MsgT = 1.0f;
        SynthAudio.UiConfirm(); // §5 4.5: a reroll is a purchase
    }

    /// <summary>The draft is shop-scoped (§5 4.3) — cleared on every wave
    /// start and run reset.</summary>
    public static void ClearDraft(GameState s)
    {
        s.Draft[0] = s.Draft[1] = s.Draft[2] = null;
        s.DraftPicked = -1;
        s.DraftRerolled = false;
    }
}
