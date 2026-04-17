using Raylib_cs;
using MissileCommandOverdrive;
using MissileCommandOverdrive.Audio;
using MissileCommandOverdrive.Util;

const int InitialWidth = 1280;
const int InitialHeight = 720;

Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint);
Raylib.InitWindow(InitialWidth, InitialHeight, "Missile Command Overdrive");
Raylib.SetTargetFPS(60);
Raylib.SetExitKey(KeyboardKey.Null); // prevent ESC from closing

// Use nearest-neighbor filtering for crisp pixel art rendering
Raylib.SetTextureFilter(Raylib.GetFontDefault().Texture, TextureFilter.Point);

var S = new GameState
{
    W = InitialWidth,
    H = InitialHeight
};

Resize(S);
GameInit.BuildWorld(S);
SynthAudio.Init();

while (!Raylib.WindowShouldClose())
{
    // Handle resize
    if (Raylib.IsWindowResized())
    {
        S.W = Raylib.GetScreenWidth();
        S.H = Raylib.GetScreenHeight();
        Resize(S);
    }

    float dt = Raylib.GetFrameTime();
    if (dt > 0.1f) dt = 0.1f; // cap to avoid giant steps

    // Input
    var mp = Raylib.GetMousePosition();
    S.MouseX = mp.X;
    S.MouseY = mp.Y;

    HandleInput(S);
    if (!S.Intro) GameUpdate.UpdateAll(S, dt);
    else S.Time += dt;
    SynthAudio.Update(S, dt);

    Raylib.BeginDrawing();
    Raylib.ClearBackground(new Color(2, 5, 10, 255));
    MissileCommandOverdrive.Rendering.Renderer.DrawAll(S);
    Raylib.EndDrawing();
}

MissileCommandOverdrive.Rendering.Renderer.Shutdown();
SynthAudio.Shutdown();
Raylib.CloseWindow();

// --- Core functions ---

static void Resize(GameState s)
{
    s.GroundY = s.H * 0.82f;
    s.HorizonY = s.H * 0.38f;
    // Reposition defenses and rebuild scenery for new dimensions
    if (s.Bases.Count > 0)
        GameInit.Reposition(s);
}

