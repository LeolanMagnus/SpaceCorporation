using Godot;

namespace SpaceCorporation;

/// <summary>
/// HUD-слой в стиле X4 (hud-plan.md). Живёт на CanvasLayer layer=5 — ниже
/// DebugLayer (layer=10), чтобы F3-панель никогда не перекрывалась HUD.
/// Всё дерево UI описано в scenes/HudLayer.tscn — код только связывает и
/// обновляет (правило проекта, как у DebugLayer).
///
/// Слоты (по композиционной сетке X4):
///   Center/Crosshair      — прицельная марка (рисуется кодом, Crosshair.cs);
///   BottomLeft/StatusPanel— статус-бар своего корабля (Этап H2);
///   BottomRight/RadarPanel— мини-карта: ПОКА заглушка, радар — по radar-plan;
///   TopLeft/WorldTag      — режим мира / сеть (Этап H6);
///   TopRight/ObjectivePanel — цель и задача (Этап H6).
///
/// Хоткей H (`hud_toggle`) — полностью спрятать/показать HUD.
/// Все панели прижаты к углам якорями и ограничены по ширине с autowrap —
/// элементы влезают в экран любого размера, текст переносится.
/// </summary>
public partial class HudLayer : CanvasLayer
{
    private Control _hudRoot = null!;

    // Слоты-заглушки H1 — публичные, чтобы Этапы H2/H3/H6 заполняли их, не трогая сцену.
    private Label _lblWorldTag = null!;
    private Label _lblObjective = null!;

    // Радар (radar-plan, R2): источник контактов и панель-виджет.
    private RadarSource _radarSource = null!;
    private RadarPanel _radarView = null!;

    // Статус-бар (H2): узлы из сцены.
    private Label _lblShipClass = null!;
    private ProgressBar _barHp = null!;
    private Label _lblHp = null!;
    private ProgressBar _barShield = null!;
    private Label _lblShield = null!;
    private Label _lblSpeed = null!;
    private Label _lblModes = null!;

    // Целеуказание (H3): сервис и маркер из сцены.
    private TargetingService _targeting = null!;
    private TargetMarker _targetMarker = null!;

    // Комм-лог (H6): 6 слотов Label, кольца событий HudEventBus + возраст строк.
    private readonly Label[] _commLabels = new Label[6];
    private readonly double[] _commAge = new double[6];
    private int _commCount;          //.сколько слотов занято (0..6), 0 — самый свежий

    /// <summary>Видим ли HUD (хоткей H).</summary>
    public bool HudVisible { get; private set; } = true;

    public override void _Ready()
    {
        _hudRoot = GetNode<Control>("HudRoot");

        _lblWorldTag = GetNode<Label>("HudRoot/TopLeft/WorldTag/LblWorldTag");
        _lblObjective = GetNode<Label>("HudRoot/TopRight/ObjectivePanel/LblObjective");

        _lblShipClass = GetNode<Label>("HudRoot/BottomLeft/StatusPanel/StatusBox/ShipClass/LblShipClass");
        _barHp = GetNode<ProgressBar>("HudRoot/BottomLeft/StatusPanel/StatusBox/Bars/HpBox/HpBar");
        _lblHp = GetNode<Label>("HudRoot/BottomLeft/StatusPanel/StatusBox/Bars/HpBox/LblHp");
        _barShield = GetNode<ProgressBar>("HudRoot/BottomLeft/StatusPanel/StatusBox/Bars/ShieldBox/ShieldBar");
        _lblShield = GetNode<Label>("HudRoot/BottomLeft/StatusPanel/StatusBox/Bars/ShieldBox/LblShield");
        _lblSpeed = GetNode<Label>("HudRoot/BottomLeft/StatusPanel/StatusBox/SpeedBox/LblSpeed");
        _lblModes = GetNode<Label>("HudRoot/BottomLeft/StatusPanel/StatusBox/ModeBox/LblModes");

        _targeting = GetNode<TargetingService>("TargetingService");
        _targetMarker = GetNode<TargetMarker>("HudRoot/TargetMarker");

        // Комм-лог (H6): слоты + подписка на шину событий.
        for (int i = 0; i < _commLabels.Length; i++)
            _commLabels[i] = GetNode<Label>($"HudRoot/TopRight/CommlinkPanel/CommlinkBox/LblComm{i}");
        if (HudEventBus.Instance != null)
        {
            HudEventBus.Instance.OnEvent += CommPush;
            // Поздний подписчик не теряет последние строки: снимаем кольцо шины.
            foreach (var s in HudEventBus.Instance.Snapshot())
                CommPush(s);
        }

        ApplyTheme();
        SetPlaceholderTexts();
    }

