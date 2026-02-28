namespace MissileCommandOverdrive.Entities;

public class City
{
    public string Id = "";
    public float X, Y;
    public float W;
    public bool Destroyed;
    public float[][] Buildings = []; // height, width pairs per building
}

public class Base
{
    public string Id = "";
    public float X, Y;
    public int Ammo;
    public int MaxAmmo;
    public float Cooldown;
    public bool Destroyed;
    public float DishAngle; // radar dish oscillation
    public float DishDir = 1f;
}

public class Phalanx
{
    public string Id = "PHALANX";
    public float X, Y;
    public int Ammo;
    public int MaxAmmo;
    public float Cool;
    public float FireAcc;
    public float AudioT;
    public float Heat;
    public float SpinAngle;
    public float SpinSpeed;
    public float FireMix;
    public float AimAng;
    public float AimX, AimY;
    public float AimErr;
    public float TargetDist;
    public string? TargetKind;
    public string? LockKey;
    public bool Destroyed;
    public object? Target; // reference to current target entity
}

public class HellRaiser
{
    public float X, Y;
    public string State = "hidden"; // hidden,opening,rising,active,lowering,closing,cooldown
    public string Command = "idle"; // idle,deploy,retract
    public float StateTime;
    public float Lift; // 0..1 vertical emergence
    public float DoorOpen; // 0..1
    public int Ammo;
    public int MaxAmmo;
    public float FireCd;
    public float Cool;
    public float ActiveTime;
    public bool Destroyed;
}
