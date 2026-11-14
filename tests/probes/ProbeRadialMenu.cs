using Godot;

namespace SpaceCorporation;

/// <summary>Probe кругового меню контекстных действий (Tab / radial_menu):
///
///   1) в Main.tscn на HudLayer есть RadialMenu, он закрыт и подключён к
///      TargetingService;
///   2) Tab зажат (radial_menu press) — кольцо открыто; отпустили (release) —
///      закрыто;
///   3) без цели сектор «Связь с целью» (лево-низ, idx 5) НЕдоступен:
///      TryActivate(5) = false; прочие сектора недоступны всегда;
///   4) с целью (spawn + CycleNext) TryActivate(5) = true, меню закрывается,
///      в комм-лог приходит событие «Commlink».
/// Запуск: ./run-tests.sh res://tests/probes/ProbeRadialMenu.tscn</summary>
public partial class ProbeRadialMenu : Node
{
    private const int SitFrames = 40;

    private int _frames, _phase, _failures;
    private Node _main = null!;
    private HudLayer _hud = null!;
    private TargetingService _svc = null!;
    private RadialMenu _menu = null!;
    private WorldSpawner _spawner = null!;
    private string? _lastEvent;

    public override void _Ready()
    {
        _main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(_main);
    }

    public override void _Process(double delta)
    {
        _frames++;
        switch (_phase)
        {
            case 0 when _frames == SitFrames:
                Setup(); _phase = 1; break;
            case 1 when _frames == SitFrames + 3:
                TabPress(); _phase = 2; break; // открыть
            case 2 when _frames == SitFrames + 5:
                CheckOpen(); TabRelease(); _phase = 3; break; // отпустили — закрылось
            case 3 when _frames == SitFrames + 7:
                CheckClosedNoTarget(); _phase = 4; break;
            case 4 when _frames == SitFrames + 10:
                AcquireTarget(); _phase = 5; break;
            case 5 when _frames == SitFrames + 14:
                CommlinkAndFinish(); break;
        }
    }

    private void Setup()
    {
        GetWindow().Size = new Vector2I(1152, 648); //.реальный экран: Size у Control и центр кольца
        _hud = _main.GetNode<HudLayer>("HudLayer");
        _svc = _hud.GetNode<TargetingService>("TargetingService");
        _menu = _hud.GetNode<RadialMenu>("HudRoot/RadialMenu");
        _spawner = FindNode<WorldSpawner>(_main);

        NetworkManager.Instance?.TestSetRoleForTest(NetworkManager.NetRole.Host);

        Check(_menu != null, "на HudLayer есть узел RadialMenu");
        Check(_menu.Targeting == _svc, "RadialMenu подключён к TargetingService");
        Check(!_menu.IsOpen, "меню закрыто до нажатия Tab");

        if (HudEventBus.Instance != null)
            HudEventBus.Instance.OnEvent += m => _lastEvent = m;

        var cam = GetViewport().GetCamera3D();
        _svc.Clear();
    }

    /// <summary>Нажать Tab как реальный игрок (action radial_menu).</summary>
    private void TabPress()
    {
        var ev = new InputEventKey
        {
            Pressed = true,
            PhysicalKeycode = Key.Tab,
        };
        Input.ParseInputEvent(ev);
    }

    /// <summary>Отпустить Tab (кольцо закрыто при отпускании).</summary>
    private void TabRelease()
    {
        var ev = new InputEventKey
        {
            Pressed = false,
            PhysicalKeycode = Key.Tab,
        };
        Input.ParseInputEvent(ev);
    }

