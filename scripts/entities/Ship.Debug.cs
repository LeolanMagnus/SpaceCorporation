using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial Ship: вырезано из Ship.cs при partial-декомпозиции.
public partial class Ship : RigidBody3D
{
    /// <summary>Ввод из теста (без клавиатуры): если true, `_localInput` берётся
    /// из `_testFrame`, а не из `Input.IsActionPressed`.</summary>
    private bool _testInput;
    private InputFrame _testFrame;

    /// <summary>Шаг из теста: `_IntegrateForces` игнорирует реальные физкадры и
    /// ждёт явного `TestStepForTest()`. Нужен, если в headless физический шаг
    /// этого тела не доходит до интегратора (проверяется пробой
    /// `tests/probes/ProbeInputHeadless.tscn`).</summary>
    private bool _testStep;

    /// <summary>Ручка кнопки «Teleport to Docking Zone» (шаг 5.12) — снимается
    /// в _ExitTree по ручке, а не по метке (надёжно при пересборке корабля).</summary>
    private DebugButtonHandle? _teleportHandle;

    /// <summary>Ручки «Nudge Ship» и «Toggle Interpolation» этого корабля. Снимаем
    /// ТОЛЬКО по ним (BUG-008-рецидив): снятие по метке помечает снятыми ВСЕ ручки
    /// с таким именем, поэтому смена пилота на одном корабле срезала кнопку ДРУГОГО
    /// (в Stage1Test п.25 «Nudge Ship» после этого толкал не тот корабль →
    /// «инерция сохранилась: |v| = 0,00»). У нескольких кораблей метка законно
    /// повторяется, поэтому массовая чистка по метке недопустима.</summary>
    private DebugButtonHandle? _nudgeHandle;
    private DebugButtonHandle? _interpHandle;
    private DebugButtonHandle? _godHandle;
    private DebugButtonHandle? _assistHandle;
    /// <summary>Кнопки выбора типа корабля (по одной на запись каталога) — тоже
    /// по ручкам этого корабля, см. комментарий к _nudgeHandle.</summary>
    private readonly DebugButtonHandle?[] _typeHandles =
        new DebugButtonHandle?[ShipStatCatalog.Types.Length];

    /// <summary>Зарегистрированы ли кнопки этого корабля (BuildDebugButtons уже
    /// отработал). `ApplyPilot` вызывается и внутри `_Ready()` — до регистрации, —
    /// и тогда пересборку кнопок делает идущий следом BuildDebugButtons.</summary>
    private bool _debugButtonsBuilt;

    /// <summary>Авторитет, под который собраны кнопки (`null` — кнопок нет). Нужен,
    /// чтобы `ApplyPilot` пересобирал кнопки ТОЛЬКО при реальной смене роли: набор
    /// кнопок гейтуется авторитетом, а при В14=B смена пилота авторитет не меняет
    /// (§3.5 п.8). Пересборка «на всякий случай» снимала кнопку и ставила её в
    /// конец списка панели, и автотест инерции (Stage1Test п.25) начинал толкать не
    /// тот корабль: в мире два корабля, у каждого своя «Nudge Ship», а тест ищет
    /// кнопку по тексту и берёт первую найденную.</summary>
    private bool? _debugButtonsAuthority;
    /// <summary>Пир-пилот, под которого собраны кнопки (смена пилота меняет
    /// набор: кнопки ассиста и выбора типа корабля — только локальному пилоту).</summary>
    private int _debugButtonsPilot = -1;
    /// <summary>Состояние окна, которое пилотирование временно перехватывает:
    /// режим мыши и включён ли ввод у узла. Возвращаем при освобождении.</summary>
    private Input.MouseModeEnum _prevMouseMode = Input.MouseModeEnum.Visible;
    private bool _capturedInput;

    /// <summary>
    /// Тестовый шов (п.13): симулирует переключение флага `PilotAssist` на тике
    /// `t` (в середине прогона предсказания клиента). Эмулирует поведение
    /// `TogglePilotAssist`: инвертирует `Loadout.PilotAssist`, помечает ожидаемым
    /// новое значение (`_assistExpected`) и пересобирает `Flight` с новым флагом.
    /// Вызывается внутри прогона предсказания в `Ship.ReplayInputsAfter(t)` —
    /// там, где клиент переигрывает буфер на серверных тиках с новым значением
    /// (ожидаемое состояние) и не видит ошибок расхождения с репликой.
    /// </summary>
    public void ApplyAssistToggleForTest(uint t)
    {
        // Инвертируем локальный флаг (Loadout.PilotAssist).
        Loadout.PilotAssist = !Loadout.PilotAssist;
        RebuildFlight();
        // Помечаем ожидаемое состояние (как реальное T): `_assistExpected` — это
        // новое значение, и все кадры `> t` в истории переигровки строятся с ним.
        _assistExpected = Loadout.PilotAssist;
        _assistAppliedTick = t;
    }