    /// <summary>Один источник стиля для всех панелей — HudTheme (правило §4).</summary>
    private void ApplyTheme()
    {
        SetPanelStyle("HudRoot/TopLeft/WorldTag");
        SetPanelStyle("HudRoot/TopRight/ObjectivePanel");
        SetPanelStyle("HudRoot/BottomLeft/StatusPanel");

        var radarPanel = GetNode<PanelContainer>("HudRoot/BottomRight/RadarPanel");
        radarPanel.AddThemeStyleboxOverride("panel", HudTheme.PanelStyle());

        // Радар (radar-plan R2): источник контактов кормит панель-виджет;
        // клики панели выбирают цель через TargetingService.
        _radarSource = GetNode<RadarSource>("RadarSource");
        _radarView = GetNode<RadarPanel>("HudRoot/BottomRight/RadarPanel/RadarBox/RadarView");
        _radarView.Source = _radarSource;
        _radarView.Targeting = _targeting;
        _radarSource.TargetingServiceRef = _targeting;
    }

    private void SetPanelStyle(string path)
    {
        var p = GetNode<PanelContainer>(path);
        p.AddThemeStyleboxOverride("panel", HudTheme.PanelStyle());
    }

    /// <summary>Тексты-заглушки H1: показывают сетку HUD до прихода данных.</summary>
    private void SetPlaceholderTexts()
    {
        _lblWorldTag.Text = "OFFLINE";
        _lblObjective.Text = "No target";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("hud_toggle"))
        {
            ToggleHud();
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("target_cycle"))
        {
            // Tab — цикл целей (H3). Сервис живёт рядом на HudLayer.
            _targeting.CycleNext();
            _targetMarker.QueueRedraw();
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("hud_labels"))
        {
            // L — показать/скрыть подписи целей (H3).
            _targetMarker.LabelsOn = !_targetMarker.LabelsOn;
            _targetMarker.QueueRedraw();
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventKey k && k.Pressed && k.Keycode == Key.R
                 && k.ShiftPressed)
        {
            // Shift+R — перебор режимов дальности радара (Near→Mid→Long→Auto).
            // Проверяем ДО hud_radar: одиночная R скрывает панель.
            _radarView.CycleMode();
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("hud_radar"))
        {
            // R — показать/скрыть радар (radar-plan R2). Прямо всю панель
            // BottomRight: и кольцо, и подпись «RADAR» — как переключение окна.
            var panel = GetNode<Control>("HudRoot/BottomRight/RadarPanel");
            panel.Visible = !panel.Visible;
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>H: полностью спрятать/показать HUD (не трогаем DebugLayer).</summary>
    public void ToggleHud()
    {
        HudVisible = !HudVisible;
        _hudRoot.Visible = HudVisible;
    }

    public override void _Process(double delta)
    {
        if (!HudVisible)
            return;

        UpdateStatusPanel();
        UpdateWorldTag();
        UpdateObjective();
        UpdateCommlink(delta);

        // H3: цель могла умереть/исчезнуть — снимаем её (маркер сам пустеет).
        if (_targeting.TargetNode != null && !_targeting.ValidateTarget())
        {
            // H6: смерть цели — событие в комм-лог (до Clear — имя ещё живо).
            HudEventBus.Report($"Target lost: {_targeting.TargetName}");
            _targeting.Clear();
        }
    }

    // ─────────────── Этап H2: статус-бар своего корабля ───────────────

    /// <summary>Корабль локального игрока: ищем по точке крепления Seat, к которой
    /// пристёгнут локальный Player (Player.GetMountedShip() — сущностный аналог
    /// из hud-plan: игрок сидит в кресле, значит его корабль — владелец кресла).</summary>
    private Ship? _myShip;
    private bool _myShipResolved;

    /// <summary>Локальный игрок (узел Player с авторитетом этого процесса).</summary>
    private Player? FindLocalPlayer()
    {
        if (!Multiplayer.HasMultiplayerPeer())
        {
            // Offline: единственный Player в сцене — локальный.
            foreach (var n in GetTree().GetNodesInGroup("player"))
                if (n is Player p && GodotObject.IsInstanceValid(p))
                    return p;
            return null;
        }
        int localId = Multiplayer.GetUniqueId();
        foreach (var n in GetTree().GetNodesInGroup("player"))
            if (n is Player p && GodotObject.IsInstanceValid(p) && p.GetMultiplayerAuthority() == localId)
                return p;
        return null;
    }

    /// <summary>Кэш «своего» корабля: игрок пристёгнут к Seat → владелец точки (Ship)
    /// и есть корабль пилота. Инвалидируется, если игрок отстегнулся или корабль умер.</summary>
    private Ship? GetMyShip()
    {
        var player = FindLocalPlayer();
        if (player?.AttachedTo is AttachPoint seat && seat.Kind == AttachPoint.KindOf.Seat)
        {
            var ship = FindOwnerShip(seat);
            if (ship != null && GodotObject.IsInstanceValid(ship))
            {
                _myShip = ship;
                _myShipResolved = true;
                return ship;
            }
        }
        // Игрок без корабля: если кэш жил, а игрока рядом нет — сбрасываем раз в кадр.
        if (_myShip != null && !GodotObject.IsInstanceValid(_myShip))
            _myShip = null;
        return _myShip;
    }

    /// <summary>Ближайший предок-Ship у точки крепления (как в AttachPoint.ResolveOwnerNetId).</summary>
    private static Ship? FindOwnerShip(Node n)
    {
        for (Node c = n; c != null; c = c.GetParent())
            if (c is Ship s)
                return s;
        return null;
    }

    private void UpdateStatusPanel()
    {
        var ship = GetMyShip();
        if (ship == null)
        {
            _lblShipClass.Text = "—";
            _lblHp.Text = "-- / --";
            _barHp.Value = 0;
            _lblShield.Text = "0 / 0";
            _barShield.Value = 0;
            _lblSpeed.Text = "0 m/s";
            _lblModes.Text = "";
            return;
        }

        var health = ship.GetNodeOrNull<ShipHealthState>("ShipHealthState");
        var net = ship.GetNodeOrNull<NetState>("NetState");

        // Класс корабля — из типа (реплицируемое byte-поле, без обращений к чужим нодам).
        var spec = ShipStatCatalog.Find(
            (ShipType)(net?.ShipTypeValue ?? (byte)ShipType.Courier));
        _lblShipClass.Text = spec.DisplayName;

        // HP: полоса + число, цвет по HudTheme.HpColor.
        if (health != null && health.MaxHp > 0)
        {
            float ratio = Mathf.Clamp((float)health.Hp / health.MaxHp, 0f, 1f);
            _barHp.Value = ratio;
            _barHp.Modulate = HudTheme.HpColor(ratio);
            _lblHp.Text = $"{health.Hp} / {health.MaxHp}";
            _lblHp.Modulate = HudTheme.HpColor(ratio);
        }

        // Shield — слот-заглушка H2 (Этап H7 решит наполнение).
        _barShield.Value = 0;
        _lblShield.Text = "0 / 0";

        // Скорость: из реплицированного NetLinearVelocity; максимум — из полётной модели.
        var velocity = net?.NetLinearVelocity ?? Vector3.Zero;
        float speed = velocity.Length();
        float maxSpeed = ship.Flight.MaxLinearSpeed;
        _lblSpeed.Text = maxSpeed > 0f
            ? $"{speed:F0} m/s\nmax {maxSpeed:F0}"
            : $"{speed:F0} m/s";

        // Режимные иконки текстом (X4-подобные аббревиатуры): boost / assist / camera.
        // BOOST — удерживается ли клавиша сейчас (локальный ввод); ASSIST —
        // реплицируемый флаг PilotAssist; CAM — локальный вид пилота (V).
        string boost = Input.IsActionPressed("boost") ? "BOOST" : "boost";
        string assist = (net?.PilotAssist ?? 1) == 1 ? "ASSIST" : "assist-off";
        string camera = ship.PilotChaseView ? "CHASE" : "COCKPIT";
        _lblModes.Text = $"{boost}\n{assist}\n{camera}";
    }

    // ─────────────── Этап H6: тег мира (левый верх) ───────────────
    // Две строки X4: роль мира (OFFLINE / SERVER / SERVER+CLIENT / CLIENT +
    // пиры) и вторая строка — ping до сервера (- при offline).

    private string _worldText = "";

    private void UpdateWorldTag()
    {
        var nm = NetworkManager.Instance;
        string role = nm switch
        {
            null => "OFFLINE",
            _ => nm.Role switch
            {
                NetworkManager.NetRole.Offline => "OFFLINE",
                NetworkManager.NetRole.Dedicated => "SERVER",
                NetworkManager.NetRole.Host => "SERVER+CLIENT",
                NetworkManager.NetRole.Client => "CLIENT",
                _ => "?",
            },
        };
        int peers = nm != null && (nm.IsServer || nm.IsClient) ? Multiplayer.GetPeers().Length : 0;
        string line1 = nm != null && (nm.IsServer || nm.IsClient) ? $"{role} · {peers}p" : role;
        int ping = nm?.PingMs ?? -1;
        string line2 = ping >= 0 ? $"ping {ping} ms" : "world: local";
        string text = $"{line1}\n{line2}";
        if (text != _worldText)
        {
            _worldText = text;
            _lblWorldTag.Text = text;
        }
    }

    // ───────── Правый верх: строка цели/задачи (H6) ─────────
    // «On: {target} — {dist}» от TargetingService (дистанция от камеры до мировой
    // позиции цели); при отсутствии цели — «No target». Своей игровой задачи в
    // игре ещё нет — роль objective выполняет текущая цель прицела; при
    // появлении задачи (квестовая серия) подмена — одной точкой в этом методе.

    /// <summary>Формат строки цели (пробы сверяются с ним же).</summary>
    public static string FormatObjective(string targetName, float distance) =>
        $"On: {targetName} \u2014 {distance:F0} m";

    private string _objectiveText = "";

    private void UpdateObjective()
    {
        string line;
        if (_targeting.TargetNode != null &&
            TargetingService.WorldPosOf(_targeting.TargetNode, out Vector3 pos))
        {
            var cam = GetViewport()?.GetCamera3D();
            float dist = cam != null ? cam.GlobalPosition.DistanceTo(pos) : 0f;
            line = FormatObjective(_targeting.TargetName, dist);
        }
        else
        {
            line = "No target";
        }
        if (line != _objectiveText)
        {
            _objectiveText = line;
            _lblObjective.Text = line;
        }
    }

    // ─────── Правый верх: комм-лог (H6, шина HudEventBus) ───────
    // 6 слотов, свежее сверху. Строка живёт HoldSeconds полной яркостью, затем
    // гаснет за FadeSeconds, после чего слот освобождается (список уплотняется).

    /// <summary>Новое событие в лог: слот 0, остальные сдвигаются (6-я строка
    /// теряется — кольцевой буфер самой шины длиннее, 16).</summary>
    private void CommPush(string message)
    {
        for (int i = _commLabels.Length - 1; i > 0; i--)
        {
            _commLabels[i].Text = _commLabels[i - 1].Text;
            _commAge[i] = _commAge[i - 1];
        }
        _commLabels[0].Text = message;
        _commAge[0] = 0.0;
        if (_commCount < _commLabels.Length)
            _commCount++;
    }

    private void UpdateCommlink(double delta)
    {
        double total = HudEventBus.HoldSeconds + HudEventBus.FadeSeconds;
        for (int i = 0; i < _commLabels.Length; i++)
        {
            var lbl = _commLabels[i];
            if (i >= _commCount)
            {
                if (lbl.Visible)
                    lbl.Visible = false;
                continue;
            }
            _commAge[i] += delta;
            double age = _commAge[i];
            if (age > total)
            {
                // Строка померкла — уплотняем список (свежие остаются плотной стопкой).
                lbl.Visible = false;
                for (int k = i; k < _commCount - 1; k++)
                {
                    _commLabels[k].Text = _commLabels[k + 1].Text;
                    _commAge[k] = _commAge[k + 1];
                }
                _commCount--;
                i--; //.пересмотреть этот же индекс: сюда встал следующий слот
                continue;
            }
            float alpha = age <= HudEventBus.HoldSeconds
                ? 1f
                : 1f - (float)((age - HudEventBus.HoldSeconds) / HudEventBus.FadeSeconds);
            lbl.Visible = true;
            lbl.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(alpha, 0f, 1f));
        }
    }
}
