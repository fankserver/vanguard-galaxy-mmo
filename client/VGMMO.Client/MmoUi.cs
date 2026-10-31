using System.Collections.Generic;
using UnityEngine;

namespace VGMMO.Client;

/// <summary>Simple IMGUI panel: server fields, connect/disconnect, status and a log view. Toggle with F8.</summary>
public sealed class MmoUi : MonoBehaviour
{
    private Rect _window = new(16f, 16f, 380f, 420f);
    private bool _show = true;
    private Vector2 _scroll;

    private string _host = "127.0.0.1";
    private string _port = "34567";
    private string _status = "";

    private void Start()
    {
        _host = MmoPlugin.Instance?.Host.Value ?? _host;
        _port = MmoPlugin.Instance?.Port.Value.ToString() ?? _port;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8)) _show = !_show;
    }

    private void OnGUI()
    {
        if (!_show || MmoPlugin.Instance == null) return;
        _window = GUI.Window(761, _window, Draw, "VGMMO client");
    }

    private void Draw(int id)
    {
        var session = MmoPlugin.Instance!.Session;
        GUILayout.BeginVertical();
        GUILayout.Label("Server");
        GUILayout.BeginHorizontal();
        GUILayout.Label("host", GUILayout.Width(42));
        _host = GUILayout.TextField(_host);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("port", GUILayout.Width(42));
        _port = GUILayout.TextField(_port);
        GUILayout.EndHorizontal();
        // Identity (callsign/full name/title) is read from the save and sent to the
        // server automatically — nothing to enter or display here.

        GUILayout.BeginHorizontal();
        if (session.Connected)
        {
            if (GUILayout.Button("Disconnect")) session.Disconnect();
        }
        else if (GUILayout.Button("Connect"))
        {
            var port = int.TryParse(_port, out var p) ? p : 34567;
            session.Connect(_host, port, MmoPlugin.Instance?.PlayerName.Value ?? "Player", MmoPlugin.Instance?.BuildHash.Value ?? "vgmmo-night-1");
        }
        GUILayout.Label("F8 to hide", GUILayout.Width(80));
        GUILayout.EndHorizontal();

        _status = session.Status + (session.SessionId.Length > 0 ? " · " + session.SessionId[..6] : "");
        GUILayout.Label("Status: " + _status);

        GUILayout.Label("Log");
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(240));
        foreach (var line in session.LogLines) GUILayout.Label(line);
        GUILayout.EndScrollView();
        GUILayout.EndVertical();

        GUI.DragWindow(new Rect(0, 0, 10000, 20));
    }
}