    /// <summary>Счётчик жёстких откатов предсказания (reconciile-рассинхрон, шаг 2.4).
    /// В тесте п.13 ожидаем: переключение флага в середине прогона НЕ должно
    /// добавлять откатов (переигровка с новым флагом сходится с сервером).</summary>
    public int AssistRollbacks => HardRollbackCount;


    /// <summary>Зарегистрировать кнопки корабля под текущую роль, предварительно
    /// сняв свои же. Вызывается из `_Ready()` один раз и из `ApplyPilot` при смене
    /// пилота (§3.1 п.4: кнопку импульса перерегистрируем вместе с ролью, иначе у
    /// бывшего пилота остаётся кнопка, шлющая импульс в никуда).
    ///
    /// Кнопка для проверки инерции («Nudge Ship»): короткий импульс вперёд, дальше
    /// тело летит само. Только авторитету: не-авторитет не имеет права вызывать
    /// ApplyImpulse (§2.5 п.6). В offline авторитет есть у всех — кнопка на месте.
    /// Шаг 4.9: god mode включает только серверный процесс. Сервер — всегда
    /// авторитет корабля (В14=B), поэтому гейт по HasAuthority = гейт «только
    /// сервер» (п.23: у клиента-не-авторитета кнопки нет вовсе).
    /// Шаг 5.12: телепорт в зону стыковки — только сервер (он единственный легально
    /// двигает тела, §3.6): у клиента-не-авторитета кнопки нет вовсе (п.33).
    /// Кнопка интерполяции, наоборот, имеет смысл только там, где hull рисуется по
    /// снимкам (наблюдатель/пилот-предсказание), поэтому — только не-авторитету.</summary>
    private void BuildDebugButtons()
    {
        RemoveDebugButtons();

        if (HasAuthority)
        {
            _nudgeHandle = DebugInfoBus.RegisterDebugButton("Nudge Ship", NudgeShip);
            _godHandle = DebugInfoBus.RegisterDebugButton("Toggle God Mode", ToggleGodMode);
            _teleportHandle = DebugInfoBus.RegisterDebugButton(
                "Teleport to Docking Zone", TeleportToDockingZone);

            // CLI-путь god mode для headless/dedicated (панели нет): `-- --god`
            // идёт в тот же серверный метод, что и кнопка (п.24).
            if (NetworkManager.GodModeCli)
                ToggleGodMode();
        }
        else
        {
            _interpHandle = DebugInfoBus.RegisterDebugButton(
                "Toggle Interpolation", ToggleInterpolation);
        }

        // Этап 6: кнопка переключения ассиста — локальному пилоту (любой роли:
        // offline-хосту с авторитетом и клиенту-пилоту). Не-пилоту кнопки нет:
        // флаг — право пилота, кнопка только сбила бы его запросом-отказом.
        if (IsLocalPilot)
        {
            _assistHandle = DebugInfoBus.RegisterDebugButton(
                "Toggle Pilot Assist (T)", RequestTogglePilotAssist);

            // Тип корабля (дебаг-выбор из трёх пресетов): локальному пилоту —
            // по кнопке на каждый тип каталога. Запрос идёт через RequestShipType
            // (гейт пилота на сервере), поэтому кнопка дублирует разрешение, а
            // не обходит его. Регистрация — по ручкам этого корабля (как у
            // _nudgeHandle): метки кнопок не различают корабли.
            for (int i = 0; i < ShipStatCatalog.Types.Length; i++)
            {
                var spec = ShipStatCatalog.Types[i];
                ShipType captured = spec.Type; // захват в замыкание (цикловая переменная)
                _typeHandles[i] = DebugInfoBus.RegisterDebugButton(
                    $"ShipType: {spec.DisplayName}", () => RequestShipType(captured));
            }
        }

        _debugButtonsBuilt = true;
        _debugButtonsAuthority = HasAuthority;
        _debugButtonsPilot = PilotPeerId;
    }

    /// <summary>Снять СОБСТВЕННЫЕ кнопки корабля (по ручкам). Массовое снятие по
    /// метке здесь недопустимо: у нескольких кораблей метка законно повторяется, и
    /// смена пилота на одном корабле срезала бы кнопку другого (см. _nudgeHandle).</summary>
    private void RemoveDebugButtons()
    {
        DebugInfoBus.RemoveDebugButton(_nudgeHandle);
        DebugInfoBus.RemoveDebugButton(_godHandle);
        DebugInfoBus.RemoveDebugButton(_teleportHandle); // 5.12: снимаем по ручке (не задев чужие)
        DebugInfoBus.RemoveDebugButton(_interpHandle);
        DebugInfoBus.RemoveDebugButton(_assistHandle); // Этап 6: кнопка ассиста
        for (int i = 0; i < _typeHandles.Length; i++)
        {
            DebugInfoBus.RemoveDebugButton(_typeHandles[i]); // выбор типа корабля
            _typeHandles[i] = null;
        }
        _nudgeHandle = null;
        _godHandle = null;
        _teleportHandle = null;
        _interpHandle = null;
        _assistHandle = null;
    }

