using System;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Игрок-персонаж: капсула + привязанная к ней камера (scenes/Player.tscn).
///
/// Ключевая механика задела на этап 6 — крепление к точкам (AttachPoint):
///   AttachTo(point) — игрок становится узлом точки крепления (кресло кабины,
///                     позже — пол станции/палубы) и летает/ходит вместе с ней;
///   Detach()        — возвращается в мировой корень сцены, сохраняя позицию.
///
/// Поэтому в Main.tscn игрок просто стоит рядом с кораблём, а сам факт «игрок
/// в кабине» задаётся кодом (точка Seat с IsDefaultForPlayer = true), а не
/// вложенностью узлов в сцене: иерархию можно менять в рантайме.
///
/// Горизонтальное перемещение по палубе (этап 6) — MoveInAttachedPlane: движение
/// в локальной плоскости XZ точки крепления, то есть «вперёд» для игрока =
/// «вперёд» корабля или станции, к которой он пристёгнут.
/// </summary>
public partial class Player : CharacterBody3D
{
    /// <summary>Высота капсулы (совпадает с CapsuleShape3D в scenes/Player.tscn).</summary>
    public const float CapsuleHeight = 1.8f;

    [Export] public NodePath CameraRigPath { get; set; } = "CameraRig";

    /// <summary>Автопристёжка к точке по умолчанию на первом физическом шаге.
    /// По умолчанию ВКЛЮЧЕНА (added 2026-08): игрок спавнится пристёгнутым к
    /// креслу пилота и видит дефолтный вид «из глаз» (своя камера Camera3D).
    /// V (`toggle_pilot_camera`) переключает вид из кресла на chase-камеру
    /// корабля и обратно, F (`detach`) отстёгивает.</summary>
    [Export] public bool AutoAttachOnSpawn { get; set; } = true;

    /// <summary>Точка крепления, к которой пристёгнут игрок (null — свободен).</summary>
    public AttachPoint? AttachedTo { get; private set; }

    public bool IsAttached => AttachedTo != null;

    /// <summary>Куда игрок возвращается при Detach (мировой корень сцены).</summary>
    private Node3D _worldRoot = null!;

    private Node3D _cameraRig = null!;
    private bool _awaitingDefaultPoint = true;
    private int _defaultPointAttempts;

    /// <summary>Этот узел — локальный игрок (§3.5 п.2): ввод, камера и кнопки только
    /// для него. В offline авторитет = 1 = SelfId, поэтому true, и поведение стадии 1
    /// не меняется. На клиенте чужие игроки имеют авторитет другого пира — у них
    /// false; в dedicated-процессе узла Player нет вовсе (§2.4).</summary>
    private bool _isLocal;

    /// <summary>Свои кнопки дебаг-меню (только для _isLocal). Храним ручки,
    /// чтобы снять именно их при _ExitTree, не задев кнопки нового игрока с теми же
    /// метками при смене роли (§3.11 п.9). Ручка работает и если кнопка была
    /// зарегистрирована до готовности слоя (тогда снимается отложенная заявка).</summary>
    private DebugButtonHandle? _debugDetachButton;
    private DebugButtonHandle? _debugAttachButton;