    private void CheckOpen()
    {
        Check(_menu.IsOpen, "Tab открыл кольцо");

        // Геометрия хиттеста = геометрия отрисовки: сектор i рисуется центром
        // на i*45°, и точка на этом луче (и ±21° от него) должна попадать в i,
        // а за границей +23° — уже в i+1. (Баг: подсветка убегала на
        // полсектора по часовой.)
        Vector2 c = _menu.Size / 2f;
        float rMid = (90f + 190f) / 2f;
        bool geoOk = true;
        for (int i = 0; i < RadialMenu.SectorCount; i++)
        {
            foreach (float off in new[] { 0f, -21f, 21f })
            {
                float a = Mathf.DegToRad(i * 45f - 90f + off);
                var p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rMid;
                if (_menu.SectorAt(p) != i) geoOk = false;
            }
            float ab = Mathf.DegToRad(i * 45f - 90f + 23f);
            var pb = c + new Vector2(Mathf.Cos(ab), Mathf.Sin(ab)) * rMid;
            if (_menu.SectorAt(pb) != (i + 1) % RadialMenu.SectorCount) geoOk = false;
        }
        Check(geoOk, "ховер совпадает с сектором (без сдвига на полсектора)");
        // Ховер продолжается за внешней дугой — вплоть до края экрана.
        var pOut = c + new Vector2(0f, -(rMid + 120f));
        Check(_menu.SectorAt(pOut) == 0, "точка за кольцом — тот же сектор (выбор до края экрана)");
        Check(_menu.SectorAt(new Vector2(1f, 1f)) >= 0, "угол экрана — сектор, не -1");
    }

    private void CheckClosedNoTarget()
    {
        Check(!_menu.IsOpen, "отпускание Tab закрыло кольцо");

        _menu.Open();
        Check(!_menu.TryActivate(RadialMenu.SectorCommlink),
            "без цели сектор «Связь с целью» недоступен");
        // Дебаг-режим пустых слотов: заглушки «Опция N» доступны и сообщают.
        if (RadialMenu.DebugSlots)
        {
            // сектор 6 (лево) — точка за внешней дугой, за краем экрана;
            // выбор происходит в момент закрытия (commit).
            Vector2 c = _menu.Size / 2f;
            float a6 = Mathf.DegToRad(6 * 45f - 90f);
            var edgeLeft = c + new Vector2(Mathf.Cos(a6), Mathf.Sin(a6)) * 900f;
            _lastEvent = null;
            _menu.Open();
            _menu.HoverAt(edgeLeft);
            _menu.Close(commit: true); //.закрытие «отпусканием Tab» = выбор
            Check(_lastEvent != null && _lastEvent.Contains("Опция 6") && _lastEvent.Contains("выбрана"),
                $"выбор при закрытии (наведение до края): {_lastEvent ?? "(нет)"}");
        }
        _menu.Close();
    }

    private void AcquireTarget()
    {
        var cam = GetViewport().GetCamera3D();
        Vector3 fwd = -cam.GlobalTransform.Basis.Z;
        _spawner.SpawnTarget(cam.GlobalPosition + fwd * 100f);
        bool ok = _svc.CycleNext();
        Check(ok && _svc.TargetNode != null, $"цель взята ({_svc.TargetName})");
    }

    private void CommlinkAndFinish()
    {
        _menu.Open();
        bool ok = _menu.TryActivate(RadialMenu.SectorCommlink);
        Check(ok, "с целью сектор «Связь с целью» активировался");
        Check(!_menu.IsOpen, "меню закрылось после активации");
        Check(_lastEvent != null && _lastEvent.Contains("Commlink"),
            $"в комм-логе событие связи: {_lastEvent ?? "(нет)"}");
        Finish();
    }

    private void Check(bool cond, string msg)
    {
        GD.Print((cond ? "  ok   " : "  FAIL ") + msg);
        if (!cond) _failures++;
    }

    private static T FindNode<T>(Node root) where T : Node
    {
        if (root is T t) return t;
        foreach (var c in root.GetChildren())
        {
            var r = FindNode<T>(c);
            if (r != null) return r;
        }
        return null!;
    }

    private void Finish()
    {
        GD.Print((_failures == 0 ? "PROBE PASS" : "PROBE FAIL")
            + $" ProbeRadialMenu: {_failures} провалов");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
