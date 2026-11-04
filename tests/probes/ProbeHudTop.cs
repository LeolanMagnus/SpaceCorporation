using Godot;

namespace SpaceCorporation;

/// <summary>Probe Этапа H6: верхние углы HUD — тег мира, строка цели,
/// комм-лог через шину HudEventBus (заглушка).
///
/// 1) Тег мира: 2 строки, при роли Host — «SERVER+CLIENT · 0p» / ping;
/// 2) Objective: до цели — "No target", после Tab — "On: Target #… — NN m";
/// 3) Комм-лог: событие шины → слот 0 (текст, видимость, alpha=1); 7 событий
///    → показываются только 6 свежих, самое старое вытеснено;
/// 4) Перенос текста: длинное сообщение остаётся в границах панели и экрана
///    (autowrap), панель целиком в видимой области;
/// 5) Fade-out: через HoldSeconds+FadeSeconds+буфер строки гаснут и слоты
///    очищаются (видимость — нет).
/// Запуск: ./run-tests.sh res://tests/probes/ProbeHudTop.tscn</summary>
public partial class ProbeHudTop : Node
{
    private const int SitFrames = 40;

    private int _frames;
    private int _failures;
    private int _phase;
    private double _elapsedSincePush;

    private Node _main = null!;
    private HudLayer _hud = null!;
    private TargetingService _svc = null!;
    private Label _lblWorld = null!, _lblObjective = null!;
    private PanelContainer _commPanel = null!;
    private Label[] _comm = new Label[6];

    public override void _Ready()
    {
        GetWindow().Size = new Vector2I(1152, 648);
        _main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(_main);
    }

    public override void _Process(double delta)
    {
        _frames++;
        _elapsedSincePush += delta;
        switch (_phase)
        {
            case 0 when _frames == SitFrames:
                Setup(); _phase = 1; break;
            case 1 when _frames == SitFrames + 3:
                CheckWorldTag(); _phase = 2; break;
            case 2 when _frames == SitFrames + 5:
                CheckNoTarget(); PushEvents(); _phase = 3; break;
            case 3 when _frames == SitFrames + 7:
                CheckCommlinkFresh(); CheckFit(); _phase = 4; break;
            case 4 when _frames == SitFrames + 10:
                _svc.Clear(); _svc.CycleNext(); _phase = 5; break;
            case 5 when _frames == SitFrames + 12:
                CheckObjectiveAfterCycle(); _phase = 6; break;
            case 6 when _elapsedSincePush > HudEventBus.HoldSeconds + HudEventBus.FadeSeconds + 0.7:
                CheckFadedOut(); break;
        }
    }

    private void Setup()
    {
        _hud = _main.GetNode<HudLayer>("HudLayer");
        _svc = _hud.GetNode<TargetingService>("TargetingService");
        _lblWorld = _hud.GetNode<Label>("HudRoot/TopLeft/WorldTag/LblWorldTag");
        _lblObjective = _hud.GetNode<Label>("HudRoot/TopRight/ObjectivePanel/LblObjective");
        _commPanel = _hud.GetNode<PanelContainer>("HudRoot/TopRight/CommlinkPanel");
        for (int i = 0; i < 6; i++)
            _comm[i] = _hud.GetNode<Label>($"HudRoot/TopRight/CommlinkPanel/CommlinkBox/LblComm{i}");

        NetworkManager.Instance.TestSetRoleForTest(NetworkManager.NetRole.Host);

        var spawner = FindNode<WorldSpawner>(_main);
        var cam = GetViewport().GetCamera3D();
        Check(cam != null, "есть активная камера");
        if (cam == null) { Finish(); return; }
        Vector3 fwd = -cam.GlobalTransform.Basis.Z;
        var t = spawner.SpawnTarget(cam.GlobalPosition + fwd * 120f);
        Check(t != null, "мишень заспавнена (для objective-строки)");
    }

