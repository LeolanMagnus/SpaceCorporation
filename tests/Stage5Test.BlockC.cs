using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Блок C (п.19–п.29 плана stage5) — пружина 5.8, гейт предсказания 5.9,
/// гейт урона + замок 5.10. Отдельный partial того же драйвера, чтобы
/// Stage5Test.cs оставался картой фаз (образец Stage4Test.Helpers.cs).
/// </summary>
public partial class Stage5Test
{
    // ──────────── п.19–п.20: огни у копии и после отмены ────────────

    /// <summary>п.19–п.20: «огни» — ПРОИЗВОДНОЕ от реплицируемого DockApproved:
    /// как только свойство стало true (в живом тесте его ставит Synchronizer из
    /// реплики — здесь мы пишем репличное значение напрямую, подменяя только
    /// транспорт), _Process станции зажигает маркер; AbortDock гасит его.
    /// Тот же seam, что п.4–п.5 блока A: инспекция вместо реального UDP.</summary>
    private void CheckBeaconOnReplica()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null || _station == null) return;
        var beacon = _station.GetNodeOrNull<MeshInstance3D>("DockingPoint/DockBeacon");
        if (beacon == null) { Check(false, "п.19: DockBeacon есть в сцене"); return; }

        // п.19: реплика дописала DockApproved=true у копии станции (так
        // придёт изменение из батча) — маркер загорается из реплики, RPC нет.
        _station.State!.DockApproved = true;
        _station._Process(0.0);
        Check(beacon.Visible, "п.19: после реплики DockApproved=true маркер виден («клиенты видят огни»)");

        // п.20: AbortDock на сервере обнуляет StationState (обе стороны),
        // _Process гасит маркер.
        _shipA.GlobalPosition = _station.DockPointGlobal + new Vector3(40, 0, 0);
        _shipA.TestSetVelocity(Vector3.Zero);
        _shipA.TestRequestDock(1, _station.State!.StationNetId); // подтверждённая запись поверх
        nm.AbortDock(_shipA.Net!.ShipNetId, "test-p20");
        _station._Process(0.0);
        Check(!beacon.Visible && !_station.State!.DockApproved,
              "п.20: после AbortDock маркер погашен и DockApproved=false");
    }

    // ──────────── п.21–п.23, п.26: серверная пружина захвата ────────────

    private int _lastDockDamageCount;
    private float _dockDistStart;

    /// <summary>п.21: после Capturing пружина тянет корабль к точке: расстояние
    /// не растёт, скорость не раскачивается (демпфированная система, не
    /// осциллятор). п.22: финальная поза в доке (0.5 м). п.23: сетевой слепок
    /// (Net.NetPosition — источник батча) едет следом. п.26: всё это ВНУТРИ
    /// цилиндра станции (r=60) — урона нет ни одного кадра (гейт В19).</summary>
    private void CheckDockSpringCapture()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null) return;
        int shipA = _shipA.Net!.ShipNetId, stationId = _station!.State!.StationNetId;
        Vector3 dp = _station.DockPointGlobal;

        // Ввод-«тяга» есть, но на capture-фазе он игнорируется пружиной (п.25
        // для клиента — отдельно; здесь серверный путь).
        var frame = new InputFrame { Tick = 1, Mask = InputMask.ThrustF };
        _shipA.TestSetInputForTest(frame);
        _shipA.TestSetPilotForTest(1);
        _shipA.GlobalPosition = dp + new Vector3(55, 0, 0); // и в зоне (<=90), и в цилиндре (r=60) — п.26
        _shipA.TestSetVelocity(Vector3.Zero);

        int dmgBefore = _shipA.Health!.DamageTakenCount;
        float distStart = _dockDistStart = _shipA.GlobalPosition.DistanceTo(dp);

        // F: Approved; повторная F: захват (AcceptDockFrom ведёт сделку в
        // Capturing — «вторая F» боевого пути).
        Check(_shipA.TestRequestDock(1, stationId) && _shipA.TestRequestDock(1, stationId),
              "п.21: Approved → Capturing повторным запросом (боевой путь второй F)");
        Check((DockState)_shipA.Net!.DockStateValue == DockState.Capturing,
              "п.21: фаза корабля Capturing до шагов пружины");

        // Полёт к станции с посадочной скоростью — пружина подхватывает.
        _shipA.TestSetVelocity(new Vector3(-4, 0, 0));

        float prevDist = _shipA.GlobalPosition.DistanceTo(dp);
        float prevSpeed = 4f;
        bool monotonous = true, noGrowth = true;
        float badAt = 0, badPrev = 0, badPosNaN = 0;
        int badStep = -1;
        int steps = 0;
        const int Cap = 1200; // 20 с симуляции
        for (; steps < Cap; steps++)
        {
            _shipA.TestStepForTest(FlightModel.FixedDt);
            float d = _shipA.GlobalPosition.DistanceTo(dp);
            float s = _shipA.TestGetVelocity().Length();
            if (float.IsNaN(d) || float.IsNaN(s))
            {
                badAt = d; badStep = steps; badPosNaN = 1;
                monotonous = false;
                break;
            }
            // Пружина демпфирована: у точки стыка возможен small overshoot
            // (проба показала 0.26 м при LatchDistance=3) — он в пределах латча
            // и гасится. Рост дальше 1 м = осцилляция/отскок — баг.
            if (d > prevDist + 1.0f)
            {
                if (badStep < 0) { badStep = steps; badPrev = prevDist; badAt = d; }
                monotonous = false;
            }
            // «Не раскачивается»: |v| не больше большего из начальной и терминальной
            // скорости пружины (терминальная при клампе a=25: a/c ≈ 6.25 м/с + запас).
            if (s > Mathf.Max(prevSpeed, 8f) + 0.5f && prevDist > 5f) noGrowth = false;
            prevDist = d; prevSpeed = s;
            if ((DockState)_shipA.Net!.DockStateValue == DockState.Docked) break;
        }
        _lastDockDamageCount = _shipA.Health!.DamageTakenCount;

        Check(monotonous, badPosNaN != 0
              ? $"п.21: NaN в шаге {badStep} (расстояние {badAt})"
              : $"п.21: расстояние до точки не растёт все {steps+1} шагов (рост на шаге {badStep}: {badPrev:F2}->{badAt:F2})");
        Check(noGrowth, "п.21: |v| не раскачивается после входа в док-окно (демпфирование)");
        Check((DockState)_shipA.Net!.DockStateValue == DockState.Docked,
              $"п.22: захват доведён до Docked за {steps+1} шагов (получено {(DockState)_shipA.Net!.DockStateValue})");
        Check(_shipA.GlobalPosition.DistanceTo(dp) <= Docking.DockDistance + 1e-3f,
              $"п.22: финальная поза в доке: {prevDist:F2} м <= {Docking.DockDistance} (старт {distStart:F1})");
        // П.23 (сетевой слепок едет следом) — в P16/CheckDockLock: публикация
        // снимка идёт в _PhysicsProcess автора, а ручной цикл шагов synchronously
        // не доходит до физкадра; на следующем кадре NetPosition догонит истину.
        Check(_shipA.Health!.DamageTakenCount == dmgBefore,
              "п.26: всё внутри цилиндра станции — урона ноль (гейт В19 на dock-фазе)");
        Check(_shipA.Health!.Hp > 0, "п.26: корабль жив после захвата (HP не капал)");
    }

    private float Net2Dock() => _shipA!.Net!.NetPosition.DistanceTo(_station!.DockPointGlobal);

    // ──────────── п.27, п.29: финальный замок и его стойкость ────────────

    /// <summary>п.27: на Docked сервер обнулил скорости и заморозил тело.
    /// п.29: пересчёт роли (тот же ApplyAuthority, что зовёт ApplyPilot) не
    /// снимает dock-замок. Продолжает состояние после CheckDockSpringCapture.</summary>
    private void CheckDockLock()
    {
        if (_shipA == null || _station == null) return;
        Check((DockState)_shipA.Net!.DockStateValue == DockState.Docked,
              "п.27: фаза Docked (после проверки пружины)");
        // п.23: слепок сервера (NetPosition) публикуется в _PhysicsProcess — к
        // этому кадру он уже доехал до дока: ближе к точке, чем было до захвата.
        Check(_shipA.Net!.NetPosition.DistanceTo(_station.DockPointGlobal) < _dockDistStart,
              $"п.23: сетевой слепок едет следом: NetPosition в {Net2Dock():F2} м от точки (старт {_dockDistStart:F1})");
        Check(_shipA.TestGetVelocity().Length() == 0f,
              $"п.27: LinearVelocity обнулён на Docked (получено {_shipA.TestGetVelocity().Length()})");
        Check(_shipA.Freeze, "п.27: у сервера-авторитета Freeze == true на Docked");

        // п.29: смена пилота пересчитывает роль через ApplyAuthority — замок
        // обязан пережить (иначе «смена пилота стирает замок»).
        _shipA.TestSetPilotForTest(999);
        _shipA.TestSimulateAuthorityOwnerForTest(1);
        Check(_shipA.Freeze && (DockState)_shipA.Net!.DockStateValue == DockState.Docked,
              "п.29: ApplyPilot-пересчёт (ApplyAuthority) не снял dock-замок");
        // Снимаем замок штатным порядком: фаза ушла (AbortDock убрал её до
        // пересчёта) → ApplyAuthority пересчитывает Freeze.
        NetworkManager.Instance!.AbortDock(_shipA.Net.ShipNetId, "test-cleanup-p29");
        _shipA.TestSimulateAuthorityOwnerForTest(1);
        Check(!_shipA.Freeze && (DockState)_shipA.Net!.DockStateValue == DockState.None,
              "п.29: AbortDock + пересчёт роли штатно снимают фазу и замок");
        _shipA.TestSetPilotForTest(1);
    }

    // ──────────── п.28: таран без сделки по-прежнему ранит ────────────

    /// <summary>п.28: гейт В19 точечный — без активной сделки тот же вход в
    /// цилиндр станции на 20 м/с даёт урон (регресс Stage4Test п.17/п.18 жив).</summary>
    private void CheckNoDockStillRams()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null) return;
        nm.AbortDock(_shipA.Net!.ShipNetId, "test-p28");
        _shipA.TestClearInput();
        _shipA.Freeze = false; // замок снят AbortDock'ом через ApplyAuthority? проверяем ниже
        _shipA.TestSetInputForTest(new InputFrame { Tick = 1 });

        // Ставим корабль ВНУТРЬ ТЕЛА станции (цилиндр r=60, h=40 от центра
        // станции), а не на высоте точки дока: точка дока приподнята над крышей
        // (1dd24b7 «жидкий космос»: y=26.55 при полувысоте 20), и на её высоте
        // корпус ни с чем не пересекается — IntersectShape даёт ноль, урона нет.
        // Горизонтали 50 м достаточно: и корпус в цилиндре, и зона дока (r=90 от
        // точки) покрыта, т.е. таран происходит ровно там, где мог бы быть док.
        Vector3 dp = _station!.DockPointGlobal;
        Vector3 st = _station.GlobalPosition;
        _shipA.GlobalPosition = st + new Vector3(50, 0, 0);
        Check(_shipA.GlobalPosition.DistanceTo(dp) <= Docking.DockRange,
              $"п.28: таран в пределах док-зоны: {dp.DistanceTo(_shipA.GlobalPosition):F1} м <= {Docking.DockRange}");
        _shipA.TestSetVelocity(new Vector3(-20, 0, 0));

        int dmgBefore = _shipA.Health!.DamageTakenCount;
        int hpBefore = _shipA.Health!.Hp;
        for (int i = 0; i < 30; i++)
            _shipA.TestStepForTest(FlightModel.FixedDt);

        Check(_shipA.Health!.DamageTakenCount > dmgBefore && _shipA.Health!.Hp < hpBefore,
              $"п.28: без сделки таран 20 м/с ранит: dmgCount {dmgBefore}->{_shipA.Health!.DamageTakenCount}, HP {hpBefore}->{_shipA.Health!.Hp}");
        Check((DockState)_shipA.Net!.DockStateValue == DockState.None,
              "п.28: контакт без сделки не запустил dock-машину");

        // Убираем корабль из станции, чистим состояние перед следующим блоком.
        _shipA.GlobalPosition = dp + new Vector3(0, 0, 400);
        _shipA.TestSetVelocity(Vector3.Zero);
        _shipA.TestClearInput();
    }

    // ──────────── п.24–п.25: клиент-пилот не предсказывает на dock-фазе ────────────

    /// <summary>п.24: у клиента-пилота (авторитет = чужой пир, пилот = локальный)
    /// в Capturing за 60 кадров _Process счётчик жёстких откатов не растёт;
    /// п.25: hull клиента идёт по интерполяции, а не от локального движения тела
    /// (поза hull = интерполированная, тело следует за ней).</summary>
    private void CheckClientPilotDockGate()
    {
        if (_shipA == null || _station == null) return;
        var nm = NetworkManager.Instance!;

        // Роль «пилот-клиент»: авторитет узла = чужой пир 2, пилот = мы (1).
        _shipA.TestSetPilotForTest(nm.SelfId);
        _shipA.TestSimulateAuthorityOwnerForTest(2);
        Check(_shipA.IsLocalPilot && !_shipA.IsServerAuthority,
              "п.24: стенд «клиент-пилот» собран (IsLocalPilot && !IsServerAuthority)");

        int rollbacksBefore = _shipA.HardRollbackCount;

        // Dock-фаза в реплике (так её увидит клиент из батча).
        _shipA.Net!.DockStateValue = (byte)DockState.Capturing;
        Vector3 dp = _station.DockPointGlobal;

        // 60 кадров реплик: SimTick двигается, _Process пилота обязан уйти в
        // dock-ветку (интерполяция) и НЕ реконсилировать.
        for (int i = 1; i <= 60; i++)
        {
            _shipA.Net.NetPosition = dp + new Vector3(20f - 0.3f * i, 0, 0);
            _shipA.Net.NetRotation = Quaternion.Identity;
            _shipA.Net.SimTick = i;
            _shipA._Process(1.0 / 60.0);
        }
        Check(_shipA.HardRollbackCount == rollbacksBefore,
              $"п.24: в Capturing за 60 кадров откатов ноль (было {rollbacksBefore}, стало {_shipA.HardRollbackCount})");

        // п.25: hull — по интерполяции принятых снимков, НЕ там, где тело само
        // по себе. К концу цикла hull уже едет по позиции из реплик (около дока,
        // с отставанием на InterpDelay); «телепорт тела» рядом не должен сдвинуть
        // картинку — поза берётся из батча, а не из локального движения.
        Vector3 hullBefore = _shipA.TestHullGlobalPosition();
        Check(hullBefore.DistanceTo(dp) < 25f,
              $"п.25: hull следует за репликами батча: hull в {hullBefore.DistanceTo(dp):F1} м от точки");
        _shipA.GlobalPosition = dp + new Vector3(300, 0, 0); // «телепорт тела» — hull обязан за ним не пойти
        _shipA.Net.NetPosition = dp + new Vector3(2, 0, 0);
        _shipA.Net.SimTick = 61;
        for (int i = 0; i < 60; i++)
        {
            _shipA.Net.NetPosition = dp + new Vector3(2, 0, 0);
            _shipA.Net.SimTick = 62 + i;
            _shipA._Process(1.0 / 60.0);
        }
        var hull = _shipA.TestHullGlobalPosition();
        // Тело (на пилоте-клиенте в dock-фазе) само ведётся за hull — важно
        // обратное: картинка НЕ поехала за «телепортом» тела (dp+300).
        Check(hull.X < dp.X + 100f && hull.DistanceTo(dp + new Vector3(300, 0, 0)) > 200f,
              $"п.25: hull не поехал за локальным телом ({hull}, телепорт был в {dp + new Vector3(300, 0, 0)})");
        Check(hull.DistanceTo(dp) < 25f,
              $"п.25: hull вернулся в позу из батча: {hull.DistanceTo(dp):F1} м от точки");

        // Возврат в боевую конфигурию теста: авторитет сервера, пилот мы,
        // фаза снята. (Предсказание рестартует из реплики — _hasPrediction=false
        // ставит сам выход из dock-ветки.)
        _shipA.Net.DockStateValue = 0;
        _shipA.TestSimulateAuthorityOwnerForTest(1);
        _shipA.TestSetPilotForTest(1);
        _shipA.Net!.DockStateValue = 0;
        _shipA.Freeze = false;
        nm.AbortDock(_shipA.Net.ShipNetId, "test-cleanup-p25");
        _shipA.TestClearInput();
    }

    // ──────────── п.30–п.32: обрыв пира посреди стыковки (5.11) ────────────

    /// <summary>п.30: сделка до Docked + OnPeerLeft → полная отмена (у сервера
    /// DockState None, DockApproved false, StationState.DockShipNetId 0);
    /// п.31: Docked + OnPeerLeft → запись из реестра уходит, НО состояние и
    /// Freeze остаются (посаженный без пилота корабль стоит на причале);
    /// п.32: реестр активных сделок после отключения пуст.
    /// «Копия после Pump» в headless недоступна (ENet-цикла нет, §3.10 шаг 6) —
    /// роль реплики играет NetState/StationState, очищенные тем же AbortDock.</summary>
    private void CheckPeerLeftDockAbort()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null || _station == null) return;
        NetworkManager.TestExtraPeers.Add(2);
        int shipId = _shipA.Net!.ShipNetId, stId = _station.State!.StationNetId;

        // --- п.30: Capturing + обрыв пира → отмена ------------------------
        nm.BeginDock(shipId, 2, stId, out _);
        nm.SetDockPhase(shipId, DockState.Capturing);
        Check(nm.ActiveDockCount == 1 && _station.State!.DockApproved,
              "п.30: стенд незавершённой сделки поднят (реестр+Approved)");
        nm.OnPeerLeft(2);
        Check((DockState)_shipA.Net.DockStateValue == DockState.None &&
              _shipA.Net.DockStationNetId == 0,
              "п.30: у корабля DockState -> None, связь со станцией снята");
        Check(!_station.State!.DockApproved &&
              (DockState)_station.State!.DockStateValue == DockState.None &&
              _station.State!.DockShipNetId == 0,
              "п.30: у станции Approved false, DockState None, DockShipNetId 0");

        // --- п.31: Docked + обрыв пира → состояние остаётся ---------------
        _shipA.Net.DockStateValue = (byte)DockState.Docked;
        _shipA.Net.DockStationNetId = stId;
        _shipA.Freeze = true;
        nm.BeginDock(shipId, 2, stId, out _);
        nm.SetDockPhase(shipId, DockState.Docked);
        // Финальный флаг станции: окно Approved к этому моменту уже закрыто
        // (формула SetDockPhase покрывает только фазы окна) — так же, как его
        // закрывает окно-гейт в реальном флоу.
        _station.SetDockStateServerOnly(DockState.Docked, shipId);
        _station.State!.DockApproved = false;
        nm.OnPeerLeft(2);
        Check((DockState)_shipA.Net.DockStateValue == DockState.Docked && _shipA.Freeze,
              "п.31: Docked пережил обрыв пира (состояние+Freeze сохранены)");

        // --- п.32: реестр пуст ---------------------------------------------
        Check(nm.ActiveDockCount == 0,
              $"п.32: карта активных сделок после отключения пуста (активных: {nm.ActiveDockCount})");

        // Возврат тестовой сцены в «базу»: Docked-состояние снимаем явно.
        nm.AbortDock(shipId, "test-cleanup-p32");
        _shipA.Freeze = false;
    }

    // ──────────── п.33–п.34: серверная кнопка телепорта в зону (5.12) ────────────

    /// <summary>п.33: у не-авторитета кнопка «Teleport to Docking Zone» не
    /// регистрируется (гейт HasAuthority в _Ready + снятие по ручке в _ExitTree).
    /// Как в Stage4Test п.23: инстансим Ship.tscn с authority=999 ДО AddChild.</summary>
    private void CheckTeleportButtonServerOnly()
    {
        int before = DebugInfoBus.CountDebugButtons("Teleport to Docking Zone");
        var scene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        Check(scene != null, "п.33: Ship.tscn загружается");
        if (scene == null) return;
        var ship = scene.Instantiate<Ship>();
        ship.Name = "TpClientCopy";
        ship.SetMultiplayerAuthority(999, false);
        _root!.AddChild(ship);
        ship.TestSimulateAuthorityOwnerForTest(999);
        Check(DebugInfoBus.CountDebugButtons("Teleport to Docking Zone") == before,
              "п.33: у клиента-не-авторитета кнопка телепорта НЕ добавлена (счётчик не вырос)");
        // У авторитета того же корабля кнопка есть — дырки в гейте нет: штатные
        // корабли стенда (сервер-авторитет) зарегистрировали её в _Ready.
        Check(DebugInfoBus.CountDebugButtons("Teleport to Docking Zone") >= 1,
              "п.33: у сервера-авторитета кнопка телепорта зарегистрирована");
        ship.QueueFree();
    }

    /// <summary>п.34: сервер дёргает телепорт (шов вызывает тело кнопки) — корабль
    /// из точки вне зоны и с большой скоростью оказывается внутри зоны с нулевой
    /// скоростью, и запрос стыковки сразу принимается (тот же гейт 5.6).</summary>
    private void CheckServerTeleportThenDock()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null || _station == null) return;
        int stId = _station.State!.StationNetId;

        // Доводим стенд до «нельзя пристыковаться»: далеко и быстро.
        _shipA.TestClearInput();
        nm.AbortDock(_shipA.Net!.ShipNetId, "test-p34-pre");
        _shipA.GlobalPosition = _station.DockPointGlobal + new Vector3(0, 0, 400);
        _shipA.TestSetVelocity(new Vector3(0, 0, -120));
        Check(!Docking.InZone(_shipA.GlobalPosition, _station.DockPointGlobal),
              "п.34: стенд собран — корабль вне зоны стыковки");
        int rejBefore = nm.RejectedDockCount;
        Check(!_shipA.TestRequestDock(1, stId),
              "п.34: до телепорта заявка отклонена (вне зоны)");
        Check(nm.RejectedDockCount == rejBefore + 1 && !nm.HasDockRequest(_shipA.Net.ShipNetId),
              "п.34: счётчик отказов вырос, записи в реестре нет");
        // Сервер дёргает кнопку.
        Check(_shipA.TestTeleportToDockingZoneForTest(), "п.34: телепорт нашёл станцию");
        Check(Docking.InZone(_shipA.GlobalPosition, _station.DockPointGlobal),
              $"п.34: после телепорта InZone == true ({_shipA.GlobalPosition})");
        Check(_shipA.TestGetVelocity().Length() <= Docking.DockSpeedLimit,
              $"п.34: |v| <= лимита после телепорта ({_shipA.TestGetVelocity().Length()})");

        // Тот же запрос теперь принимается — телепорт подготовил ровно те условия.
        Check(_shipA.TestRequestDock(1, stId), "п.34: заявка после телепорта принята");
        Check(nm.HasDockRequest(_shipA.Net.ShipNetId) && nm.ActiveDockCount == 1,
              $"п.34: сделка в реестре (активных {nm.ActiveDockCount})");
        nm.AbortDock(_shipA.Net.ShipNetId, "test-cleanup-p34");
        _shipA.GlobalPosition = _station.DockPointGlobal + new Vector3(0, 0, 400);
        _shipA.TestSetVelocity(Vector3.Zero);
    }

    // ──────────── п.35: dock-строка в панели debug-info (5.13) ────────────

    /// <summary>п.35: BuildInfoText содержит `dock = None` (и `dockStn = —`) до
    /// запроса; после фиксации (SetDockPhase Docked) — `dock = Docked` и адрес
    /// станции; у станции — свой блок `station#N: dock = ..., docker = ...`.
    /// Прежние ключи (`mode =`, `frozen =`, `HP:`) остаются на месте — их
    /// assert'ы стадий 3–4 (п.36 общего прогона) не ломаются.</summary>
    private void CheckDockDebugInfo()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null || _station == null) return;
        int shipId = _shipA.Net!.ShipNetId;
        int stId = _station.State!.StationNetId;

        Check(DebugInfoBus.HasInfoSource($"Ship#{shipId}"),
              $"п.35: источник панели Ship#{shipId} зарегистрирован");
        Check(DebugInfoBus.HasInfoSource($"Station#{stId}"),
              $"п.35: источник панели Station#{stId} зарегистрирован (новый блок 5.13)");

        // До запроса: None и пустой адрес станции.
        nm.AbortDock(shipId, "test-pre-p35");
        string text = DebugInfoBus.BuildInfoText();
        Check(text.Contains($"ship#{shipId}") && text.Contains("dock = None")
              && text.Contains("dockStn = —"),
              "п.35: до запроса в панели dock = None, dockStn = —");
        Check(text.Contains($"station#{stId}: dock = None") && text.Contains("docker = —"),
              "п.35: станционный блок есть: station#N: dock = None, docker = —");
        // Прежние ключи не съехали (Stage3 п.26 / Stage4 п.26 читают их из того же текста).
        Check(text.Contains("mode = ") && text.Contains("frozen = ") && text.Contains("HP: "),
              "п.35: прежние ключи mode/frozen/HP на месте — assert'ы стадий 3-4 целы");

        // После фиксации: Docked + адрес станции у корабля, docker у станции.
        nm.BeginDock(shipId, 1, stId, out _);
        nm.SetDockPhase(shipId, DockState.Docked);
        _station.SetDockStateServerOnly(DockState.Docked, shipId);
        text = DebugInfoBus.BuildInfoText();
        Check(text.Contains("dock = Docked") && text.Contains($"dockStn = {stId}"),
              "п.35: после фиксации в панели dock = Docked, dockStn = <id станции>");
        Check(text.Contains($"station#{stId}: dock = Docked") && text.Contains($"docker = {shipId}"),
              "п.35: станционный блок: dock = Docked, docker = <id корабля>");

        nm.AbortDock(shipId, "test-cleanup-p35");
        _station.SetDockStateServerOnly(DockState.None, 0);
    }
}
