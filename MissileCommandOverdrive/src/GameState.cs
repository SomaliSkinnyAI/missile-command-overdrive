using MissileCommandOverdrive.Entities;

namespace MissileCommandOverdrive;

public class GameState
{
    // Theme
    public string Theme = "modern"; // modern, xbox, recharged

    // Viewport
    public float W = 1280, H = 720;
    public float GroundY, HorizonY;

    // Timing
    public float Time, Last;

    // Game flow
    public bool Intro = true;
    public bool GameOver;
    public bool GameOverSfx;
    public float GameOverTime;
    public int Level = 1;
    public int Score;
    public int Combo;
    public int MaxCombo;
    public float ComboTimer;
    public float Danger;

    // Wave
    public float WavePause = 2f;
    public float WaveTime;
    public List<WavePlanEntry> WavePlan = [];
    public int SpawnI;
    public int UfoQuota;
    public float NextUfo;
    public int RaiderQuota;
    public float NextRaider;

    // Entities
    public List<Enemy> Enemies = [];
    public List<PlayerMissile> PlayerMissiles = [];
    public List<Explosion> Explosions = [];
    public List<UFO> UFOs = [];
    public List<Raider> Raiders = [];
    public Daemon? Demon;

    // Defenses
    public List<Base> Bases = [];
    public List<City> Cities = [];
    public List<Phalanx> Phalanxes = [];
    public HellRaiser? HellRaiser;

    // Particles / FX
    public List<Spark> Sparks = [];
    public List<Smoke> SmokeParts = [];
    public List<Trail> Trails = [];
    public List<Debris> DebrisParts = [];
    public List<Shockwave> Shockwaves = [];
    public List<LightBurst> LightBursts = [];
    public List<MuzzleFlash> MuzzleFlashes = [];
    public List<Scorch> Scorches = [];
    public List<ShootingStar> ShootingStars = [];
    public List<FloatingText> FloatingTexts = [];

    // Screen FX
    public float Chromatic;
    public float Shake;
    public float Flash;

    // Input
    public float MouseX, MouseY;

    // Player systems
    public bool Auto; // auto-defense
    public int Emp = 1;
    public int EmpMax = 3;
    public float EmpCd;
    public int? SelectedBase;

    // Messages
    public string Msg = "";
    public float MsgT;
    public string Note = "";
    public float NoteT;

    // Shop
    public bool Shop;
    public float ShopTimer;
    public Upgrades Upgrades = new();

    // ID counter
    public int NextId = 1;
    public int NewId() => NextId++;

    // World scenery (generated once)
    public List<float[]> StarsA = [];
    public List<float[]> StarsB = [];
    public List<float[]> Nebula = [];
    public List<float[]> Aurora = [];
    public List<float[]> Clouds = [];
    public List<float[]> Haze = [];
    public List<float[]> MountFar = [];
    public List<float[]> MountNear = [];

    // Weather
    public WeatherState Weather = new();

    // Debug
    public DebugState Debug = new();
}

public class WavePlanEntry
{
    public string Variant = "standard";
    public float Time;
    public float Lane;
}

public class Upgrades
{
    public float BlastScale = 1.0f;
    public float ReloadMult = 1.0f;
    public float EmpScale = 1.0f;
    public float PhalanxEff = 1.0f;
}

public class WeatherState
{
    public string Mode = "clear"; // clear, ash, storm
    public float Intensity;
    public float Wind;
    public List<WeatherParticle> Particles = [];
    public List<FogBand> FogBands = [];
    public float LightningTimer;
    public float ThunderCd;
    public List<LightningBolt> Bolts = [];
}

public struct WeatherParticle
{
    public float X, Y, Vx, Vy;
    public float Z; // parallax depth
    public float Alpha;
    public float Len; // rain streak length / ash size
    public float Hue;
}

public struct FogBand
{
    public float Y, Thickness, Alpha, Speed, Phase;
}

public struct LightningBolt
{
    public float X, Y0, Y1;
    public float Life, MaxLife;
    public float Bright;
    public int Branches;
    public List<LightningSegment> Segments;
}

public struct LightningSegment
{
    public float X1, Y1, X2, Y2;
    public bool Branch;
}

public class DebugState
{
    public bool Enabled;
    public int MaxEventsPerWave = 120000;
    public string SessionStartedAt = DateTime.UtcNow.ToString("o");
    public string? CurrentWave;
    public int WaveSeq;
    public Dictionary<string, object> Waves = [];
    public int SessionDrops;
}