    public override void _Ready()
    {
        CollisionLayer = CollisionLayers.Character;
        // Пока игрок сидит в корабле, он не должен сталкиваться с его корпусом:
        // маска намеренно без слоя Ship. Столкновения со станцией и мишенями — есть.
        CollisionMask = CollisionLayers.Station | CollisionLayers.Target;
        MotionMode = MotionModeEnum.Grounded;
        UpDirection = Vector3.Up;
        FloorMaxAngle = Mathf.DegToRad(50f);

        _cameraRig = GetNode<Node3D>(CameraRigPath);
        // §3.5 п.1: при спавне через MultiplayerSpawner родителем станет GameRoot,
        // а при креплении игрок вообще висит под ShipHull — поэтому мировой корень
        // берётся явно, а не из GetParent(): идём вверх по цепочке Node3D-предков до
        // верхнего 3D-узла сцены (в игре это Main; Stage1Test-обёртка — Node, цепочка
        // обрывается на Main). GetTree().CurrentScene не годится: в тестах текущей
        // сценой является обёртка Stage1Test, а не Main.
        _worldRoot = FindWorldRoot();
        if (_worldRoot == null)
            throw new System.InvalidOperationException(
                "Player должен лежать в сцене внутри 3D-узла (Main), чтобы было куда открепляться");

        // §3.5 п.2: ввод, камера и кнопки — только локально-авторитетному игроку.
        // В offline авторитет = 1 = SelfId, поэтому local == true и поведение
        // стадии 1 не меняется. У чужих игроков камеру глушим принудительно:
        // в Player.tscn стоит current = true, и вторая камера перехватит viewport.
        //
        // Встроенный IsMultiplayerAuthority() при снятом peer (после Disconnect
        // MultiplayerPeer = null) зовёт GetUniqueId() и печатает ERROR. Сравниваем
        // авторитет узла с локальным id напрямую; при снятом peer локальный id = 1
        // (offline-авторитет), поэтому в offline это 1 == 1.
        int localId = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetUniqueId() : 1;
        _isLocal = GetMultiplayerAuthority() == localId;
        if (!_isLocal)
        {
            var cam = GetActiveCamera();
            if (cam != null && cam.IsCurrent())
                cam.Current = false;
        }

        // Автопристёгивание откладывается на первый физический шаг: Reparent во время
        // _Ready() запрещён (дерево сцены ещё достраивается), а на первом шаге все
        // точки уже в группе attach_point — порядок узлов в Main.tscn не важен.
        _awaitingDefaultPoint = true;

        // Дебаг-кнопки: цикл «открепиться → свободно → пристегнуться обратно» — ровно
        // то, что понадобится на этапе 6 для перехода корабль ↔ станция.
        // §3.5 п.3: только локальному игроку, и вместо прямых AttachTo/Detach —
        // запрос через сеть (Request*), чтобы Reparent был на сервере и повторялся
        // каждым пиром. В offline запрос сворачивается в локальный вызов.
        if (_isLocal)
        {
            // Держим СВОИ кнопки по экземплярам: при смене роли (Offline→Host) старый
            // игрок удаляется QueueFree, а новый регистрирует кнопки с теми же метками
            // в том же кадре. Снятие по экземпляру гарантирует, что _ExitTree старого
            // игрока уберёт именно его кнопки, а не кнопки нового (§3.11 п.9).
            _debugDetachButton = DebugInfoBus.RegisterDebugButton("Detach Player", OnButtonDetach);
            _debugAttachButton = DebugInfoBus.RegisterDebugButton("Attach Player to Seat", OnButtonAttach);
            // Космическая пыль: тоже только локальному игроку — тот же гейт, что
            // у кнопок и камеры (§3.5 п.2). Детальный комментарий — в Player.Dust.cs.
            BuildDustField();
        }


        DebugInfoBus.RegisterInfoSource("Player",
            _playerInfoSource ??= () =>
            {
                // Защита от гонки удаления: если этот узел уже освобождён, а источник
                // ещё висит в шине (QueueFree откладывает _ExitTree до конца кадра),
                // GetParent() бросил бы ObjectDisposedException (§3.11 п.9).
                if (!GodotObject.IsInstanceValid(this))
                    return string.Empty;
                return
                    $"player: parent = {GetParent()?.GetName()}, " +
                    $"attached = {AttachedTo?.Name ?? "-"}, " +
                    $"pos = {Fmt(GlobalPosition)}";
            });

        // eva-plan Блок E (Е3) / п.11: HUD-источник скафандра «Suit» — топливо/
        // батарея/режим/фонарь/предупреждения. Источник ОДИН на ВСЮ шину и
        // читается HUD как статус ТЕКУЩЕГО наблюдаемого игрока — поэтому он
        // принадлежит ТОЛЬКО локальному игроку (тот же гейт, что у кнопок, камеры
        // и пыли, §3.5 п.2/п.3): иначе удалённый пир перезаписал бы чужой «Suit»
        // своими цифрами. Текст строит SuitState-реплика (Д2), а не локальные
        // поля — в сети цифры обязан показывать корректно каждый пир за СВОЁГО
        // игрока.
        if (_isLocal)
            DebugInfoBus.RegisterInfoSource(SuitInfoSourceKey,
                _suitInfoSource ??= () => SuitInfoTextSafe());
    }