static void HandleInput(GameState s)
{
    // ----- SECRET CODE BUFFER (666 -> summon demon) -----
    // Accept digits 0-9 and letters a-z, keep last 8 chars
    int keyCh = Raylib.GetCharPressed();
    while (keyCh > 0)
    {
        if ((keyCh >= '0' && keyCh <= '9') || (keyCh >= 'a' && keyCh <= 'z') || (keyCh >= 'A' && keyCh <= 'Z'))
        {
            SecretCode.Buffer += char.ToLowerInvariant((char)keyCh);
            if (SecretCode.Buffer.Length > 8) SecretCode.Buffer = SecretCode.Buffer.Substring(SecretCode.Buffer.Length - 8);
            if (SecretCode.Buffer.EndsWith("666"))
            {
                DemonSystem.Summon(s);
                SecretCode.Buffer = "";
            }
            else if (SecretCode.Buffer.EndsWith("777"))
            {
                MothershipSystem.Summon(s);
                SecretCode.Buffer = "";
            }
        }
        keyCh = Raylib.GetCharPressed();
    }

    // ----- SHOP INPUT (between waves) -----
    if (s.Shop)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.One) && s.Score >= 5000)
        {
            var dead = s.Cities.Where(c => c.Destroyed).ToList();
            if (dead.Count > 0)
            {
                s.Score -= 5000;
                var c = dead[Random.Shared.Next(dead.Count)];
                c.Destroyed = false;
                SynthAudio.Thunder(0.5f, 0.4f);
                s.Msg = "City rebuilt"; s.MsgT = 1.2f;
            }
            else { s.Msg = "All cities intact"; s.MsgT = 1.0f; }
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Two) && s.Score >= 2500)
        {
            if (s.Emp < s.EmpMax)
            {
                s.Score -= 2500;
                s.Emp++;
                SynthAudio.Launch(0.5f);
                s.Msg = "EMP +1"; s.MsgT = 1.0f;
            }
            else { s.Msg = "EMP at maximum capacity"; s.MsgT = 1.0f; }
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Three) && s.Score >= 4000 && s.Upgrades.BlastScale < 2.8f - 0.001f)
        {
            s.Score -= 4000;
            s.Upgrades.BlastScale = MathF.Min(2.8f, s.Upgrades.BlastScale + 0.2f);
            SynthAudio.Impact(0.5f, false);
            s.Msg = $"Warhead Yield x{s.Upgrades.BlastScale:F1}"; s.MsgT = 1.2f;
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Four) && s.Score >= 3500 && s.Upgrades.ReloadMult < 2.2f - 0.001f)
        {
            s.Score -= 3500;
            s.Upgrades.ReloadMult = MathF.Min(2.2f, s.Upgrades.ReloadMult + 0.12f);
            SynthAudio.Launch(0.5f);
            s.Msg = $"Reload Boost x{s.Upgrades.ReloadMult:F2}"; s.MsgT = 1.2f;
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Five) && s.Score >= 3600 && s.Upgrades.EmpScale < 2.4f - 0.001f)
        {
            s.Score -= 3600;
            s.Upgrades.EmpScale = MathF.Min(2.4f, s.Upgrades.EmpScale + 0.14f);
            s.Upgrades.PhalanxEff = MathF.Min(2.0f, s.Upgrades.PhalanxEff + 0.08f);
            SynthAudio.Thunder(0.5f, 0.5f);
            s.Msg = "EMP/Phalanx Boost"; s.MsgT = 1.2f;
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Space))
        {
            s.Shop = false;
            s.Level++;
            WaveSystem.StartWave(s, 2.9f);
        }
        return; // while shop open, suppress other gameplay inputs
    }

    // Fire interceptor
    if (Raylib.IsMouseButtonPressed(MouseButton.Left) && !s.Intro && !s.GameOver && !s.Shop)
    {
        Combat.LaunchPlayer(s, s.MouseX, s.MouseY);
    }

    // EMP
    if ((Raylib.IsMouseButtonPressed(MouseButton.Right) || Raylib.IsKeyPressed(KeyboardKey.E))
        && !s.Intro && !s.GameOver)
    {
        Combat.UseEMP(s);
    }

    // Toggle auto defense
    if (Raylib.IsKeyPressed(KeyboardKey.C))
    {
        s.Auto = !s.Auto;
        s.Msg = s.Auto ? "Auto Defense ON" : "Auto Defense OFF";
        s.MsgT = 1.2f;
    }

    // Hell Raiser
    if (Raylib.IsKeyPressed(KeyboardKey.H))
    {
        HellRaiserSystem.Toggle(s);
    }

    // Theme toggle
    if (Raylib.IsKeyPressed(KeyboardKey.T))
    {
        s.Theme = s.Theme switch
        {
            "modern" => "xbox",
            "xbox" => "recharged",
            _ => "modern"
        };
        s.Msg = $"Theme: {s.Theme.ToUpperInvariant()}";
        s.MsgT = 1.2f;
    }

    // Restart
    if (Raylib.IsKeyPressed(KeyboardKey.R))
    {
        GameInit.ResetGame(s);
    }

    // Level skip
    if (Raylib.IsKeyPressed(KeyboardKey.RightBracket) || Raylib.IsKeyPressed(KeyboardKey.PageUp))
    {
        if (s.Intro || s.GameOver) GameInit.ResetGame(s);
        s.Shop = false;
        s.Level = Math.Max(1, s.Level + 1);
        WaveSystem.StartWave(s, 0.7f);
        s.Msg = $"Jumped to Wave {s.Level}";
        s.MsgT = 1.2f;
    }
    if (Raylib.IsKeyPressed(KeyboardKey.LeftBracket) || Raylib.IsKeyPressed(KeyboardKey.PageDown))
    {
        if (s.Intro || s.GameOver) GameInit.ResetGame(s);
        s.Shop = false;
        s.Level = Math.Max(1, s.Level - 1);
        WaveSystem.StartWave(s, 0.7f);
        s.Msg = $"Jumped to Wave {s.Level}";
        s.MsgT = 1.2f;
    }

    // Debug
    if (Raylib.IsKeyPressed(KeyboardKey.F8))
    {
        s.Debug.Enabled = !s.Debug.Enabled;
        s.Msg = s.Debug.Enabled ? "Debug telemetry ON" : "Debug telemetry OFF";
        s.MsgT = 1.0f;
    }

    // Mute toggle
    if (Raylib.IsKeyPressed(KeyboardKey.M))
    {
        SynthAudio.ToggleMute();
        s.Msg = SynthAudio.IsMuted ? "Audio MUTED" : "Audio ON";
        s.MsgT = 1.0f;
    }

    // Start game on click during intro
    if (s.Intro && Raylib.IsMouseButtonPressed(MouseButton.Left))
    {
        GameInit.ResetGame(s);
    }
}

// Update is now handled by GameUpdate.UpdateAll()

// Drawing is now handled by Rendering.Renderer.DrawAll()
