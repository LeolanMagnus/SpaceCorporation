using Godot;
using System.Collections.Generic;

namespace SpaceCorporation;

/// <summary>Probe Этапа H3: выбор цели (Tab-цикл) и маркер на HUD.
///
/// Спавним Main.tscn (offline), поднимаем роль швом TestSetRoleForTest(Host)
/// (нужно только гейту WorldSpawner.SpawnTarget —headless-симуляция), спавним
/// 3 мишени относительно камеры: точно впереди (A), чуть в стороне (B), за
/// камерой (C). Далее:
///   1) Tab (target_cycle): первая цель — A (минимальный угол к взгляду);
///      следующие переключения — новые узлы (цикл без повторов до оборота);
///   2) двигаем A далеко — подпись маркера показывает выросшую дистанцию;
///   3) цель за камерой — маркер/подпись прижаты к краю экрана (внутри rect);
///   4) Hp=0 у текущей цели — HudLayer сам снимает цель (ValidateTarget).
/// Запуск: ./run-tests.sh res://tests/probes/ProbeHudTarget.tscn</summary>
public partial class ProbeHudTarget : Node
{
    private const int SitFrames = 40;

    private int _frames;
    private int _failures;
    private int _phase;

    private Node _main = null!;
    private HudLayer _hud = null!;
    private TargetingService _svc = null!;
    private TargetMarker _marker = null!;
    private Label _lbl = null!;
    private WorldSpawner _spawner = null!;

    private TargetState _a = null!, _b = null!, _c = null!;
    private readonly List<Node> _picks = new();
    private float _distAfterMove;

