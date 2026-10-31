using System;
using VGMMO.Protocol;

namespace VGMMO.Server.Core;

/// <summary>Simulated remote pilot — no real client behind it. Exercises exactly the
/// same relay path as a real session (RemoteShipState broadcast to the system group,
/// web status projection), so the web map and in-game mirrors can be tested with a
/// single real client.</summary>
public sealed class GhostPilot
{
    public string Id = "";
    public string Name = "";         // nickname (callsign)
    public string FullName = "";
    public string Title = "";
    public string TitleColor = "#BDBDBD";
    public string SystemId = "";
    public ShipStateMessage State = new();

    private readonly float _cx, _cy;   // patrol center (system layout position)
    private readonly float _rx, _ry;   // patrol radii
    private readonly float _speed;     // angular speed (rad/s)
    private float _t;
    private float _prevX, _prevY;

    public GhostPilot(string id, string name, string fullName, string title, string systemId,
        float cx, float cy, float rx, float ry, float speed)
    {
        Id = id; Name = name; FullName = fullName; Title = title;
        SystemId = systemId; _cx = cx; _cy = cy; _rx = rx; _ry = ry; _speed = speed;
        State.SystemId = systemId;
        Tick(0f);
    }

    /// <summary>Advances the patrol (Lissajous around the system center, layout units)
    /// and refreshes the ship state the same way a real client's ShipState does.</summary>
    public void Tick(float dt)
    {
        _t += dt;
        var x = _cx + (float)Math.Sin(_t * _speed) * _rx;
        var y = _cy + (float)Math.Cos(_t * _speed * 0.7) * _ry;
        if (dt > 0f) { State.Vx = (x - _prevX) / dt; State.Vy = (y - _prevY) / dt; }
        _prevX = x; _prevY = y;
        State.X = x; State.Y = y;
        State.Rot = (float)Math.Atan2(State.Vy, State.Vx);
        State.Seq++;
    }
}