    /// <summary>Шов автеста (по образцу `TestFire`): «запрос стыковки пришёл от
    /// пира X» без UDP. Зовёт боевой серверный гейт, а не копию.</summary>
    public void TestSetPilotForTest(int peerId) => PilotPeerId = peerId;

    public Vector3 TestGetVelocity() => LinearVelocity;

    public void TestSetVelocity(Vector3 v) => LinearVelocity = v;

    /// <summary>Шов автотеста: засунуть снимок в буфер интерполяции вручную
    /// (без UDP и без Synchronizer'а), чтобы проверить результат интерполяции.</summary>
    public void TestPushSnapshot(double time, Vector3 position, Quaternion rotation)
    {
        _interpBuf.Add(new Snapshot(time, position, rotation));
    }

    /// <summary>Шов автотеста: текущее глобальное положение картинки ShipHull.</summary>
    public Vector3 TestHullGlobalPosition()
    {
        var hull = GetNodeOrNull<Node3D>("ShipHull");
        return hull?.GlobalPosition ?? Vector3.Zero;
    }

    /// <summary>Шов автотеста: выставить тир последнего wire-снимка ровно так,
    /// как это делает приёмник (строка ~3059: `_lastWireFarTier = s.FarTier`).
    /// Нужен для far-гейта уноса (R8): в стенде оба корабля авторитетны,
    /// настоящим `ApplyBatchRecord` тир не выставить — он отбивается гейтом.</summary>
    public void TestSetWireTierForTest(bool farTier)
    {
        _lastWireFarTier = farTier;
    }

    // ------------------------------------------------------ силовые вызовы

    private void NudgeShip()
    {
        var impulse = -GlobalTransform.Basis.Z * Mass * 5f;
        ApplyImpulseAuthorityOnly(impulse);
        // Скорость обновится только на следующем шаге физики, поэтому печатаем импульс.
        GD.Print($"ship#{Net?.ShipNetId ?? 0} nudged, impulse = {impulse.Length():F1} Н*с (ожидается |v| ≈ 5 м/с)");
    }

    /// <summary>Подать кораблю кадр ввода без клавиатуры (headless-тест). Ввод
    /// проходит тот же путь, что и реальный: шаг берёт его как «кадр шага» и
    /// передаёт в `FlightModel.Step`. Мышь задаётся прямо в кадре (yaw/pitch в
    /// радианах), чтобы не зависеть от транспорта `InputEventMouseMotion`.
    ///
    /// `manualSteps` — вести ли шаг вручную через `TestStepForTest`. Если true,
    /// реальные физкадры пропускаются: иначе один и тот же шаг был бы посчитан
    /// и движком, и тестом (двойная тяга).</summary>
    public void TestSetInputForTest(InputFrame frame, bool manualSteps = true)
    {
        _testInput = true;
        _testFrame = frame;
        _testStep = manualSteps;
    }

    /// <summary>Выключить тестовый ввод/тестовые шаги и вернуть управление
    /// реальным клавишам и реальным физкадрам.</summary>
    public void TestClearInput()
    {
        _testInput = false;
        _testFrame = default;
        _testStep = false;
    }

    /// <summary>Шов теста Этапа 4 (п.11/п.22/п.27): изобразить НЕ-авторитет корабля
    /// — назначить узлу чужой сетевой авторитет (peer, отличный от локального) и
    /// пересчитать роль через <see cref="ApplyAuthority"/> (тот же пересчёт, что у
    /// <see cref="ApplyPilot"/>). После шва <see cref="IsServerAuthority"/> == false:
    /// сеттеры <see cref="ShipHealthState"/> помечают запись чужой
    /// (<c>IsNonAuthorityWrite()</c> == true), а <see cref="TestStepForTest"/> не
    /// шагает (не-авторитет интегрирует только предсказание, не истину). Реальной
    /// UDP-доставки пиру в headless нет — это шов «роли» по образцу NetTest
    /// (`SetMultiplayerAuthority(peer, false)`).</summary>
    public void TestSimulateAuthorityOwnerForTest(int ownerPeer)
    {
        SetMultiplayerAuthority(ownerPeer, false);
        ApplyAuthority();
    }

    /// <summary>Шов теста (п.16): сдвинуть метку Approved в прошлое, чтобы окно
    /// протухло, и дёрнуть боевой `TickDockWindow` — без ожидания 10 реальных
    /// секунд. Пишет тот же код, что читаёт боевая функция (подделка времени,
    /// не логики — §3.8).</summary>
    public void TestAgeDockWindowForTest(double secondsAgo)
    {
        _dockApprovedUptimeSec = Time.GetTicksMsec() / 1000.0 - secondsAgo;
        _hasDockApprovedUptime = true;
    }

