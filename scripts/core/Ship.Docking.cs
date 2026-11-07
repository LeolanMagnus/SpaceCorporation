using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial Ship: вырезано из Ship.cs при partial-декомпозиции.
public partial class Ship : RigidBody3D
{
    // Шаг 5.9: клиент-пилот в док-фазе (предсказание выключено, картинка идёт
    // по интерполяции батча). true — пока фаза активна; при выходе из фазы
    // предсказание сбрасывается (_hasPrediction=false) и стартует от реплики.
    private bool _clientDockPhaseActive;

    /// <summary>Шаг 5.8 (В20): серверная пружина вместо `Joint3D`. У корабля
    /// `CustomIntegrator` — движок `Joint3D` всё равно не читает (§0 п.3),
    /// поэтому доводка считается здесь, в том же `FlightModel`-шаге:
    /// `a = Docking.CaptureAccel(target - pos, v)` + угловая доводка носа к оси
    /// стыка (`AlignK`/`AlignDamp`). Кадр ввода — пустой (тяга пилота
    /// проигнорирана, п.25), ориентация и скорость — наши. `Freeze` не трогаем:
    /// сервер интегрирует через `CustomIntegrator`, не снимая заморозки у других.
    /// Фазы `Approved` здесь нет: там пилот ещё ЛЕТИТ к зоне обычным управлением,
    /// пружина включается только с `Capturing` (риск №3: самовольный захват).</summary>
    private FlightState StepDockSpring(FlightState s, float dt)
    {
        var station = NetworkManager.Instance?.FindStationNode(Net!.DockStationNetId);
        Vector3 dockPos = station != null ? station.DockPointGlobal
                                          : station?.GlobalPosition ?? s.Position;
        Vector3 dx = dockPos - s.Position;

        var f = new FlightState
        {
            Position = s.Position,
            Rotation = s.Rotation,
            LinearVelocity = s.LinearVelocity,
            AngularVelocity = s.AngularVelocity,
        };

        // --- линейная доводка: ускорение пружины добавляем к существующей v ---
        f.LinearVelocity += Docking.CaptureAccel(dx, f.LinearVelocity) * dt;

        // --- угловая доводка: доворот -Basis.Z к оси стыка (корабль к позиции,
        // не к центру станции) по образцу экспоненциального сглаживания углов
        // Stage2Test.PredictWithLook: q' = normalize(q * exp(w*dt/2)) ---
        Vector3 toDock = dockPos - s.Position;
        if (toDock.LengthSquared() > 1e-6f)
        {
            toDock = toDock.Normalized();
            Vector3 nose = f.Rotation * Vector3.Back; // конвенция: нос = -Z
            Vector3 corr = nose.Cross(toDock);        // ось и величина доворота
            Vector3 w = corr * Docking.AlignK - f.AngularVelocity * Docking.AlignDamp;
            // Потолок угловой скорости корпуса — и для доводки пружины: корабль
            // не обязан крутиться быстрее своей предельной скорости даже когда
            // станция «тянет» его сильнее (MaxAngularSpeed <= 0 = без потолка).
            if (Flight.MaxAngularSpeed > 0f &&
                w.LengthSquared() > Flight.MaxAngularSpeed * Flight.MaxAngularSpeed)
                w = w.Normalized() * Flight.MaxAngularSpeed;
            float blend = Mathf.Min(1f, Docking.AlignDamp * dt);
            f.AngularVelocity = f.AngularVelocity.Lerp(w, blend);
            float wLen = f.AngularVelocity.Length();
            if (wLen > 1e-6f)
            {
                var qd = new Quaternion(f.AngularVelocity.Normalized(), wLen * dt);
                f.Rotation = (f.Rotation * qd).Normalized();
            }
        }

        // --- обычный полётный шаг (инерция + интеграция позиции/ориенти),
        // пустой кадр: тяги и ручного момента пилота нет ---
        return FlightModel.Step(f, Flight, default, dt);
    }

