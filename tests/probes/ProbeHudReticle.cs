using Godot;

namespace SpaceCorporation;

/// <summary>
/// Проба шага 8 (ручная/живая проверка HUD-прицела пилотирования 2.0) без
/// скриншота: вместо пикселей проверяем, что новый слой прицела реально
/// встроен в живой HUD и живёт по нужным правилам.
///
/// Прогоняем НАСТОЯЩИЙ <c>scenes/Main.tscn</c> (offline) — тот самый, что
/// запускается в игре, — и проверяем четыре вещи, которые скриншот доказывал
/// бы глазами:
///
///   1) <c>HudLayer._Ready()</c> нашёл узел <c>HudRoot/AimReticle</c> — иначе
///      он бы упал на GetNode и HUD вообще бы не собрался (это же закрыто
///      косвенно ProbeHudTop, но здесь мы держим ссылку и меряем дальше);
///   2) марка прицела растянута якорями на весь экран: её <c>Size</c> равен
///      размеру вьюпорта (иначе центр <c>Size/2</c> в <c>_Draw</c> съедет);
///   3) гейт <c>ShowAim</c>: на пристёгнутом корабле локального пирa (он в
///      <c>Main.tscn> автопристёгнут к креслу) марка горит — HudLayer в
///      <c>_Process</c> ставит <c>ShowAim = IsLocalPilot &amp;&amp;
///      !IsInteractionMode</c>;
///   4) гейт гаснет в режиме взаимодействия и возвращается в режиме пилота —
///      переключаем <c>SetInteractionMode</c> на своём корабле и на следующих
///      кадрах читаем, что HudLayer переключил <c>ShowAim</c> за нами.
///
/// Это не проверка конкретных пикселей (в headless окно нет вывода), а проверка
/// схемы и живого конвейера «корабль → HudLayer → AimReticle». Запуск:
/// <c>./run-tests.sh res://tests/probes/ProbeHudReticle.tscn</c>
/// </summary>
public partial class ProbeHudReticle : Node
{
    private const int SitFrames = 30;   //.дать Main устроиться и HudLayer отработать _Process

    private int _frames;
    private int _failures;
    private int _phase;

    private Node _main = null!;
    private HudLayer _hud = null!;
    private AimReticle _reticle = null!;
    private Ship _myShip = null!;

    public override void _Ready()
    {
        GD.Print("=== ProbeHudReticle ===");
        GetWindow().Size = new Vector2I(1152, 648);
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
            case 1 when _frames == SitFrames + 2:
                CheckNoseAlwaysOn(); _myShip.SetInteractionMode(true); _phase = 2; break;
            case 2 when _frames == SitFrames + 4:
                CheckInteractionGateOff(); _myShip.SetInteractionMode(false); _phase = 3; break;
            case 3 when _frames == SitFrames + 6:
                CheckPilotGateBackOn(); Finish(); break;
        }
    }

    private void Setup()
    {
        _hud = _main.GetNodeOrNull<HudLayer>("HudLayer");
        Check(_hud != null, "в Main.tscn есть HudLayer");
        if (_hud == null) { Finish(); return; }

        // 1) HudLayer обязан был разрешить узел прицела при своём _Ready: если бы
        // путь в сцене разошёлся, он бы упал — но держим и свою ссылку для замеров.
        _reticle = _hud.GetNodeOrNull<AimReticle>("HudRoot/AimReticle");
        Check(_reticle != null, "HudLayer/HudRoot/AimReticle разрешён (путь в tscn верен)");
        if (_reticle == null) { Finish(); return; }

        // 2) Марка растянута якорями (anchors_preset=15) на весь rect: Size ==
        // размер вьюпорта. Иначе центр (Size/2) в _Draw не совпадёт с центром,
        // от которого Ship отсчитывает нос, — прицел съедет относительно игры.
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        Check(_reticle.Size.DistanceTo(vp) < 2f,
            $"AimReticle растянут на весь экран: size={_reticle.Size} viewport={vp}");
        Check(_reticle.MouseFilter == Control.MouseFilterEnum.Ignore,
            "AimReticle не съедает мышь (MouseFilter=Ignore) — она идёт в Ship/HUD");

        // Найти свой корабль (тот, кем реально рулит локальный пир) — через тот же
        // признак, что и HudLayer: локальный пилот.
        foreach (var (_, ship) in NetworkManager.Instance.Ships)
            if (GodotObject.IsInstanceValid(ship) && ship.IsLocalPilot)
                _myShip = ship;
        Check(_myShip != null, "есть корабль локального пилота (Main автопристёгивает в кресло)");
        if (_myShip == null) { Finish(); return; }

        // 3) Гейт ShowAim на пилотируемом корабле вне режима взаимодействия.
        Check(_reticle.ShowAim,
            "на пилотируемом корабле марка прицела горит (ShowAim=true)");
    }

    private void CheckNoseAlwaysOn()
    {
        // Точка носа рисуется в любом случае — здесь лишь сверяем состояние гейта.
        Check(_reticle.ShowAim, "до взаимодействия: ShowAim=true");
    }

    private void CheckInteractionGateOff()
    {
        // В режиме взаимодействия прицельный слой обязан погаснуть: тот же гейт,
        // что глушит позиционный доворот в Ship (HudLayer читает IsInteractionMode).
        Check(!_myShip.IsInteractionMode || !_reticle.ShowAim,
            "в режиме взаимодействия марка прицела погасла (ShowAim=false)");
        Check(_myShip.IsInteractionMode, "сам корабль действительно в режиме взаимодействия");
    }

    private void CheckPilotGateBackOn()
    {
        Check(!_myShip.IsInteractionMode,
            "возврат из режима взаимодействия выполнен");
        Check(_reticle.ShowAim,
            "обратно в режиме пилота — марка прицела снова горит (ShowAim=true)");
    }

    private void Finish()
    {
        GD.Print(_failures == 0 ? "PROBE PASS ProbeHudReticle"
                                : $"PROBE FAIL ProbeHudReticle: {_failures} провалов");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Check(bool ok, string what)
    {
        GD.Print((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _failures++;
    }
}