    public void TestTickDockWindowForTest() => TickDockWindow();

    /// <summary>Явный шаг модели (для теста, где физкадров нет): ровно один шаг
    /// `FlightModel` из текущего состояния тела с записью обратно — тот же код,
    /// что и `_IntegrateForces`.</summary>
    public void TestStepForTest(float dt = 0f)
    {
        if (!HasAuthority)
            return;
        if (dt <= 0f) dt = FlightModel.FixedDt;

        FlightState s = new FlightState
        {
            Position = GlobalPosition,
            Rotation = GlobalTransform.Basis.GetRotationQuaternion().Normalized(),
            LinearVelocity = LinearVelocity,
            AngularVelocity = AngularVelocity,
        };

        // Шаг 2.3: ввод берётся из того же источника, что и боевой шаг
        // (`FrameForStep`): offline/локальный пилот — реальные клавиши
        // (`CollectInputFrame`), сервер с внешним пилотом — очередь принятых
        // кадров (`_serverInput`). Так тест п.7 гоняет настоящий серверный путь,
        // а не его копию (§3.8).
        FlightState n = AdvanceFlight(s, FrameForStep(), dt);

        // Этап 4, шаг 4.7: тот же серверный учёт контакта, что и в бою (§3.8).
        HandleStationContacts(n.Position, n.LinearVelocity);
        if (_collisionApplied) { n.LinearVelocity = _collisionOutVelocity; _collisionApplied = false; }

        GlobalTransform = new Transform3D(new Basis(n.Rotation), n.Position);
        LinearVelocity = n.LinearVelocity;
        AngularVelocity = n.AngularVelocity;

        // Шаг 5.8: машина фаз захвата — как в бою, после шага модели
        // (`_PhysicsProcess` автора делает то же после физики).
        TickDockPhases();
        // frame-plan шаг 3: тот же путь правила, что в бою (§3.8).
        TickCaptureRule();
    }

    /// <summary>Собрать кадр ввода так, как это делает реальный шаг (клавиши или
    /// тестовый кадр + накопленная мышь). Публично — чтобы тест мог проверить
    /// «клавиша → бит маски» без физического шага.</summary>
    public InputFrame TestCollectInputFrame() => CollectInputFrame();

    /// <summary>Добавить мышиную дельту в пикселях напрямую, не посылая события
    /// мыши (тест п.6 проверяет и транспорт, и накопление).</summary>
    public void TestAddLookDelta(Vector2 pixels) => _lookDelta += pixels;

    /// <summary>Состояние гейта прицела для данной экранныой точки. Шов нужен,
    /// чтобы тест мог проверить сам гейт, а не только следствие: если гейт
    /// сломан и всегда возвращает false, проверка «мышь вне прицела не крутит»
    /// прошла бы молча.</summary>
    public bool TestAimOnShip(Vector2 screenPos) => AimOnShip(screenPos);

    // ------------------------------------------------------------------
    // Швы реконсиляции (стадия 2, шаг 2.4, тесты п.9-11)
    // ------------------------------------------------------------------
    //
    // Реконсиляция в бою вызывается из `_Process` по новой реплике `NetState`.
    // В автотесте настоящего Synchronizer'а нет, поэтому тест сеет предсказание
    // и кольцо вводов напрямую и дёргает ядро `ReconcileWith` с эталоном. Так
    // проверяется именно алгоритм (порог / откат / число переигранных кадров),
    // а не транспорт реплик (его покрывает NetTest).

    /// <summary>Задать предсказание пилота напрямую — посев перед сверкой.</summary>
    public void TestSeedPrediction(FlightState p)
    {
        _predicted = p;
        _hasPrediction = true;
    }

    /// <summary>Записать кадр ввода в кольцевой буфер по его тикку — посев
    /// неподтверждённых кадров для проверки числа переигровок (п.9).</summary>
    public void TestSeedPending(InputFrame frame)
    {
        _pending[frame.Tick & (PendingRingSize - 1)] = frame;
    }

    /// <summary>Задать локальный счётчик тика: граница `t < _tick` в переигровке
    /// определяет, какие кадры считаются «ещё не подтверждёнными».</summary>
    public void TestSetTick(uint t) => _tick = t;

    /// <summary>Дёргнуть ядро реконсиляции против эталона и применённого тика.
    /// После — можно читать `GlobalPosition` (картинка/камера идут за телом),
    /// `PredictionError` и `LastReplayedCount`.</summary>
    public void TestReconcile(FlightState server, uint appliedTick)
        => ReconcileWith(server, appliedTick);
}