    private void CheckWorldTag()
    {
        string[] lines = _lblWorld.Text.Split('\n');
        Check(lines.Length == 2, $"тег мира — 2 строки: got \"{_lblWorld.Text.Replace("\n", "\\n")}\"");
        Check(lines[0].Contains("SERVER+CLIENT"), $"1-я строка — роль: \"{lines[0]}\"");
        Check(lines[0].Contains("·") && lines[0].Contains("0p"), $"1-я строка со счётом пиров: \"{lines[0]}\"");
        Check(lines.Length > 1 && (lines[1].StartsWith("ping") || lines[1].StartsWith("world")),
              $"2-я строка — ping/мир: \"{(lines.Length > 1 ? lines[1] : "")}\"");
    }

    private void CheckNoTarget()
    {
        _svc.Clear();
        Check(_lblObjective.Text == "No target", $"без цели: «No target», got \"{_lblObjective.Text}\"");
    }

    private void PushEvents()
    {
        _elapsedSincePush = 0.0; //.отсчёт fade — с этого момента
        HudEventBus.Report("first event");
        for (int i = 2; i <= 7; i++)
            HudEventBus.Report($"event {i}");
        // Длинное сообщение — проверка переноса (помещается в панель/экран).
        HudEventBus.Report("long: alpha beta gamma delta echo foxtrot golf hotel india " +
                           "juliet kilo lima mike november oscar papa quebec romeo sierra tango");
    }

    private void CheckCommlinkFresh()
    {
        Check(_comm[0].Visible && _comm[0].Text.StartsWith("long:"),
              $"свежее событие в слоте 0: \"{Cut(_comm[0].Text)}\"");
        int shown = 0;
        for (int i = 0; i < 6; i++)
            if (_comm[i].Visible) shown++;
        Check(shown == 6, $"показаны ровно 6 строк из 8 событий: shown={shown}");
        Check(_comm[5].Text == "event 3",
              $"вытеснены 2 самых старых (слот 5 = «event 3»): got \"{_comm[5].Text}\"");
        Check(_comm[0].Modulate.A > 0.99f, $"alpha свежей строки = 1: {_comm[0].Modulate.A:F2}");
    }

    private void CheckFit()
    {
        var rect = GetViewport().GetVisibleRect();
        var p = _commPanel.GlobalPosition;
        var s = _commPanel.Size;
        Check(p.X >= 0 && p.Y >= 0 && p.X + s.X <= rect.Size.X && p.Y + s.Y <= rect.Size.Y,
              $"панель комм-лога в экране: pos={p} size={s} screen={rect.Size}");
        Check(s.X <= 400, $"панель не шире 400 px (длинный текст перенесён): width={s.X}");
        // Длинная строка не должна выходить за панель по высоте/ширине.
        foreach (var lbl in _comm)
        {
            if (!lbl.Visible) continue;
            Check(lbl.AutowrapMode != TextServer.AutowrapMode.Off, "у строк лога включён autowrap");
            break;
        }
    }

    private void CheckObjectiveAfterCycle()
    {
        _svc.Clear();
        _svc.CycleNext();
        bool ok = _svc.TargetNode != null;
        Check(ok, $"Tab выбрал цель: {_svc.TargetName}");
        // Строка обновляется в _Process HudLayer — проверяем на следующем кадре ниже;
        // здесь только формат, когда текст уже успел обновиться.
        bool formatted = _lblObjective.Text.StartsWith("On: ") && _lblObjective.Text.Contains(" m");
        Check(formatted, $"objective-строка формата «On: … — NN m»: \"{_lblObjective.Text}\"");
    }

    private void CheckFadedOut()
    {
        int shown = 0;
        for (int i = 0; i < 6; i++)
            if (_comm[i].Visible) shown++;
        Check(shown == 0, $"через {HudEventBus.HoldSeconds + HudEventBus.FadeSeconds:F0} с строки померкли и слоты пусты: shown={shown}");
        Finish();
    }

    private static string Cut(string s) => s.Length <= 40 ? s : s[..40] + "…";

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
        GD.Print(_failures == 0 ? "PROBE PASS ProbeHudTop"
                                : $"PROBE FAIL ProbeHudTop: {_failures} провалов");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Check(bool ok, string what)
    {
        GD.Print((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _failures++;
    }
}