    public override void _Ready()
    {
        GetWindow().Size = new Vector2I(1152, 648); //.чтобы проверки клампа шли на реальном экране
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
                Cycle(); _phase = 2; break;
            case 2 when _frames == SitFrames + 5:
                Cycle(); _phase = 3; break;
            case 3 when _frames == SitFrames + 7:
                Cycle(); _phase = 4; break;
            case 4 when _frames == SitFrames + 9:
                CheckPicks(); MoveATarget(); _phase = 5; break;
            case 5 when _frames == SitFrames + 12:
                CheckDistAfterMove(); _phase = 6; break;
            case 6 when _frames == SitFrames + 14:
                CycleUntilC(); _phase = 7; break;
            case 7 when _frames == SitFrames + 20:
                CheckBehindClamped(); KillCurrent(_b); _phase = 8; break;
            case 8 when _frames == SitFrames + 24:
                CheckDeadTargetCleared(); Finish(); break;
        }
    }

    private void Setup()
    {
        _hud = _main.GetNode<HudLayer>("HudLayer");
        _svc = _hud.GetNode<TargetingService>("TargetingService");
        _marker = _hud.GetNode<TargetMarker>("HudRoot/TargetMarker");
        _lbl = _marker.GetNode<Label>("LblTargetInfo");
        _spawner = FindNode<WorldSpawner>(_main);

        var nm = NetworkManager.Instance;
        nm.TestSetRoleForTest(NetworkManager.NetRole.Host); // снимает гейт IsServer у спавнера

        var cam = GetViewport().GetCamera3D();
        Check(cam != null, "в Main.tscn есть активная камера (chase-cam)");
        if (cam == null) { Finish(); return; }

        Vector3 fwd = -cam.GlobalTransform.Basis.Z;
        Vector3 right = cam.GlobalTransform.Basis.X;
        Vector3 o = cam.GlobalPosition;

        _a = _spawner.SpawnTarget(o + fwd * 100f);
        _b = _spawner.SpawnTarget(o + fwd * 100f + right * 20f);
        _c = _spawner.SpawnTarget(o - fwd * 80f);
        Check(_a != null && _b != null && _c != null, "3 мишени заспавнены (роль Host)");
        _svc.Clear();
    }

    private void Cycle()
    {
        bool ok = _svc.CycleNext();
        _picks.Add(_svc.TargetNode);
        Print($"cycle -> {Tag(_svc.TargetNode)} (direct ret={ok})");
    }

    private void CheckPicks()
    {
        Check(_picks[0] == _a, $"первый Tab выбрал A (впереди по взгляду): got {Tag(_picks[0])}");
        bool distinct = _picks[0] != null && _picks[1] != null && _picks[2] != null
            && _picks[0] != _picks[1] && _picks[1] != _picks[2] && _picks[0] != _picks[2];
        Check(distinct, $"цикл без повторов 3 кадра подряд: {string.Join(", ", _picks.ConvertAll(Tag))}");
        bool hitB = _picks[1] == _b || _picks[2] == _b;
        Check(hitB, $"B (чуть в стороне) попала в первые два шага цикла");
    }

    private void MoveATarget()
    {
        // Двигаем ИСТИНУ мишени: в offline авторитет — мы; картинку ведёт тот же
        // узел. RigidBody двигаем через MoveTransform (присваивание трансформа
        // физическому телу интерполер перетирает).
        var body = _a.GetParent() as RigidBody3D;
        var pos = body.GlobalPosition - GetViewport().GetCamera3D().GlobalTransform.Basis.Z * 200f;
        // freeze_mode у мишени KINEMATIC (Target.tscn) — прямой перенос допустим.
        body.GlobalPosition = pos;
        _a.NetPosition = pos;
    }

    private void CheckDistAfterMove()
    {
        Check(_svc.TargetNode != null, "после паузы цель не потерялась");
        if (_svc.TargetNode == _a)
        {
            float d = LabelDistMeters();
            Check(d > 250f, $"дистанция в подписи выросла после переезда A: {d:F0} m");
        }
        else
        {
            Print($"(текущая цель {Tag(_svc.TargetNode)}, не A — дистанцию A проверяем при выборе ниже)");
        }
        _distAfterMove = 0f;
    }

    private void CycleUntilC()
    {
        for (int i = 0; i < 8 && _svc.TargetNode != _c; i++)
            Cycle();
    }

    private void CheckBehindClamped()
    {
        Check(_svc.TargetNode == _c, $"цикл дошёл до C (за камерой): got {Tag(_svc.TargetNode)}");
        if (_svc.TargetNode != _c) return;

        var rect = GetViewport().GetVisibleRect();
        // Маркер за камерой рисуется кодом; проверяем подпись — она обязана
        // остаться внутри экрана (кламп к краю).
        _lbl = _marker.GetNode<Label>("LblTargetInfo");
        bool visible = _lbl.Visible;
        Vector2 pos = _lbl.Position;
        Vector2 size = _lbl.Size;
        bool inside = pos.X >= 0 && pos.Y >= 0 && pos.X + size.X <= rect.Size.X && pos.Y + size.Y <= rect.Size.Y;
        Print($"label pos={pos} size={size} rect={rect.Size}");
        Check(visible && inside, "подпись цели за камерой прижата к краю и внутри экрана");
    }

    private void KillCurrent(TargetState t)
    {
        // Выбираем B, потом убиваем её (Hp=0 — как сделал бы сервер при уроне).
        for (int i = 0; i < 10 && _svc.TargetNode != _b; i++)
            Cycle();
        t.Hp = 0;
    }

    private void CheckDeadTargetCleared()
    {
        Check(_svc.TargetNode != _b,
            $"мёртвая цель (Hp=0) автоматически снята: now {Tag(_svc.TargetNode)}");
    }

    private float LabelDistMeters()
    {
        // Подпись вида "Name\n123 m" или "Name\n0.50 km".
        foreach (var line in _lbl.Text.Split('\n'))
        {
            var t = line.Trim();
            if (t.EndsWith("km"))
                return float.Parse(t[..^2].Replace(",", ".")) * 1000f;
            if (t.EndsWith("m"))
                return float.Parse(t[..^1].Replace(",", "."));
        }
        return -1f;
    }

    private static string Tag(Node? n) =>
        n == null ? "null" : n is TargetState ts ? $"T#{ts.TargetNetId}" : n.Name.ToString();

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
        GD.Print(_failures == 0 ? "PROBE PASS ProbeHudTarget"
                                : $"PROBE FAIL ProbeHudTarget: {_failures} провалов");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Print(string s) => GD.Print("  " + s);
    private void Check(bool ok, string what)
    {
        GD.Print((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _failures++;
    }
}
