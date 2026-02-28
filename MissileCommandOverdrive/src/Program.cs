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