    /// <summary>Лямбда-источник «Suit» в шине: держим ссылку, чтобы снять её в
    /// `_ExitTree` по владельцу (та же лямбда) и не стереть свежий источник нового
    /// игрока при перестройке мира. Текст читает скафандр только живого узла.</summary>
    private Func<string>? _suitInfoSource;
    private Func<string>? _playerInfoSource;

    private string SuitInfoTextSafe() =>
        GodotObject.IsInstanceValid(this) ? SuitInfoText() : string.Empty;

    /// <summary>Ключ источника скафандра в DebugInfoBus (eva-plan: источник «Suit»).</summary>
    public const string SuitInfoSourceKey = "Suit";

    /// <summary>Строка HUD-источника «Suit»: режим (SEAT/EVA/BOOTS), проценты
    /// топлива/батареи, фонарь (L), активные предупреждения (<10% с гистерезисом
    /// из SuitState.LowFuelWarn/LowBatteryWarn). Формат стабильный: тест п.11
    /// сверяет подстроки `fuel=`/`batt=`/`mode=`.</summary>
    public string SuitInfoText()
    {
        SuitState s = Suit;
        string mode = Mode switch
        {
            EvaMode.Seat => "SEAT",
            EvaMode.Boots => "BOOTS",
            _ => "EVA",
        };
        string warns = "";
        if (s.LowFuelWarn)
            warns += " LOW_FUEL";
        if (s.LowBatteryWarn)
            warns += " LOW_BATT";
        return $"suit: mode={mode}, fuel={Mathf.RoundToInt(s.Fuel)}%, " +
               $"batt={Mathf.RoundToInt(s.Battery)}%, light={(s.LightOn ? "on" : "off")}" +
               $"{warns}";
    }

    public override void _ExitTree()
    {
        // Снимаем регистрации в шине дебага ТОЛЬКО при реальном удалении узла
        // (QueueFree при смене роли Offline→Dedicated, §3.6): лямбда-источник
        // «Player» и кнопки захватывают этот узел, и если он освобождён, а источник
        // остался в шине, DebugInfoBus.BuildInfoText каждый кадр зовёт мёртвую лямбду
        // → GetParent() на disposed-объекте → ObjectDisposedException (§3.11 п.9).
        //
        // Важно: _ExitTree срабатывает и при Reparent (пристёжка/отстёжка игрока
        // переносит узел между точкой и мировым корнем), а _Ready после Reparent
        // НЕ вызывается повторно. Поэтому без проверки на удаление кнопки и источник
        // терялись бы после первой же пристёжки (§3.5). IsQueuedForDeletion() == true
        // ровно когда узел помечен QueueFree — это и есть путь удаления.
        if (!IsQueuedForDeletion())
            return;

        DebugInfoBus.RemoveInfoSource("Player", _playerInfoSource);
        // п.32/Блок E: источник «Suit» снимается по владельцу (та же лямбда):
        // при RebuildWorld старый игрок (QueueFree) снимает СВОЮ лямбду в
        // отложенном _ExitTree, уже после того как новый игрок перерегистрировался
        // под тем же ключом — слепое Remove стёрло бы свежий источник нового.
        DebugInfoBus.RemoveInfoSource(SuitInfoSourceKey, _suitInfoSource);
        if (_isLocal)
        {
            // Снимаем СВОИ кнопки по ручкам: при Offline→Host старый игрок удаляется
            // QueueFree (этот _ExitTree отработает в конце кадра), а новый игрок уже
            // успел зарегистрировать кнопки с теми же метками. Снятие по ручке убирает
            // ровно нашу кнопку (или её отложенную заявку), не задев кнопки нового
            // игрока (§3.11 п.9).
            DebugInfoBus.RemoveDebugButton(_debugDetachButton);
            DebugInfoBus.RemoveDebugButton(_debugAttachButton);
            _debugDetachButton = null;
            _debugAttachButton = null;
        }
    }