    /// <summary>Шаг 5.8: продвижение фаз сделки (Capturing → Latched → Docked).
    /// Зовётся из обоих серверных шагов ПОСЛЕ шага модели и учёта контактов —
    /// геометрия шага уже финальная. Гистерезис: Latch у LatchDistance=3 м,
    /// финал — у DockDistance=0.5 м от точки стыка, поэтому пружина дожимает
    /// последние метры в фазе Latched (контакт с «нашей» станцией уже без урона,
    /// шаг 5.10).</summary>
    private void TickDockPhases()
    {
        if (Net == null || NetworkManager.Instance is not { IsServer: true } nm)
            return;
        var entry = nm.FindDockRequest(Net.ShipNetId);
        if (entry == null)
            return; // реестр снят (AbortDock) — фазы больше нет
        var st = entry.Value.State;
        if (st != DockState.Capturing && st != DockState.Latched)
            return;
        var station = nm.FindStationNode(Net.DockStationNetId);
        if (station == null)
        {
            nm.AbortDock(Net.ShipNetId, "станция исчезла");
            return;
        }
        Vector3 dockPos = station.DockPointGlobal;
        Vector3 dx = dockPos - GlobalPosition;
        float alignErr = Docking.AlignError(GlobalTransform.Basis.GetRotationQuaternion(),
                                            dx.LengthSquared() > 1e-6f ? dx.Normalized()
                                                                       : -GlobalTransform.Basis.Z);
        if (st == DockState.Capturing && Docking.IsLatched(GlobalPosition, dockPos, alignErr))
        {
            nm.SetDockPhase(Net.ShipNetId, DockState.Latched);
            DebugInfoBus.Report($"dock: ship#{Net.ShipNetId} latched (d = {dx.Length():F1} м)");
        }
        else if (st == DockState.Latched && Docking.IsDocked(dx, LinearVelocity))
        {
            // Шаг 5.10: финальный замок — скорости в нуль, поза точно в точке.
            LinearVelocity = Vector3.Zero;
            AngularVelocity = Vector3.Zero;
            GlobalPosition = dockPos;
            nm.SetDockPhase(Net.ShipNetId, DockState.Docked);
            ApplyAuthority(); // Freeze = true у авторитета (единственная точка записи)
            DebugInfoBus.Report($"dock: ship#{Net.ShipNetId} DOCKED at station#{Net.DockStationNetId}");
        }
    }

    /// <summary>
    /// Этап 4, шаг 4.7 — серверный учёт контакта со станцией. Вызывается из
    /// обоих шагов (`_IntegrateForces` и `TestStepForTest`) ПОСЛЕ шага модели и
    /// ДО финальной записи `LinearVelocity` — иначе запись перетрёт отскок
    /// (stage4-plan §4.7). Вызывается только на авторитете (оба вызывающих пути
    /// уже гейтятся `HasAuthority`), поэтому «правда сервера» о HP пишется без
    /// GuardWrite-ошибок, а клиент урона не считает вовсе (шаг 4.11).
    ///
    /// Обнаружение (проба tests/probes/ProbeStationPen.cs + ProbeIntegrateContact.cs):
    ///   • в C#-биндинге Godot 4.7 НЕТ `ComputePenetration`; рабочий примитив —
    ///     `PhysicsDirectSpaceState3D.IntersectShape`, вызванный прямо из
    ///     `_IntegrateForces` (пространство во время шага доступно, не «заперто»);
    ///   • в словаре пересечения есть только ключи rid/collider_id/collider/shape —
    ///     НИ глубины, НИ нормали нет, поэтому нормаль сближения считаем
    ///     геометрически: из центра станции в корабль (`stationPos → shipPos`).
    ///   • `collider` достаём через `.As<GodotObject>()` и приводим к `Station`.
    /// Урон: `CollisionDamage.ComputeDamage` (чистая функция, шаг 4.6) по
    /// нормальной составляющей скорости `ApproachSpeed`. Отскок: `ApplyBounce` —
    /// применяется ВСЕГДА при контакте (даже dmg==0), иначе корабль ползёт
    /// внутри цилиндра станции.
    /// Анти-спам: пара `(StationNetId, PhysicsFrame)` запрещает повторный учёт
    /// того же контакта в одном физкадре; `CollisionCooldownSec` — пауза между
    /// повторными удрами по той же станции (таблица «ломается»).
    /// </summary>
    private void HandleStationContacts(Vector3 shipPos, Vector3 v)
    {
        if (_hullShapeNode?.Shape == null)
            return;
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null)
            return;

