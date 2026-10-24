using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест приёмки этапа 1. Инстанцирует Main.tscn, эмулирует F3 и клики по кнопкам,
/// печатает PASS/FAIL по каждому пункту чек-листа и завершает процесс с кодом 0/1.
/// Запуск: godot --headless --path . res://tests/Stage1Test.tscn
/// </summary>
public partial class Stage1Test : Node
{
    private int _failures;
    private int _checks;

    private DebugLayer _layer = null!;
    private RigidBody3D _probe = null!;
    private RigidBody3D? _ship;
    private Player? _player;
    private Node? _main;
    private CheckButton _chkPause = null!;
    private int _frame;
    private int _nudgeFrame;
    private Vector3 _shipFrozenPos;

    public override void _Ready()
    {
        // Тест должен жить, пока дебаг-панель ставит дерево на паузу.
        ProcessMode = ProcessModeEnum.Always;

        var main = GetNodeOrNull<Node>("Main");
        _main = main;
        if (main == null)
        {
            GD.PrintErr("FAIL: сцена Main не найдена");
            Finish();
            return;
        }

        _layer = main.GetNodeOrNull<DebugLayer>("DebugLayer");
        Check(_layer != null, "DebugLayer присутствует в Main");
        if (_layer == null)
        {
            Finish();
            return;
        }

        // --- физика: глобальная гравитация выключена
        var gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity").AsDouble();
        Check(Mathf.IsZeroApprox(gravity), $"global gravity == 0 (получено {gravity})");

        // --- имена слоёв коллизий
        Check(LayerName(1) == "Ship", $"слой 1 = Ship (получено {LayerName(1)})");
        Check(LayerName(2) == "Station", $"слой 2 = Station (получено {LayerName(2)})");
        Check(LayerName(3) == "Character", $"слой 3 = Character (получено {LayerName(3)})");
        Check(LayerName(4) == "Target", $"слой 4 = Target (получено {LayerName(4)})");

        // --- константы CollisionLayers совпадают с project.godot
        Check(CollisionLayers.Ship == 1 && CollisionLayers.Station == 2 &&
              CollisionLayers.Character == 4 && CollisionLayers.Target == 8 &&
              CollisionLayers.DockingZone == 16 && CollisionLayers.DebugHelper == 32,
              "константы CollisionLayers == степени двойки по номерам слоёв");

        // --- тестовый стенд из Main.tscn на слое Station
        var stand = main.GetNodeOrNull<StaticBody3D>("TestStand");
        Check(stand != null && stand.CollisionLayer == CollisionLayers.Station,
              "TestStand — StaticBody3D на слое Station");
        Check(stand != null && stand.SceneFilePath == "res://scenes/TestStand.tscn",
              $"TestStand — инстан сцены TestStand.tscn (получено {stand?.SceneFilePath})");

        // --- шаг 1.6: Main.tscn собрана только из импортированных сцен
        Check(main.GetNodeOrNull<Node>("Environment")?.SceneFilePath == "res://scenes/Environment.tscn",
              "Environment — инстан сцены Environment.tscn");
        Check(main.GetNodeOrNull<Node>("DebugLayer")?.SceneFilePath == "res://scenes/DebugLayer.tscn",
              "DebugLayer — инстан сцены DebugLayer.tscn");
        Check(main.GetNodeOrNull<Camera3D>("Camera3D") == null &&
              main.GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D") == null,
              "в корне Main нет самодельных узлов камеры/света — всё вынесено в сцены");

        // --- шаг 4 переделки (§3.6): Player и Ship больше НЕ лежат в Main.tscn —
        // их спавнит WorldSpawner (GameRoot + MultiplayerSpawner). Спавн в offline
        // происходит синхронно в _Ready цепочки сцен, но тест всё равно ищет их
        // через GameRoot и проверяет, что статичных инстансов в сцене нет (§4 В4=B).
        var gameRoot = main.GetNodeOrNull<Node>("GameRoot");
        Check(gameRoot != null, "в Main есть GameRoot (спавнер объектов мира, §3.6)");
        Check(gameRoot?.GetNodeOrNull<MultiplayerSpawner>("Spawner") != null,
              "у GameRoot есть дочерний MultiplayerSpawner «Spawner»");
        Check(main.GetNodeOrNull<Node>("Ship") == null && main.GetNodeOrNull<Node>("Player") == null,
              "в корне Main нет статичных Ship/Player — их спавнит код (§4 В4=B)");

        var spawnPoints = main.GetNodeOrNull<Node3D>("SpawnPoints");
        Check(spawnPoints != null, "в Main есть SpawnPoints (набор стартовых точек, §3.6)");
        int spawnMarkers = 0;
        if (spawnPoints != null)
            foreach (var c in spawnPoints.GetChildren())
                if (c is Marker3D && c.Name.ToString().StartsWith("Spawn"))
                    spawnMarkers++;
        Check(spawnMarkers >= 1,
              $"SpawnPoints содержит >= 1 Marker3D вида Spawn<N> (получено {spawnMarkers})");

        _player = gameRoot?.GetNodeOrNull<Player>("Player");
        Check(_player != null, "Player заспавнен в GameRoot");
        if (_player != null)
        {
            Check(_player.SceneFilePath == "res://scenes/Player.tscn",
                  $"Player — инстан сцены Player.tscn (получено {_player.SceneFilePath})");
            Check(_player is CharacterBody3D, "Player — CharacterBody3D (капсула)");
            var cam = _player.GetActiveCamera();
            Check(cam != null, "в сцене Player есть Camera3D");
            Check(cam != null && cam.Current, "камера игрока — активная (Current)");
            Check(cam != null && cam.GetParent() == _player.GetNodeOrNull<Node3D>("CameraRig") &&
                  _player.GetNodeOrNull<Node3D>("CameraRig")?.GetParent() == _player,
                  "камера — потомок капсулы игрока (привязана к игроку)");
            Check(_player.CollisionLayer == CollisionLayers.Character,
                  $"Player: слой Character (получено {_player.CollisionLayer})");
            Check(_player.CollisionMask == (CollisionLayers.Station | CollisionLayers.Target),
                  $"Player: маска Station|Target без Ship (получено {_player.CollisionMask})");
        }

        // --- шаг 1.5 + шаг 4: корабль спавнится через GameRoot (не инстан в Main.tscn)
        _ship = gameRoot?.GetNodeOrNull<RigidBody3D>("Ship");
        Check(_ship != null, "Ship (RigidBody3D) заспавнен в GameRoot");
        if (_ship != null)
        {
            Check(_ship.SceneFilePath == "res://scenes/Ship.tscn",
                  $"Ship — инстан сцены Ship.tscn (получено {_ship.SceneFilePath})");
            Check(Mathf.IsZeroApprox(_ship.GravityScale), "Ship: gravity_scale = 0");
            Check(Mathf.IsZeroApprox(_ship.LinearDamp), "Ship: linear_damp = 0");
            Check(Mathf.IsEqualApprox(_ship.AngularDamp, 1.5f),
                  $"Ship: angular_damp = 1.5 (получено {_ship.AngularDamp})");
            Check(_ship.CollisionLayer == CollisionLayers.Ship,
                  $"Ship: слой Ship (получено {_ship.CollisionLayer})");
            Check(_ship.CollisionMask == (CollisionLayers.Station | CollisionLayers.Target),
                  $"Ship: маска Station|Target (получено {_ship.CollisionMask})");
            Check(_ship.GetNodeOrNull<Node3D>("Muzzle") != null,
                  "у корабля есть нос Muzzle (пригодится на этапе 3)");
            // Точки крепления переехали под картинку ShipHull (§3.3 переделки стадии 1):
            // игрок в кабине обязан совпадать с картинкой, а не с физическим корпусом.
            Check(_ship.GetNodeOrNull<AttachPoint>("ShipHull/AttachPoints/Seat") != null,
                  "у корабля есть точка крепления ShipHull/AttachPoints/Seat");

            // §3.8: адрес корабля выдаётся до AddChild — у заспавненного NetId != 0
            var net = _ship.GetNodeOrNull<NetState>("NetState");
            Check(net != null, "у корабля есть узел NetState (§3.2)");
            Check(net != null && net.ShipNetId != 0,
                  $"ShipNetId != 0 (получено {net?.ShipNetId})");
            Check(net != null && net.ShipNetId == 1,
                  $"в offline первый корабль имеет ShipNetId = 1 (получено {net?.ShipNetId})");
        }

        // §3.8: источник панели называется Ship#<ShipNetId>, а не Ship
        Check(DebugInfoBus.HasInfoSource("Ship#1") && !DebugInfoBus.HasInfoSource("Ship"),
              "в панели есть источник Ship#1 и нет просто Ship (именование по ShipNetId, §3.1 п.6)");

        // --- заглушка корабля (шаг 1.5): в невесомости не двигается
        _probe = new RigidBody3D
        {
            Name = "Probe",
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 1.5f,
            CollisionLayer = CollisionLayers.Ship,
            CollisionMask = CollisionLayers.ShipMask,
        };
        _probe.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1, 1, 1) } });
        main.AddChild(_probe);
        Check(Mathf.IsZeroApprox(_probe.GravityScale), "проба: GravityScale = 0");

        // --- DebugInfoBus: кнопка и источник через шину
        DebugInfoBus.RegisterDebugButton("BusButton", () => GD.Print("bus button pressed"));
        DebugInfoBus.RegisterInfoSource("Probe", () => $"probe vel = {_probe.LinearVelocity.Length():F3}");

        var bus = GetNodeOrNull<DebugInfoBus>("/root/DebugInfoBus");
        Check(bus != null, "autoload DebugInfoBus существует");

        // --- панель изначально закрыта, кнопка «Debug Mode» изначально скрыта
        Check(!_layer.DebugModeVisible, "дебаг-режим изначально выключен (кнопка скрыта)");
        Check(!_layer.PanelOpen, "панель изначально закрыта");
        Check(!GetTree().Paused, "дерево изначально не на паузе");

        var btn = _layer.GetNodeOrNull<Button>("DebugRoot/BtnDebugMode");
        Check(btn != null, "узел DebugRoot/BtnDebugMode существует");
        Check(btn != null && btn.Text == "Debug Mode", "подпись кнопки = «Debug Mode»");
        Check(btn != null && btn.Visible == false, "кнопка изначально Visible = false");
        Check(btn != null && btn.Position.X < 32f && btn.Position.Y < 32f,
              $"кнопка в левом верхнем углу (позиция {btn?.Position})");

        var panel = _layer.GetNodeOrNull<PanelContainer>("DebugRoot/DebugPanel");
        Check(panel != null, "PanelContainer «DebugPanel» существует");
        Check(_layer.GetNodeOrNull<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons") != null,
              "в панели есть VBoxContainer с кнопками");

        // UI описан в сцене: VBox содержит узлы из .tscn, код к структуре не прикладывается.
        // >= 4: добавятся кнопки сети (Start Server / Join / Disconnect, §3.7 шаг 5).
        var vbox = _layer.GetNodeOrNull<VBoxContainer>("DebugRoot/DebugPanel/VBox");
        Check(vbox != null && vbox.GetChildCount() >= 4,
              $"структура панели из сцены: VBox детей = {vbox?.GetChildCount()} (ожидается >= 4)");
        Check(_layer.GetNodeOrNull<HSeparator>("DebugRoot/DebugPanel/VBox/Separator") != null,
              "разделитель LblDebugInfo / кнопок задан в сцене");

        // Опция паузы мира — тоже узел сцены, а не созданный кодом чекбокс
        _chkPause = _layer.GetNodeOrNull<CheckButton>("DebugRoot/DebugPanel/VBox/ChkPauseWorld")!;
        Check(_chkPause != null, "в панели есть CheckButton «Pause world» из сцены");
        Check(_chkPause != null && _chkPause.ToggleMode && _chkPause.ButtonPressed,
              "чекбокс «Pause world» по умолчанию включён (паузить мир при открытой панели)");
        Check(_layer.PauseWorldEnabled, "PauseWorldEnabled == true по умолчанию");
        bool pauseToggleConnected = false;
        if (_chkPause != null)
        {
            foreach (var conn in _chkPause.GetSignalConnectionList(BaseButton.SignalName.Toggled))
            {
                if (conn.TryGetValue("callable", out Variant v) &&
                    v.As<Callable>().Method == "SetPauseWorld")
                    pauseToggleConnected = true;
            }
        }
        Check(pauseToggleConnected, "сигнал toggled чекбокса подключён к SetPauseWorld в сцене");

        // Клик обрабатывается не подпиской в коде, а связью сигнала в сцене
        bool sceneConnection = false;
        if (btn != null)
        {
            foreach (var conn in btn.GetSignalConnectionList(BaseButton.SignalName.Pressed))
            {
                // В Godot 4 словарь связи содержит ключи signal/callable/flags (method был в Godot 3)
                if (conn.TryGetValue("callable", out Variant v) &&
                    v.As<Callable>().Method == "TogglePanel")
                    sceneConnection = true;
            }
            Check(btn.IsConnected(BaseButton.SignalName.Pressed, new Callable(_layer, "TogglePanel")),
                  "сигнал pressed кнопки подключён к методу TogglePanel дебаг-слоя");
        }
        Check(sceneConnection, "pressed кнопки «Debug Mode» подключён к TogglePanel в сцене");
        Check(_layer.GetNodeOrNull<Label>("DebugRoot/DebugPanel/VBox/LblDebugInfo") != null,
              "в панели есть Label «LblDebugInfo»");

        // --- шаг 1.4: ввод и конфликт с меню
        Check(InputMap.HasAction("debug_toggle"), "в InputMap есть действие debug_toggle");
        bool f3Bound = false;
        foreach (var ev in InputMap.ActionGetEvents("debug_toggle"))
        {
            if (ev is InputEventKey k && (k.Keycode == Key.F3 || k.PhysicalKeycode == Key.F3))
                f3Bound = true;
        }
        Check(f3Bound, "debug_toggle привязан к F3 через InputMap, а не хардкодом");

        var dbgRoot = _layer.GetNode<Control>("DebugRoot");
        Check(dbgRoot.MouseFilter == Control.MouseFilterEnum.Ignore,
              "фон DebugRoot не глотает клики по игре (MouseFilter = Ignore)");
        Check(panel != null && panel.MouseFilter == Control.MouseFilterEnum.Stop,
              "сама панель кликабельна (MouseFilter = Stop)");
        Check(_layer.ProcessMode == ProcessModeEnum.Always,
              "DebugLayer живёт на паузе (ProcessMode = Always)");

        // --- зелёный текст кнопки «Debug Mode»
        if (btn != null)
        {
            var c = btn.GetThemeColor("font_color");
            Check(c.G > 0.6f && c.G > c.R + 0.3f, $"текст кнопки зелёный: {c}");
        }

        // --- тестовая кнопка из DebugLayer.Ready()
        var buttons = _layer.GetNodeOrNull<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons");
        Check(buttons != null && buttons.GetChildCount() >= 1,
              $"тестовая кнопка зарегистрирована (кнопок: {buttons?.GetChildCount()})");
    }

    public override void _Process(double delta)
    {
        _frame++;

        switch (_frame)
        {
            case 2:
                // F3 → показать кнопку
                SendF3();
                break;
            case 4:
                Check(_layer.DebugModeVisible, "F3 включает дебаг-режим (кнопка видима)");
                Check(_layer.GetNode<Button>("DebugRoot/BtnDebugMode").Visible,
                      "после F3 BtnDebugMode.Visible == true");
                break;
            case 5:
                // клик по кнопке «Debug Mode» → панель + пауза
                _layer.GetNode<Button>("DebugRoot/BtnDebugMode").EmitSignal(
                    BaseButton.SignalName.Pressed);
                break;
            case 7:
                Check(_layer.PanelOpen, "клик по «Debug Mode» открывает панель");
                Check(GetTree().Paused, "открытая панель ставит дерево на паузу (опция включена по умолчанию)");
                Check(_layer.GetNode<Label>("DebugRoot/DebugPanel/VBox/LblDebugInfo").Text
                      .Contains("world: PAUSED"), "в DebugInfo видно, что мир на паузе");
                break;
            case 8:
                // клик по тестовой кнопке → печать в консоль (см. вывод «debug button pressed»)
                var buttons = _layer.GetNode<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons");
                var testBtn = FindButton(buttons, "Test");
                Check(testBtn != null, "в панели есть кнопка «Test» из DebugLayer");
                testBtn?.EmitSignal(BaseButton.SignalName.Pressed);
                break;
            case 9:
                // кнопка из шины должна появиться в той же панели
                var busButtons = _layer.GetNode<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons");
                Check(busButtons.GetChildCount() >= 2,
                      $"DebugInfoBus.RegisterDebugButton добавил кнопку (всего: {busButtons.GetChildCount()})");
                FindButton(busButtons, "BusButton")?.EmitSignal(BaseButton.SignalName.Pressed);
                break;
            case 10:
                // DebugInfo обновляется и на паузе (у DebugLayer ProcessMode = Always)
                var label = _layer.GetNode<Label>("DebugRoot/DebugPanel/VBox/LblDebugInfo");
                Check(label.Text.Contains("FPS"), $"DebugInfo содержит FPS: [{FirstLine(label.Text)}]");
                Check(label.Text.Contains("probe vel"),
                      "DebugInfo содержит блок системы (probe vel)");
                // §3.8: ключ источника — Ship#{ShipNetId}; в offline адрес = 1,
                // поэтому строка панели начинается с "ship#1:" и в одиночном режиме.
                Check(label.Text.Contains("ship#1:"),
                      "DebugInfo содержит блок корабля (ship#1: |v| ...)");
                break;
            case 11:
                // выключаем опцию паузы галочкой в панели (смена ButtonPressed эмитирует toggled)
                if (_chkPause != null) _chkPause.ButtonPressed = false;
                break;
            case 13:
                // опция выключена: панель открыта, но мир живёт
                Check(!_layer.PauseWorldEnabled, "PauseWorldEnabled == false после снятия галочки");
                Check(_layer.PanelOpen, "панель при этом осталась открытой");
                Check(!GetTree().Paused, "выключенная опция: мир НЕ на паузе при открытой панели");
                Check(_layer.GetNode<Label>("DebugRoot/DebugPanel/VBox/LblDebugInfo").Text
                      .Contains("world: running"), "в DebugInfo видно, что мир работает");
                break;
            case 14:
                // гравитация не тянет тело: за ~10 кадров скорость осталась нулевой
                Check(Mathf.IsZeroApprox(_probe.LinearVelocity.Length()),
                      $"проба не упала: |velocity| = {_probe.LinearVelocity.Length():F4}");
                break;
            case 15:
                // кнопка корабля: импульс → дальше инерция без гравитации.
                // Мир живёт (опция паузы выключена), поэтому тело реально полетит.
                var shipButtons = _layer.GetNode<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons");
                var nudge = FindButton(shipButtons, "Nudge Ship");
                Check(nudge != null, "Ship зарегистрировал кнопку «Nudge Ship» через шину");
                nudge?.EmitSignal(BaseButton.SignalName.Pressed);
                _nudgeFrame = _frame;
                break;

            // --- шаг 1.6: крепление игрока к сценам (задел на этап 6)
            case 16:
                Check(_player != null && _player.IsAttached,
                      "Player авто-пристёгнут к точке по умолчанию");
                Check(_player?.AttachedTo?.Name == "Seat",
                      $"точка по умолчанию — кресло корабля Seat (получено {_player?.AttachedTo?.Name})");
                Check(_player != null && _player.GetParent() == _ship?.GetNodeOrNull<Node>("ShipHull/AttachPoints/Seat"),
                      "после пристёжки игрок — узел внутри иерархии корабля");
                break;
            case 17:
                var detachBtn = FindButton(_layer.GetNode<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons"),
                                           "Detach Player");
                Check(detachBtn != null, "Player зарегистрировал кнопку «Detach Player»");
                detachBtn?.EmitSignal(BaseButton.SignalName.Pressed);
                break;
            case 18:
                Check(_player != null && !_player.IsAttached, "Detach: игрок больше ни к чему не пристёгнут");
                // §3.5 п.1: мировой корень берается явно (Player.FindWorldRoot),
                // после detach игрок возвращается в Main — не в GameRoot и не в Ship.
                Check(_player != null && _player.GetParent() == _main,
                      "Detach: игрок вернулся в мировой корень Main");
                break;
            case 19:
                var attachBtn = FindButton(_layer.GetNode<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons"),
                                           "Attach Player to Seat");
                Check(attachBtn != null, "Player зарегистрировал кнопку «Attach Player to Seat»");
                attachBtn?.EmitSignal(BaseButton.SignalName.Pressed);
                break;
            case 20:
                var seat = _ship?.GetNodeOrNull<AttachPoint>("ShipHull/AttachPoints/Seat");
                Check(_player != null && _player.IsAttached && _player.AttachedTo == seat,
                      "повторная пристёжка работает (игрок снова в кресле)");
                Check(_player != null && seat != null &&
                      _player.Position.DistanceTo(seat.PlayerOffset) < 0.001f,
                      $"пристёгнутый игрок стоит в позе точки (локально {_player?.Position})");
                break;
            case 25:
                // спустя ~10 кадров после импульса: скорость сохранилась (linear_damp = 0),
                // вертикальная составляющая не появилась (гравитации нет)
                if (_ship != null)
                {
                    var v = _ship.LinearVelocity;
                    Check(v.Length() > 4.5f,
                          $"инерция сохранилась: |v| = {v.Length():F2} м/с через {_frame - _nudgeFrame} кадров");
                    Check(Mathf.IsZeroApprox(v.Y),
                          $"гравитация не тянет корабль вниз: v.Y = {v.Y:F4}");
                }
                break;
            case 26:
                // включаем паузу обратно — мир должен встать прямо при открытой панели
                if (_chkPause != null) _chkPause.ButtonPressed = true;
                break;
            case 28:
                Check(_layer.PauseWorldEnabled, "PauseWorldEnabled == true после возврата галочки");
                Check(GetTree().Paused, "возврат опции: мир снова на паузе при открытой панели");
                break;
            case 29:
                // на паузе физика не шагает: положение корабля замирает
                _shipFrozenPos = _ship?.GlobalPosition ?? Vector3.Zero;
                break;
            case 39:
                var p = _ship?.GlobalPosition ?? Vector3.Zero;
                Check(p.DistanceTo(_shipFrozenPos) < 0.0001f,
                      $"включённая пауза реально заморозила мир: смещение за 10 кадров = " +
                      $"{p.DistanceTo(_shipFrozenPos):F4} м");
                break;
            case 40:
                // F3 при открытой панели → закрыть панель, снять паузу, скрыть кнопку
                SendF3();
                break;
            case 42:
                Check(!_layer.DebugModeVisible, "повторный F3 выключает дебаг-режим");
                Check(!_layer.PanelOpen, "панель закрылась");
                Check(!GetTree().Paused, "пауза снята");
                Finish();
                break;
        }
    }

    /// <summary>Кнопки создаются кодом, их Name генерируется движком — ищем по подписи.</summary>
    private static Button? FindButton(Node container, string text)
    {
        foreach (var child in container.GetChildren())
        {
            if (child is Button b && b.Text == text)
                return b;
        }
        return null;
    }

    private void SendF3()
    {
        var key = new InputEventKey
        {
            Keycode = Key.F3,
            PhysicalKeycode = Key.F3,
            Pressed = true,
            Echo = false,
        };
        GetViewport().PushInput(key);
        GetViewport().PushInput(new InputEventKey
        {
            Keycode = Key.F3,
            PhysicalKeycode = Key.F3,
            Pressed = false,
        });
    }

    private string LayerName(int layer) =>
        ProjectSettings.GetSetting($"layer_names/3d_physics/layer_{layer}").AsString();

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }

    private void Check(bool condition, string description)
    {
        _checks++;
        if (condition)
        {
            GD.Print($"PASS  {description}");
        }
        else
        {
            _failures++;
            GD.Print($"FAIL  {description}");
        }
    }

    private void Finish()
    {
        GD.Print($"RESULT checks={_checks} failures={_failures}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
