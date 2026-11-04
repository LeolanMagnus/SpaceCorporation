using Godot;

namespace SpaceCorporation;

/// <summary>
/// Probe-проверка раскладки HUD (требование к Этапу H1/H2): элементы HUD
/// влезают в экран при любом размере окна, текст переносится (autowrap),
/// хоткей H прячет/показывает HUD, конфликта с DebugLayer нет (слой 5 < 10).
/// Запуск: сцена tests/probes/ProbeHudLayout.tscn. Печатает PROBE PASS/FAIL.
/// </summary>
public partial class ProbeHudLayout : Node
{
    private HudLayer _hud = null!;
    private int _stageFrame; // кадры внутри текущей стадии
    private int _failures;
    private int _stage; // 0 = ждём layout, 1 = проверили 1152x648, 2 = проверили 800x480, 3 = финал

    private static readonly (string Path, string Name)[] Panels =
    [
        ("HudRoot/TopLeft/WorldTag", "WorldTag"),
        ("HudRoot/TopRight/ObjectivePanel", "ObjectivePanel"),
        ("HudRoot/BottomLeft/StatusPanel", "StatusPanel"),
        ("HudRoot/BottomRight/RadarPanel", "RadarPanel"),
    ];

    public override void _Ready()
    {
        var scene = GD.Load<PackedScene>("res://scenes/HudLayer.tscn");
        _hud = scene.Instantiate<HudLayer>();
        AddChild(_hud);

        GetWindow().Size = new Vector2I(1152, 648); // дефолтный размер проекта
    }

    public override void _Process(double delta)
    {
        _stageFrame++;

        switch (_stage)
        {
            case 0:
                if (_stageFrame < 3) return; // дали HUD собрать layout
                CheckPanelsFit("1152x648");
                CheckTextWraps();
                GetWindow().Size = new Vector2I(800, 480);
                NextStage();
                break;
            case 1:
                if (_stageFrame < 4) return; // кадры на пересчёт layout после ресайза
                CheckPanelsFit("800x480");
                GetWindow().Size = new Vector2I(1152, 648);
                NextStage();
                break;
            case 2:
                if (_stageFrame < 4) return;
                Finish();
                break;
        }
    }

    private void NextStage() { _stage++; _stageFrame = 0; }

    private void CheckPanelsFit(string tag)
    {
        var screen = _hud.GetViewport().GetVisibleRect();
        foreach (var (path, name) in Panels)
        {
            var c = _hud.GetNodeOrNull<Control>(path);
            if (c == null) { Fail($"{name}: узел не найден"); continue; }
            var r = c.GetGlobalRect();
            bool fits = r.Position.X >= -0.5f && r.Position.Y >= -0.5f &&
                        r.End.X <= screen.Size.X + 0.5f && r.End.Y <= screen.Size.Y + 0.5f;
            Check(fits, $"{name}@{tag} rect {r} в экране {screen.Size}");
        }
    }

    private void CheckTextWraps()
    {
        foreach (var (path, name) in Panels)
        {
            var c = _hud.GetNodeOrNull<Control>(path);
            if (c == null) continue;
            foreach (Label l in EnumerateLabels(c))
            {
                if (l.Text.Length > 12 && l.AutowrapMode == TextServer.AutowrapMode.Off
                    && !l.Text.Contains('\n'))
                {
                    // Короткие заголовки-капсы (HULL/SPEED…) переносить не обязательно,
                    // но любые «предложения» — обязаны.
                    Fail($"{name}/{l.Name}: текст длинный и autowrap выключен: \"{l.Text}\"");
                }
                // Высота label под текст не больше панели (перенос реально работает).
                if (l.GetGlobalRect().End.Y > c.GetGlobalRect().End.Y + 0.5f)
                    Fail($"{name}/{l.Name}: текст Label ниже панели (перенос не помог)");
            }
        }
    }

    private void Finish()
    {
        // Слои: HUD (5) ниже DebugLayer (10) — конфликта нет.
        Check(_hud.Layer == 5, $"HudLayer.Layer = {_hud.Layer} (ожидался 5)");

        // Хоткей H: скрытие/показ.
        _hud.ToggleHud();
        Check(!_hud.HudVisible && !_hud.GetNode<Control>("HudRoot").Visible, "H скрыл HUD");
        _hud.ToggleHud();
        Check(_hud.HudVisible && _hud.GetNode<Control>("HudRoot").Visible, "H показал HUD");

        GD.Print(_failures == 0 ? "PROBE PASS ProbeHudLayout"
                                : $"PROBE FAIL ProbeHudLayout: {_failures} провалов");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private static System.Collections.Generic.IEnumerable<Label> EnumerateLabels(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            if (c is Label l) yield return l;
            foreach (var d in EnumerateLabels(c)) yield return d;
        }
    }

    private void Check(bool ok, string what)
    {
        GD.Print((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _failures++;
    }

    private void Fail(string what) => Check(false, what);
}