        var xform = new Transform3D(GlobalTransform.Basis, shipPos);
        var pq = new PhysicsShapeQueryParameters3D
        {
            Shape = _hullShapeNode.Shape,
            Transform = xform,
            CollisionMask = CollisionLayers.Station,
            CollideWithBodies = true,
            CollideWithAreas = false,
            Exclude = new Godot.Collections.Array<Rid> { GetRid() },
            Margin = 0.0f,
        };

        var hits = space.IntersectShape(pq, 8);
        if (hits.Count == 0)
            return;

        var health = GetNodeOrNull<ShipHealthState>("ShipHealthState");
        ulong frame = Engine.GetPhysicsFrames();
        double nowSec = Time.GetTicksMsec() / 1000.0;

        foreach (Variant hv in hits)
        {
            var d = hv.AsGodotDictionary();
            if (!d.TryGetValue("collider", out Variant cv))
                continue;
            var col = cv.As<GodotObject>();
            if (col is not Station station)
                continue; // TestStand и прочая статика слоя Station урона не дают

            int stationNetId = station.State?.StationNetId ?? 0;

            // Шаг 5.10 (гейт В19): на dock-фазе контакт с ЦЕЛЕВОЙ станцией
            // (к которой идёт захват) пропускается ЦЕЛИКОМ — ни урона, ни
            // отскока. Иначе серверная пружина сама себя таранит: корпус
            // затягивает в цилиндр r=60, урон капает каждый кадр до смерти
            // корабля (риск №2 плана). Чужие станции и корабль без активной
            // сделки — как в стадии 4 (п.28: таран 20 м/с без сделки ранит).
            if (Net != null &&
                (DockState)Net.DockStateValue >= DockState.Approved &&
                Net.DockStationNetId != 0 &&
                stationNetId == Net.DockStationNetId)
                continue;

            // Тот же контакт в этом же физкадре — уже учтён (п.19).
            if (stationNetId == _lastContactStationNetId && frame == _lastContactPhysicsFrame)
                continue;
            // Кулдаун по станции (п. «застрять → урон капает»): в пределах
            // CollisionCooldownSec урон не начисляем, но отскок ниже всё равно.
            bool inCooldown = _hasLastCollisionTime &&
                              stationNetId == _lastContactStationNetId &&
                              nowSec - _lastCollisionTimeSec < CollisionCooldownSec;

            // Внешняя нормаль станции → в сторону корабля.
            Vector3 outward = shipPos - station.GlobalPosition;
            if (outward.LengthSquared() <= 0.0001f)
                outward = -GlobalTransform.Basis.Z; // вырожденный случай: наружу от носа
            outward = outward.Normalized();

            float vn = CollisionDamage.ApproachSpeed(v, outward); // >0 когда летим в станцию

            _lastContactStationNetId = stationNetId;
            _lastContactPhysicsFrame = frame;

            if (health != null && !inCooldown)
            {
                int dmg = CollisionDamage.ComputeDamage(health.MaxHp, health.GodMode, vn);
                if (dmg > 0)
                {
                    health.Hp = Mathf.Max(0, health.Hp - dmg);
                    health.LastDamage = dmg;
                    health.DamageTakenCount++;
                    _lastCollisionTimeSec = nowSec;
                    _hasLastCollisionTime = true;
                    // H6 (hud-plan): событие в комм-лог HUD — ровно один вызов
                    // на событие, шина ничего не знает про UI.
                    HudEventBus.Report($"Damage {dmg} — hull {health.Hp}/{health.MaxHp}");
                    GD.Print($"ship#{Net?.ShipNetId ?? 0}: урон {dmg} от станции " +
                             $"#{stationNetId} (|vn| = {Mathf.Abs(vn):F1} м/с), " +
                             $"Hp = {health.Hp}/{health.MaxHp}, ударов = {health.DamageTakenCount}");

                    // Шаг 4.8: Hp<=0 → серверная смерть. Этот путь уже гейтится
                    // авторитетом (_IntegrateForces/HandleStationContacts работают
                    // только у сервера), поэтому повторная проверка не нужна.
                    if (health.Hp <= 0)
                        ServerDieShip();
                }
            }

            // Отскок — всегда при контакте, независимо от урона и кулдауна.
            v = CollisionDamage.ApplyBounce(v, outward, vn, CollisionDamage.BounceRestitution);
        }