    private static string Fmt(Vector3 v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";

    /// <summary>Верхний Node3D-предок: мировой корень сцены (§3.5 п.1). Обёртка
    /// теста — обычный Node, поэтому цепочка обрывается на Main.</summary>
    private Node3D? FindWorldRoot()
    {
        Node3D? top = null;
        for (var n = GetParent(); n != null; n = n.GetParent())
            if (n is Node3D n3)
                top = n3;
        return top;
    }

    public override void _PhysicsProcess(double delta)
    {
        // Космическая пыль — локальный визуал, обновляем КАЖДЫЙ физический шаг и
        // ДО раннего return ниже (иначе в покое/на клиенте UpdateDust не дошёл бы
        // и поле застыло). Детали жизненного цикла частиц — в Player.Dust.cs.
        if (_isLocal)
            UpdateDust((float)delta);

        // ── EVA поза-реплика (Блок D, Д1): публикация СВОЕЙ позы идёт ДО EVA-
        // ветки и её ранних return'ов ниже — иначе в покое (StepEva рано выходит)
        // и в кресле (Seat return) поза не слалась бы вовсе. Внутри метода свои
        // гейты: не-владелец, offline/без peer и EvaMode.Seat выходят сразу,
        // поэтому офлайн-прогон и кресло не генерируют ни одного пакета.
        PublishEvaPoseFrame();

        // ── EVA (Блок C, С4): клавиши читаются в физическом процессе ДО раннего
        // return ниже — иначе G/L/T не работают в покое (тот же грабль, что
        // «UpdateDust до return» выше). StepEva возвращает true, когда режим EVA
        // сам ведёт тело: тогда и автокрепление, и любая обычная ходьба ниже
        // обязаны пропустить шаг, иначе `Velocity = wish * speed` из
        // MoveInAttachedPlane перетёр бы инерцию скафандра (шаг 6 плана).
        // В EvaMode.Seat StepEva сам возвращает false — автокрепление на спавне
        // (AutoAttachOnSpawn) не задевает. НО: пока игрок ждёт дефолтную точку
        // крепления, его режим — Jetpack (дефолт свободного тела) и StepEva вернул
        // бы true, отрезав автопристёгивание насовсем (регресс Stage1Test: «F:
        // игрок вернулся в мировой корень», поза NaN). Поэтому EVA-шаг включается
        // только когда ожидания дефолтной точки уже нет.
        if (_isLocal && !(AutoAttachOnSpawn && _awaitingDefaultPoint))
        {
            HandleEvaKeys();
            // Hover-панель под прицелом (гейт «сесть только через панель» +
            // HUD-подсказка): обновляем КАЖДЫЙ физический шаг локального
            // свободного EVA-игрока — и ДО раннего return StepEva, иначе в
            // движении hover устаревал бы. В тестовых стендах физика игрока
            // глушится (`EvaTestSetPhysicsPaused`), а hover задаётся швами
            // `EvaTestHoverAt` — этот шаг их не перетирает.
            UpdatePanelHover();
            if (StepEva(FlightModel.FixedDt))
                return;
        }

        // ── Скафандр в кресле (Блок B2): батарея заряжается/обогрев ест энергию и
        // в кресле — игрок не должен «замораживать» расходники, сидя в пилоте.
        // Идёт ДО раннего return ниже (ветка автопристёжки) и только на
        // авторитетной стороне: по плану расход считает сервер, клиент рисует
        // реплику (риск 4 eva-plan — иначе «заправка при ложном пристёгивании»
        // становится чит-дырой). Локальному игроку вне кресла шаг делает StepEva.
        if (IsAttached)
        {
            var nmAttach = NetworkManager.Instance;
            bool authorityAttach = nmAttach == null || !nmAttach.HasRealPeer || nmAttach.IsServer;
            if (authorityAttach)
                StepSuitInSeat(FlightModel.FixedDt);
        }

        // §3.5 п.4: автопристёгивание — только серверу и offline. На клиенте
        // крепление приходит репликой ApplyAttach, и _awaitingDefaultPoint не должен
        // заставлять клиента крепиться самому (иначе Reparent разойдётся с сервером).
        // AutoAttachOnSpawn = true (added 2026-08): игрок спавнится пристёгнутым в
        // кресле пилота и сразу видит дефолтный вид «из глаз» (своя Camera3D);
        // V переключает вид на chase-камеру корабля, F отстёгивает.
        if (!AutoAttachOnSpawn || !_awaitingDefaultPoint)
        {
            _awaitingDefaultPoint = false;
            return;
        }
        var nm = NetworkManager.Instance;
        bool authoritySide = nm == null || !nm.HasRealPeer || nm.IsServer;
        if (!authoritySide)
            return;

        if (AttachToDefaultPoint())
        {
            _awaitingDefaultPoint = false;
            return;
        }

        // Точки ещё нет в группе (её сцена достраивается) — пробуем в следующем шаге,
        // но бесконечно молчать не будем: через секунду предупреждаем один раз.
        if (++_defaultPointAttempts == 60)
        {
            GD.PushWarning("Player: точка крепления по умолчанию " +
                           "(AttachPoint.IsDefaultForPlayer) так и не найдена");
        }
    }

    /// <summary>RPC-рассылка состояния принимается только от сервера (id = 1):
    /// в авторитетной сервером модели именно он рассылает Apply* всем (§2.5).</summary>
    private bool IsFromServer() => Multiplayer.GetRemoteSenderId() == 1;

    // ------------------- клавиши V (вид пилота) и F (отстегнуться), added 2026-08

    /// <summary>
    /// Ввод локального игрока двумя действиями (added 2026-08):
    ///   V  `toggle_pilot_camera` — сменить вид из кресла пилота: «из глаз»
    ///      (дефолт пристёгнутого — своя камера Camera3D) ⇄ chase-камера корабля.
    ///      Работает только когда игрок пристёгнут к креслу корабля, где он пилот:
    ///      режим вида — локальное свойство корабля, по сети он не летит.
    ///   F  `detach` — отстегнуться (раньше F была клавишей запроса стыковки;
    ///      запрос стыковки переезжает в окно радиосвязи, следующая стадия).
    /// Событие, а не физический шаг: как у F3 в DebugLayer — `_Input` +
    /// `IsActionPressed`, затем SetInputAsHandled, чтобы клавиша не утёкла в кадр
    /// ввода корабля (битов V и F маска пилота не содержит — `torque_yaw_right`
    /// переехал на B, — но страховка от будущих коллизий).
    /// Пути те же, что у дебаг-кнопок: клиенту RPC, хосту/offline — серверная
    /// сердцевина напрямую (§3.5 п.3).
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (!_isLocal)
            return;

        if (@event.IsActionPressed("toggle_pilot_camera"))
        {
            TogglePilotCameraView();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event.IsActionPressed("detach"))
        {
            // F = «кресло» по плану (В21: одна клавиша на два действия):
            // пристёгнут ⇒ отстегнуться, свободен ⇒ занять штурвал/кресло под
            // курсором (EVA-луч). Развилка здесь, а не внутри EVA-обработчика:
            // путь отстёжки (RPC клиенту / серверная сердцевина хосту) живёт в
            // OnButtonDetach, и дублировать его в EVA-файле нельзя (§3.5 п.3).
            if (IsAttached)
                OnButtonDetach();
            else
                OnDetachOrSeatPressed();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>V: переключить вид пилота (только если мы пристёгнуты к креслу
    /// корабля, которым управляем локально). Возвращает false, если переключать
    /// нечего — игрок не в кресле пилота.</summary>
    public bool TogglePilotCameraView()
    {
        // Правка: V работает и вне кресла — в EVA (джетпак/ботинки) она
        // переключает камеру от первого лица ⇄ внешнюю (капсула видна). Только
        // если мы НЕ в кресле; в кресле — обычная пилотская chase-камера.
        if (_mode != EvaMode.Seat && ToggleEvaCameraView())
            return true;
        Ship? ship = PilotedShip();
        if (ship == null)
        {
            GD.Print("Player: V — вид пилота доступен только пристёгнутым в кресле " +
                     "корабля, которым управляешь сам");
            return false;
        }
        ship.TogglePilotCamera();
        return true;
    }

    /// <summary>Корабль, к креслу которого игрок пристёгнут И который является
    /// пилотируемым для этого пира (локальный пилот). Иначе null: у пассажира
    /// своей камеры-вида нет, chase-камеру чужого корабля включать нельзя.</summary>
    private Ship? PilotedShip()
    {
        if (!IsAttached || AttachedTo == null || AttachedTo.Kind != AttachPoint.KindOf.Seat)
            return null;
        for (Node n = AttachedTo.GetParent(); n != null; n = n.GetParent())
        {
            if (n is Ship ship && ship.IsLocalPilot)
                return ship;
        }
        return null;
    }

    // ------------------------------------------------------ дебаг-кнопки

    private void OnButtonAttach()
    {
        var (owner, point) = NearestAttachPoint();
        if (owner <= 0)
        {
            GD.PrintErr("Player: в реестре нет ни одной точки крепления");
            return;
        }
        // §3.5 п.3: при нескольких кораблях кнопка крепит к ближайшей точке
        // реестра, а не к Seat первого.
        GD.Print($"attach → ship#{owner}/point#{point}");
        // RPC → сервер нужен ТОЛЬКО настоящему клиенту. Хост — это и есть сервер
        // (авторитетная сторона): у его собственного игрока нет клиент→серверного
        // перехода, и RequestAttach с CallLocal=false отработал бы НЕ из сети
        // (GetRemoteSenderId() == 0), а IsRequestFromOwner сравнил бы 0 с авторитетом
        // 1 и отклонил запрос (§2.5 п.2). Хост и offline выполняют серверную логику
        // напрямую — тот же признак authoritySide, что в _PhysicsProcess.
        if (NetworkManager.Instance is { IsClient: true })
            RequestAttach(owner, point);      // клиент: RPC → сервер
        else
            AttachServerSide(owner, point);   // хост/offline: тот же код локально (§1.8)
    }

    private void OnButtonDetach()
    {
        // См. OnButtonAttach: RPC → сервер только настоящему клиенту. Хост (он же
        // сервер) и offline выполняют DetachServerSide напрямую — иначе RPC с
        // CallLocal=false отработает не из сети, GetRemoteSenderId() == 0, и
        // IsRequestFromOwner отклонит запрос («пир 0 просит … авторитет = 1»).
        if (NetworkManager.Instance is { IsClient: true })
            RequestDetach();                  // клиент: RPC → сервер
        else
            DetachServerSide();               // хост/offline: локально
        GD.Print($"player detached, global = {Fmt(GlobalPosition)}");
    }

    /// <summary>Камера игрока (для дебаг-инфо и будущего прицела).</summary>
    public Camera3D? GetActiveCamera() =>
        _cameraRig?.GetNodeOrNull<Camera3D>("Camera3D");
}