        _collisionOutVelocity = v;
        _collisionApplied = true;
    }

    /// <summary>Запрос стыковки локального пилота (клавиша F, действие
    /// `request_docking`). Тот же `FireGatesNonAim`-принцип: пилот и не
    /// Interaction. Публично — шов теста зовёт путь кода без эмуляции клавиш.</summary>
    public void RequestDocking(int stationNetId)
    {
        if (!IsLocalPilot || IsInteractionMode)
            return;
        SubmitDockToServer(stationNetId);
    }

    /// <summary>Клиент-пилот: послать серверу намерение пришвартоваться к
    /// станции `stationNetId`. Адресуется по ShipNetId (путь не идентификатор).</summary>
    private void SubmitDockToServer(int stationNetId)
    {
        if (Net == null || Net.ShipNetId == 0)
            return; // нет адреса — запрос адресовать нечем
        if (NetworkManager.Instance is not { IsServer: false })
        {
            // Сервер (и offline — локальный авторитет) сам себе RPC не шлёт:
            // приём делается напрямую тем же серверным гейтом (В14=B: авторитет
            // — источник истины, RPC — транспорт чужих намерений).
            AcceptDockFrom(NetworkManager.Instance?.SelfId ?? 1,
                           Net.ShipNetId, stationNetId);
            return;
        }
        RpcId(1, nameof(RpcRequestDocking), Net.ShipNetId, stationNetId);
    }

    /// <summary>Серверный приём запроса стыковки от пилота (гейты В17). Атрибут
    /// как у `RpcRequestFire`: AnyPeer, CallLocal=false, надёжный, канал 1 —
    /// запросы редки и терять их нельзя (в отличие от кадров ввода на канале 0).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcRequestDocking(int shipNetId, int stationNetId)
    {
        AcceptDockFrom(Multiplayer.GetRemoteSenderId(), shipNetId, stationNetId);
    }

    public bool TestRequestDock(int senderPeer, int stationNetId)
        => AcceptDockFrom(senderPeer, Net?.ShipNetId ?? 0, stationNetId);

    /// <summary>Шов теста п.12: RPC с Поддельным shipNetId (чужой корабль).
    /// Зовёт тот же серверный гейт, что и RPC — «боевой код, а не копия».</summary>
    public bool TestAcceptDockFromForTest(int senderPeer, int shipNetId, int stationNetId)
        => AcceptDockFrom(senderPeer, shipNetId, stationNetId);

    /// <summary>Сколько запросов стыковки принял/отклонил сервер (автотесты
    /// п.10-п.15, п.29).</summary>
    public int AcceptedDockCount { get; private set; }
    public int RejectedDockCount { get; private set; }

    /// <summary>Метка времени принятия Approved (сек, Time.GetTicksMsec) для
    /// авто-сброса окна DockWindowSec (шаг 5.6, п.16).</summary>
    private double _dockApprovedUptimeSec;
    private bool _hasDockApprovedUptime;

    /// <summary>
    /// Серверные гейты запроса стыковки (шаги 5.5/5.6). Гейты: только сервер;
    /// адрес корабля; отправитель = пилот; авторитет корабля (В14=B); станция
    /// найдена по адресу; корабль жив; нет активной сделки (реестр). Затем
    /// геометрические: `Docking.InZone` (от точки стыка) и `Docking.AllowDock`
    /// по нормальной скорости сближения (та же метрика, что урона В19).
    /// Принятый запрос кладёт сделку в реестр (Requested) и сразу переводит в
    /// Approved («огни горят»); из Approved без захвата окно гаснет через
    /// DockWindowSec (п.16, проверка в _PhysicsProcess авторитета).
    /// </summary>
    private bool AcceptDockFrom(int senderPeer, int shipNetId, int stationNetId)
    {
        var nm = NetworkManager.Instance;
        if (nm is not { IsServer: true })
        {
            GD.PrintErr($"ship#{shipNetId}: AcceptDockFrom вызван не на сервере " +
                        "— отказ (В14=B: клиент не может сам себя одобрить)");
            return false;
        }
        if (Net == null || Net.ShipNetId != shipNetId)
            return false; // запрос про другой корабль (адрес важнее пути)
        if (senderPeer != PilotPeerId)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: стыковку запрашивает не пилот " +
                        $"(peer={senderPeer}, pilot={PilotPeerId}) — отказ (В17)");
            RejectedDockCount++;
            nm.NoteDockRejection();
            return false;
        }
        if (!HasAuthority)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: запрос стыковки на копии без " +
                        "авторитета — отказ");
            RejectedDockCount++;
            nm.NoteDockRejection();
            return false;
        }
        if (Health != null && Health.Hp <= 0)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: мёртвый корабль не стыкуется — отказ");
            RejectedDockCount++;
            nm.NoteDockRejection();
            return false;
        }
        var stationState = nm.FindStation(stationNetId);
        var station = nm.FindStationNode(stationNetId);
        if (stationState == null || station == null)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: станция #{stationNetId} не найдена — отказ");
            RejectedDockCount++;
            nm.NoteDockRejection();
            return false;
        }

        // Гейты В17 пройдены; дальше — геометрия (шаг 5.6): зона и скорость.
        // Метрика «в зоне» — от ТОЧКИ СТЫКОВКИ станции (не от её центра: r=60).
        Vector3 dockPoint = station.DockPointGlobal;
        Vector3 outward = (GlobalPosition - dockPoint).Normalized();
        float vn = CollisionDamage.ApproachSpeed(LinearVelocity, outward); // >0 когда летим в станцию
        if (!Docking.InZone(GlobalPosition, dockPoint))
        {
            GD.Print($"ship#{Net.ShipNetId}: стыковка отклонена: вне зоны " +
                     $"({GlobalPosition.DistanceTo(dockPoint):F1} м > {Docking.DockRange})");
            RejectedDockCount++; nm.NoteDockRejection();
            return false;
        }
        if (!Docking.AllowDock(Mathf.Abs(vn)))
        {
            GD.Print($"ship#{Net.ShipNetId}: стыковка отклонена: скорость {Mathf.Abs(vn):F1} " +
                     $"> {Docking.DockSpeedLimit}");
            RejectedDockCount++; nm.NoteDockRejection();
            return false;
        }

        // Все гейты пройдены. Если для этого корабля уже есть Approved-сделка
        // на ЭТУ станцию от ЭТОГО пира — повторное F = подтверждение захвата:
        // Approved → Capturing, пружина включается (п.21+; переход делает
        // сервер, клиент лишь подаёт сигнал — В14=B). Окно при этом всё ещё
        // живое: мёртвое окно TickDockWindow закрыл бы до сюда (п.16).
        var existing = nm.FindDockRequest(Net.ShipNetId);
        if (existing != null && existing.Value.State == DockState.Approved
            && existing.Value.StationNetId == stationNetId
            && existing.Value.PeerId == senderPeer)
        {
            nm.SetDockPhase(Net.ShipNetId, DockState.Capturing);
            _hasDockApprovedUptime = false;
            AcceptedDockCount++;
            DebugInfoBus.Report($"dock: ship#{Net.ShipNetId} -> station#{stationNetId} " +
                                $"capture started (peer {senderPeer})");
            return true;
        }

        // Сделка — в реестр (Requested), затем сразу Approved:
        // запрос у нас авторизуется синхронно, переходной фазы «ждём ответа» нет,
        // но фазу Requested реестр видит для честной карты (п.7).
        if (!nm.BeginDock(Net.ShipNetId, senderPeer, stationNetId, out string reason))
        {
            GD.Print($"ship#{Net.ShipNetId}: стыковка отклонена: {reason}");
            RejectedDockCount++;
            return false; // счётчик уже поднят в реестре (station-busy/already)
        }
        nm.SetDockPhase(Net.ShipNetId, DockState.Approved);
        AcceptedDockCount++;
        _dockApprovedUptimeSec = Time.GetTicksMsec() / 1000.0;
        _hasDockApprovedUptime = true;
        DebugInfoBus.Report($"dock: ship#{Net.ShipNetId} -> station#{stationNetId} approved " +
                            $"(peer {senderPeer})");
        return true;
    }

    /// <summary>Авто-сброс окна Approved (шаг 5.6, п.16): если через
    /// DockWindowSec после одобрения корабль так и не вошёл в захват —
    /// AbortDock("окно истекло"). Вызывается из _PhysicsProcess авторитета.</summary>
    private void TickDockWindow()
    {
        if (!_hasDockApprovedUptime || Net == null)
            return;
        var nm = NetworkManager.Instance;
        var entry = nm?.FindDockRequest(Net.ShipNetId);
        if (entry == null || entry.Value.State != DockState.Approved)
        {
            _hasDockApprovedUptime = false;
            return; // сделка закрыта/перешла — окно больше не Our concern
        }
        double ageSec = Time.GetTicksMsec() / 1000.0 - _dockApprovedUptimeSec;
        if (ageSec > Docking.DockWindowSec)
        {
            nm!.AbortDock(Net.ShipNetId, "окно истекло");
            DebugInfoBus.Report($"dock: ship#{Net.ShipNetId} window expired ({ageSec:F1}s)");
            _hasDockApprovedUptime = false;
        }
    }
    /// <summary>Шаг 5.12: телепорт в зону стыковки ближайшей станции — только
    /// сервер/локальный авторитет (кнопка регистрируется только ему, а метод
    /// гейтован дополнительно: прямые вызовы с клиента обязаны отбиваться).
    /// Ставит корабль вглубь зоны (20 м от точки стыка по −X) и гасит скорость:
    /// после телепорта запрос принят (зона + лимит скорости выполнены).</summary>
    private void TeleportToDockingZone()
    {
        if (!HasAuthority)
        {
            GD.Print("teleport to docking zone: запрещено (не авторитет — только сервер)");
            return;
        }
        if (!TeleportToDockingZoneCore())
            GD.Print("teleport to docking zone: нет ни одной станции — телепорт нечего направлять");
    }

    /// <summary>Тело телепорта без гейта роли (шов автотеста п.34: серверный
    /// стенд уже авторитет, проверяем эффект — InZone + |v| <= лимита +
    /// принятый запрос). Возвращает false, если станции не найдено.</summary>
    private bool TeleportToDockingZoneCore()
    {
        var nm = NetworkManager.Instance;
        Station? station = null;
        foreach (var (_, st) in nm!.Stations)
        {
            var node = st.GetParent() as Station;
            if (node != null && GodotObject.IsInstanceValid(node))
            {
                station = node;
                break;
            }
        }
        if (station == null)
            return false;

        Vector3 dp = station.DockPointGlobal;
        // Вглубь зоны (DockRange=90): 20 м — гарантированно внутри и сферы, и
        // цилиндра collision (r=60), тело не застревает в станции.
        GlobalPosition = dp + new Vector3(20, 0, 0);
        LinearVelocity = Vector3.Zero;
        // Если авторитет сам пилотирует (хост-стенд), синхронизируем и состояние
        // предсказания — иначе следующий кадр пилота продолжит от старого _predicted.
        if (IsLocalPilot)
        {
            _predicted.Position = GlobalPosition;
            _predicted.LinearVelocity = Vector3.Zero;
        }
        GD.Print($"ship#{Net?.ShipNetId ?? 0} teleported to docking zone of " +
                 $"Station#{station.State?.StationNetId ?? 0} at {GlobalPosition}");
        return true;
    }

    /// <summary>Шов автотеста п.34: вызывает телепорт как серверный обработчик
    /// кнопки (тот же боевой код).</summary>
    public bool TestTeleportToDockingZoneForTest() => TeleportToDockingZoneCore();

    // ==================================================================
    // Шаг 2.2.3: камера-преследование и захват мыши
    // ==================================================================

}
