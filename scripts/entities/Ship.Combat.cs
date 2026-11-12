using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial Ship: вырезано из Ship.cs при partial-декомпозиции.
public partial class Ship : RigidBody3D
{
    private MeshInstance3D? _aimBeam;
    // Ствол из сцены — начало луча; null, если в сцене нет узла Muzzle (тогда
    // луч рисуется от центра корпуса).
    private Node3D? _muzzle;

    // ─── Шаг 3.4: серверная авторизация выстрела (В17) ──────────────────────
    // Гейты В17: (1) стреляющий = пилот корабля (тот же гейт, что у ввода);
    // (2) не чаще FireCooldownSec (иначе спам RPC = флуд); (3) присланный
    // aimPoint согласован с СЕРВЕРНЫМ лучом носа (-Basis.Z) по углу в допуске
    // AimToleranceM. Состояние гейтов существует только на авторитете.
    /// <summary>Минимальный интервал между принятыми выстрелами (сек), гейт
    /// В17 п.2. Сервер отсчитывает от момента последнего ПРИНЯТОго выстрела.</summary>
    [Export] public float FireCooldownSec = 0.2f;
    /// <summary>Угловой допуск (рад) сверки клиентского `aimPoint` с серверным
    /// лучом носа (В17 п.3): точка обязана лежать в конусе AimToleranceM вокруг
    /// серверного `-Basis.Z`. По умолчанию 0.15 рад ≈ 8.6°.</summary>
    [Export] public float AimToleranceM = 0.15f;

    private double _lastFireTimeSec;      // время последнего принятого выстрела
    private bool _hasLastFireTime;        // был ли хотя бы один принятый выстрел
    private uint _lastAcceptedFireTick;   // монотонность тика выстрела (как тик ввода)
    private bool _hasAcceptedFireTick;    // задан ли уже эталонный тик выстрела

    // ─── Шаг 3.5: серверный хитскан (В17 п.3, §3.5 п.3 — НЕ в обработчике RPC) ──
    /// <summary>Дальность хитскан-луча (м). Конец луча = серверный `Muzzle` +
    /// серверный `shipForward (-Basis.Z) * LaserRange`. Риск №3 плана: хитскан
    /// обязан читаться ПОСЛЕ шага физики — поэтому принятый выстрел кладётся в
    /// очередь и исполняется в `_PhysicsProcess` авторитета, а не в `AcceptFireFrom`.</summary>
    [Export] public float LaserRange = 400f;

    /// <summary>Урон лазера по мишени (шаг 3.6): Hp -= FireDamage при каждом
    /// подтверждённом сервером попадании. Единственный источник истины по урону —
    /// сервер; `Hp` уходит пирам ТОЛЬКО репликой (риск №5: второй источник истины
    /// запрещён — в `RpcOnHitConfirmed` `damage` нужен картинке, не счёту HP).</summary>
    [Export] public int FireDamage = 25;

    /// <summary>Принятые, но ещё не исполненные запросы выстрела: тик + мировая
    /// точка прицела (она уже прошла угловую сверку В17 п.3). Исполняются и
    /// очищаются только авторитетом в `_PhysicsProcess`.</summary>
    private readonly Queue<(uint tick, Vector3 aimPoint)> _pendingFires = new();

    /// <summary>Событие «попадание в мишень подтверждено сервером» (шаг 3.5):
    /// `(targetNetId, tick, hitPoint)`. Шов автотеста п.21-п.23; в шаге 3.6 его
    /// же сервер превратит в `RpcOnHitConfirmed` + урон. Станция (слой Station)
    /// попаданием в мишень НЕ считается — событие не стреляет.</summary>
    public event System.Action<int, uint, Vector3>? HitConfirmed;

    /// <summary>
    /// Шаг 4.8: серверная смерть корабля (Hp<=0 после урона от станции).
    /// По образцу ветки `target.Hp <= 0` у мишени (QueueFree + печать).
    /// Вызывается только с сервера-авторитета (_IntegrateForces/HandleStationContacts
    /// гейтятся авторитетом), поэтому сам метод авторитет не проверяет.
    /// Пилот снимается присваиванием PilotPeerId = 0 (не через SetPilot — корабль
    /// всё равно удаляется, рассылать нового пилота незачем). Пристёгнутый игрок
    /// отпускается в _ExitTree (§3.9 п.16) ДО удаления узла — капсула не виснет.
    /// Удаление корабля реплицируется спавнером (spawner-реплика QueueFree), поэтому
    /// у не-авторитета корабль исчезнет тем же путём, что и у сервера (п.22).
    /// </summary>
    private void ServerDieShip()
    {
        if (IsQueuedForDeletion() || !IsNodeReady())
            return; // уже умирает — не повторять

        PilotPeerId = 0; // п.21: после Hp=0 у сервера пилот снят (0 = не назначался)
        GD.Print($"ship#{Net?.ShipNetId ?? 0}: уничтожен (Hp<=0) — сервер удаляет узел");
        QueueFree();
    }

    /// <summary>
    /// Мышиная дельта от сборщика (шаг 2.2.2). Копим до физического шага:
    /// `InputEventMouseMotion` приходит чаще, чем 60 Гц, и терять её нельзя.
    ///
    /// Гейт — НЕ режим мыши: в headless `Input.MouseMode` не меняется (проба
    /// `tests/probes/ProbeMousePick.tscn`), поэтому гейт по нему нельзя ни
    /// проверить автотестом, ни использовать как единственный признак. Гейт —
    /// «курсор смотрит на корабль» (луч камеры проходит ближе `AimRadiusM` к
    /// центру корпуса): он работает и в `Captured` (виртуальный курсор в центре
    /// окна, камера смотрит на корпус → прицел активен), и в `Visible` (курсор
    /// на UI-панели → прицел отключён), и в headless.
    /// </summary>
    internal void HandleMouseMotion(InputEventMouseMotion m)
    {
        // Блок A (eva-plan): накопление курсора живёт в `PilotInput` — там же,
        // где его читает look-канал и (п.6) прицел-визуал/скафандр. Флаг
        // `Active` collector'а = `IsLocalPilot` (п.16: позиция курсора и бит
        // «курсор есть» пишутся под ОДНИМ гейтом), поэтому чужой пир не копит
        // прицел к чужому кораблю. `LastMousePx` пишется всегда: выстрел
        // (RequestFire) восстанавливает луч по последней позиции, даже если в
        // момент клика мышь не двигалась.
        _pilotInput.Active = IsLocalPilot;
        _pilotInput.InteractionMode = IsInteractionMode;
        _pilotInput.HandleMouse(m, ViewportSize);
    }

    /// <summary>Экранный курсор пилота (пиксели) — точка, из которой восстанавливают
    /// луч прицела (п.6 eva-plan: пиксель-пер-клик скафандра берёт ЛУЧ ИЗ ЭТОЙ ЖЕ
    /// камеры и тех же координат, второй copies быть не должно). Пусто/ноль, если
    /// мышь ни разу не приходила.</summary>
    public Vector2 AimCursorPx => _pilotInput.LastMousePx;

    /// <summary>Смотрит ли курсор на корабль: расстояние от центра корпуса до
    /// луча камеры, восстановленного из экранных координат курсора (метры).
    /// Без камеры гадаем нечем — считаем, что пилот целивается в свой корабль
    /// (в offline камера есть всегда, так что ветка — только страховка).</summary>
    private bool AimOnShip(Vector2 screenPos)
    {
        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam == null)
            return true;

        Vector3 o = cam.ProjectRayOrigin(screenPos);
        Vector3 d = cam.ProjectRayNormal(screenPos);
        Vector3 w = GlobalPosition - o;
        float t = w.Dot(d);
        return (o + d * t - GlobalPosition).Length() <= AimRadiusM;
    }

    // ───────────────────────── Шаг 3.3: выстрел (визуал) ──────────────────────
    //
    // Точка входа выстрела. Пока (шаг 3.3) только визуал: луч от ствола до точки
    // прицела, картинка живёт AimingBeamLifetime секунд и удаляется сама. Реального
    // подсчёта попадания здесь НЕТ (хитскан — шаг 3.5, урон/подтверждение — шаг 3.6).
    // Гейты ровно те же, что у доворота носа: локальный пилот + режим пилотирования
    // + «курсор смотрит на корабль». Иначе ЛКМ, например в режиме взаимодействия
    // (клики по UI), породила бы ложные лучи.

    /// <summary>Гейты выстрела, НЕ связанные с прицельным лучом камеры:
    /// «локальный пилот» + «не режим взаимодействия». Разделены с AimOnShip
    /// именно для шва п.17: в headless камера есть (current=true у камеры
    /// игрока/фолбэка), и экранный курсор (0,0) в неё не целится, поэтому
    /// aim-гейт блокировал бы seam. AimOnShip — тот же гейт, что у доворота
    /// носа, его корректность покрыта стадией 2; здесь проверяем сам луч.</summary>
    private bool FireGatesNonAim() => IsLocalPilot && !IsInteractionMode;

    /// <summary>Запрос выстрела локального пилота (ЛКМ через ShipMouseCatcher).
    /// Гейты + визуал луча. Публично — чтобы тест мог позвать тот же путь кода
    /// без эмуляции клика (шов <see cref="TestFireRequest"/>).</summary>
    public void RequestFire()
    {
        if (!FireGatesNonAim())
            return;
        if (!AimOnShip(_pilotInput.LastMousePx))
            return;
        ShowAimingBeam();
        TryFire();
    }

    /// <summary>Мировая точка выстрела (для сетевого гейта В17 п.3 и локального
    /// хитскана): ствол + серверный/локальный нос `-Basis.Z` на длину луча. Берём
    /// НОС корабля, а не курсорный луч камеры: сервер сверяет `aimPoint` со своим
    /// `-Basis.Z`, поэтому намерение по носу проходит допуск `AimToleranceM`
    /// надёжнее, чем экранный луч чужой камеры.</summary>
    private Vector3 AimPointWorld()
        => BeamOriginGlobal() + (-GlobalTransform.Basis.Z) * AimingBeamLength;

    /// <summary>Запуск выстрела после визуала (исправленный баг: раньше `RequestFire`
    /// рисовал только луч и никогда не звал хитскан — урон не проходил ни в offline,
    /// ни в сети). Развилка по роли процесса (<see cref="NetworkManager.IsAuthoritySide"/>):
    ///  - авторитетная сторона (offline/хост-пилот): тот же кулдаун `FireCooldownSec`,
    ///    что у серверного гейта, и прямой `Enqueue` в очередь хитскана (исполнится в
    ///    `_PhysicsProcess`); в offline `AcceptFireFrom` отсечён гейтом `IsServer`,
    ///    поэтому локальный путь обязателен;
    ///  - клиент-пилот: `SubmitFireToServer` (надёжный RPC, канал 1) — сервер проверит
    ///    намерение (В17) и сам посчитает хитскан/урон (единый источник истины).</summary>
    private void TryFire()
    {
        var nm = NetworkManager.Instance;
        if (nm == null || Net == null || Net.ShipNetId == 0)
            return; // нет адреса — выстрел нечем ни засчитать, ни адресовать

        // Кулдаун на обеих сторонах (как гейт В17 п.2) — клиент так же троттлит RPC.
        double now = Time.GetTicksMsec() / 1000.0;
        if (_hasLastFireTime && now - _lastFireTimeSec < FireCooldownSec)
            return;

        Vector3 aim = AimPointWorld();
        if (nm.IsAuthoritySide)
        {
            uint tick = (uint)Mathf.Max(0, Net.SimTick);
            _lastAcceptedFireTick = tick;
            _hasAcceptedFireTick = true;
            _lastFireTimeSec = now;
            _hasLastFireTime = true;
            AcceptedFireCount++;
            ShotsFired++;
            _pendingFires.Enqueue((tick, aim));
        }
        else
        {
            // Клиент: сервер засчитает выстрел; локально запоминаем время, чтобы
            // троттлить спам RPC с той же периодичностью, что гейт сервера.
            _lastFireTimeSec = now;
            _hasLastFireTime = true;
            SubmitFireToServer(aim);
        }
    }

    /// <summary>Тот же путь кода, что у ЛКМ, но БЕЗ aim-гейта (шов п.17): мышь
    /// в headless эмулировать нельзя, а current-камера перехватывает прицел по
    /// пикселю. Seam оставляет гейты «локальный пилот» и «взаимодействие» —
    /// именно их и проверяет п.17 (в Interaction луча нет).</summary>
    public void TestFireRequest()
    {
        if (!FireGatesNonAim())
            return;
        ShowAimingBeam();
    }

    // ─── Шаг 3.4: RPC выстрела + серверная авторизация (В15 п.1, В17) ────────

    /// <summary>Клиент-пилот: послать серверу намерение выстрелить в мировую
    /// точку `aimPoint` (та, куда смотрит прицел). Сервер проверит намерение и
    /// сам решит, был ли выстрел (хитскан — шаг 3.5). Адресуется по ShipNetId.</summary>
    private void SubmitFireToServer(Vector3 aimPoint)
    {
        if (Net == null || Net.ShipNetId == 0)
            return; // нет адреса — выстрел адресовать нечем
        uint tick = (uint)Mathf.Max(0, Net.SimTick);
        RpcId(1, nameof(RpcRequestFire), Net.ShipNetId, tick, aimPoint);
    }

    /// <summary>Шов автеста (по образцу `TestSubmitInput`, §3.9 п.15): «запрос
    /// выстрела пришёл от пира X» без UDP. Тест зовёт тот же серверный гейт
    /// `AcceptFireFrom`, что и боевой RPC — проверяется боевой код, а не копия.
    /// `shipNetId` берётся из адреса этого корабля (RPC адресуются по адресу).</summary>
    public bool TestFire(int senderPeer, uint tick, Vector3 aimPoint)
        => AcceptFireFrom(senderPeer, Net?.ShipNetId ?? 0, tick, aimPoint);

    /// <summary>Серверный приём запроса выстрела от пилота (гейты В17). Атрибут
    /// как у `SubmitInputToServer` (В15 п.1): AnyPeer, CallLocal=false, надёжный,
    /// канал 1 — выстрелы редки и терять их нельзя, в отличие от кадров ввода.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcRequestFire(int shipNetId, uint tick, Vector3 aimPoint)
    {
        AcceptFireFrom(Multiplayer.GetRemoteSenderId(), shipNetId, tick, aimPoint);
    }

    /// <summary>Серверные гейты выстрела + учёт. Возвращает true, если выстрел
    /// принят. Гейты (В17): только сервер и авторитет; адрес корабля; отправитель
    /// = пилот; монотонность тика; кулдаун `FireCooldownSec`; угловая сверка
    /// `aimPoint` с СЕРВЕРНЫМ лучом носа (`-Basis.Z`) в допуске `AimToleranceM`.
    /// Счётчики принявших/отклонённых — для assert'ов п.18-п.20.</summary>
    private bool AcceptFireFrom(int senderPeer, int shipNetId, uint tick, Vector3 aimPoint)
    {
        if (NetworkManager.Instance is not { IsServer: true })
            return false; // только сервер авторизует выстрел (В14=B)
        if (Net == null || Net.ShipNetId != shipNetId)
            return false; // выстрел про другой корабль (адрес важнее пути)
        if (!HasAuthority)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: запрос выстрела пришёл пиру, который не " +
                        $"является авторитетом корабля (auth = {GetMultiplayerAuthority()}) — отказ");
            RejectedFireCount++;
            return false;
        }

        if (senderPeer != PilotPeerId)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: запрос выстрела от пира {senderPeer}, а пилот " +
                        $"= {PilotPeerId} — отказ (В17 п.1)");
            RejectedFireCount++;
            return false;
        }

        // Монотонность тика по образцу ввода: устаревший/дубликат выстрела — отказ.
        if (_hasAcceptedFireTick && tick <= _lastAcceptedFireTick)
        {
            RejectedFireCount++;
            return false;
        }

        // Гейт В17 п.2 — кулдаун: не чаще FireCooldownSec от последнего ПРИНЯТОго.
        // Отсчёт от серверных часов (одни часы у авторитета), поэтому тест
        // укладывается в допуск, чередуя тики и кадры.
        if (_hasLastFireTime)
        {
            double now = Time.GetTicksMsec() / 1000.0;
            if (now - _lastFireTimeSec < FireCooldownSec)
            {
                RejectedFireCount++;
                return false;
            }
        }

        // Гейт В17 п.3 — сверка намерения с СЕРВЕРНЫМ лучом носа. Сервер стреляет
        // по своему `-Basis.Z`, а чужая точка годится лишь подтвердить намерение:
        // если вектор к aimPoint вне конуса AimToleranceM вокруг носа — отказ.
        Vector3 serverForward = -GlobalTransform.Basis.Z;
        Vector3 toAim = aimPoint - BeamOriginGlobal();
        if (toAim.LengthSquared() < 1e-6f ||
            serverForward.AngleTo(toAim.Normalized()) > AimToleranceM)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: aimPoint вне допуска {AimToleranceM:F3} рад " +
                        $"от серверного носа — отказ (В17 п.3)");
            RejectedFireCount++;
            return false;
        }

        // Все гейты пройдены — выстрел принят. Кладём в очередь хитскана (риск
        // №3 плана: сам луч исполнится в `_PhysicsProcess` авторитета, а не здесь),
        // урон и RPC подтверждения добавит шаг 3.6.
        _lastAcceptedFireTick = tick;
        _hasAcceptedFireTick = true;
        _lastFireTimeSec = Time.GetTicksMsec() / 1000.0;
        _hasLastFireTime = true;
        AcceptedFireCount++;
        ShotsFired++;
        _pendingFires.Enqueue((tick, aimPoint));
        return true;
    }

    /// <summary>Сколько выстрелов принял/отклонил сервер (автотесты п.18-п.20) и
    /// сколько выстрелов вообще засчитано (счётчик для дебаг-строки шага 3.7).</summary>
    public int AcceptedFireCount { get; private set; }
    public int RejectedFireCount { get; private set; }
    public int ShotsFired { get; private set; }
    /// <summary>Число подтверждений попадания (счётчик для строки «Fire: shots/hits/rejected», шаг 3.7).</summary>
    public int HitsRegistered { get; private set; }

    // ═══════════ Этап 5 (шаги 5.5/5.6): запрос стыковки (F) ═══════════
    // Копия связки RequestFire/SubmitFireToServer/RpcRequestFire/AcceptFireFrom
    // (В15 п.1, В17): клиент-пилот шлёт намерение, сервер гейтит и решает.

    /// <summary>Хитскан нашёл тело с `TargetState`, но его НЕТ в реестре мишеней —
    /// попадание проигнорировано (п.25, риск №7: «мишень удалили, пока луч летел»).
    /// Счётчик — детерминированный шов теста (ловля warning'а хрупка).</summary>
    public int GhostHitsIgnored { get; private set; }

    /// <summary>Начало луча — ствол (Muzzle), если он есть в сцене; иначе корпус.</summary>
    private Vector3 BeamOriginGlobal()
        => (_muzzle ?? (Node3D)this).GlobalPosition;

    /// <summary>Серверный хитскан принятых выстрелов (шаг 3.5, риск №3 плана).
    /// Вызывается из `_PhysicsProcess` авторитета — то есть ПОСЛЕ шага физики,
    /// когда `PhysicsDirectSpaceState3D` видит актуальные позы тел, а не позапрошлого
    /// кадра. Луч: от серверного `Muzzle` по серверному `shipForward (-Basis.Z)`
    /// на `LaserRange`; `Basis` сервера уже содержит прицел (он интегрировал
    /// LookYaw/LookPitch пилота), поэтому `aimPoint` клиента тут не нужен — он
    /// проверен на угол в гейте В17 п.3. Маска `LaserMask` (Station | Target).
    /// Тело попадания сопоставляется с мишенью ЧЕРЕЗ узел-компонент `TargetState`
    /// (а не по имени/пути): есть `TargetState` — это мишень (п.21, событие
    /// `HitConfirmed`); станция без `TargetState` (TestStand, слой Station) —
    /// попаданием в цель НЕ считается (п.22); пустота — `IntersectRay` пуст (п.23).</summary>
    private void ProcessPendingFires()
    {
        if (_pendingFires.Count == 0)
            return;

        PhysicsDirectSpaceState3D? space = GetWorld3D().DirectSpaceState;
        if (space == null)
        {
            GD.PushWarning($"ship#{Net?.ShipNetId ?? 0}: нет PhysicalSpace3D — хитскан пропущен");
            _pendingFires.Clear();
            return;
        }

        Vector3 from = BeamOriginGlobal();      // серверный ствол (не клиентский!)
        Vector3 fwd = -GlobalTransform.Basis.Z; // серверный прицел (уже в Basis)
        Vector3 to = from + fwd * LaserRange;

        // Убираем из трассы собственное тело, чтобы луч не стартовал внутри корпуса
        // (ствол лежит на передней грани и может её зацепить).
        var exclude = new Godot.Collections.Array<Rid> { GetRid() };

        while (_pendingFires.Count > 0)
        {
            (uint tick, Vector3 _) = _pendingFires.Dequeue();
            var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.LaserMask, exclude);
            Godot.Collections.Dictionary hit = space.IntersectRay(query);
            if (hit.Count == 0)
                continue; // п.23: выстрел в пустоту — подтверждения нет

            // result.GetCollider(): попадание → тело (PhysicsBody3D), не имя/путь.
            if (hit["collider"].AsGodotObject() is not PhysicsBody3D body)
                continue;

            // ─── фикс «нет урона по станции и кораблям»: хитскан раньше
            // признавал ТОЛЬКО мишени (TargetState) и молча пропускал всё
            // остальное. Теперь три kinds: мишень (TargetState), чужой корабль
            // (ShipHealthState) и станция (StationState). Событие HitConfirmed,
            // счётчик HitsRegistered, реестровая сверка и RPC по мишеням — НЕ
            // изменились: автотесты п.21-п.25 Stage3 завязаны на них и на том,
            // что TestStand (слой Station, БЕЗ StationState) попаданием не
            // считается — у стенда нет ни одного буфера состояния.
            TargetState? state = body.GetNodeOrNull<TargetState>("TargetState");
            if (state != null && state.TargetNetId != 0)
            {
                ApplyLaserToTarget(state, hit["position"].AsVector3(), tick);
                continue;
            }

            var health = body.GetNodeOrNull<ShipHealthState>("ShipHealthState");
            if (health != null && health.MaxHp > 0)
            {
                ApplyLaserToShip(health, hit["position"].AsVector3(), tick);
                continue;
            }

            var station = body.GetNodeOrNull<StationState>("StationState");
            if (station != null && station.StationNetId != 0)
            {
                ApplyLaserToStation(station, hit["position"].AsVector3(), tick);
                continue;
            }
            // Ни одного буфера состояния (TestStand, декорации) — промах «в
            // стену»: попадания нет, урона нет (п.22/п.23 Stage3).
        }
    }

    /// <summary>Шаг 3.6 (поведение без изменений): подтверждённое попадание по
    /// мишени — сверка с реестром (риск №7), урон, RPC-картинка, событие теста,
    /// уничтожение при Hp&lt;=0.</summary>
    private void ApplyLaserToTarget(TargetState target, Vector3 hitPoint, uint tick)
    {
        // Шаг 3.6, риск №7: реестр — единственный владелец живых мишеней.
        // Если мишень уже снята (удаляется в этом же кадре / гонка с чужим
        // выстрелом), FindTarget вернёт null — это warning, а не исключение
        // (п.25). Урон наносим по записи реестра, а не по «сырому» телу.
        var nm = NetworkManager.Instance;
        if (nm != null && nm.FindTarget(target.TargetNetId) == null)
        {
            GhostHitsIgnored++;
            GD.PushWarning($"ship#{Net.ShipNetId}: хитскан в мишень " +
                           $"#{target.TargetNetId}, её НЕТ в реестре — попадание " +
                           $"игнорируется (риск №7: удаление во время полёта)");
            return;
        }

        HitsRegistered++;

        // ─── шаг 3.6: урон наносит ТОЛЬКО сервер (единый источник истины) ───
        target.Hp = System.Math.Max(0, target.Hp - FireDamage);

        // Рассылка подтверждения всем пирам (Reliable, канал 1): картинке —
        // вспышка в hitPoint; счёт HP — репликой Hp, параметр damage здесь
        // только для визуала/логов (риск №5: не второй источник истины).
        // Offline (peer == null) — рассылать некому: там работает локальный
        // шов HitConfirmed и локальная вспышка ниже.
        if (Multiplayer.GetMultiplayerPeer() != null)
            Rpc(nameof(RpcOnHitConfirmed), target.TargetNetId, FireDamage,
                hitPoint, Net.ShipNetId, tick);

        GD.Print($"ship#{Net.ShipNetId}: попадание в мишень #{target.TargetNetId} " +
                 $"(tick={tick}, HP {target.Hp}/{target.MaxHp})");

        // Локальный шов теста/интерфейса (п.21-23 слушают именно его).
        HitConfirmed?.Invoke(target.TargetNetId, tick, hitPoint);

        // Уничтожение: сервер QueueFree — MultiplayerSpawner сам снимет узел
        // у всех пиров; реестр чистит _ExitTree мишени.
        if (target.Hp <= 0)
        {
            GD.Print($"мишень #{target.TargetNetId} уничтожена (стрелок " +
                     $"ship#{Net.ShipNetId})");
            target.GetParent()?.QueueFree(); // корень Target (RigidBody3D)
        }

        // Вспышка и на сервере (хост видит мир; dedicated — безвредно).
        ShowHitFlash(hitPoint);
    }

    /// <summary>Фикс урона: попадание лазера по чужому кораблю. Урон гасится
    /// щитом (<see cref="ShipHealthState.Shield"/>), остаток уходит в Hp;
    /// пишущий — только авторитет (метод вызывается из ProcessPendingFires).
    /// GodMode — вспышка без урона. Hp&lt;=0 — серверная смерть тем же путём,
    /// что урон от столкновения со станцией (<see cref="ServerDieShip"/>).</summary>
    private void ApplyLaserToShip(ShipHealthState health, Vector3 hitPoint, uint tick)
    {
        int shipNetId = health.Owner?.Net?.ShipNetId ?? 0;
        if (health.GodMode)
        {
            ShowHitFlash(hitPoint);
            GD.Print($"ship#{Net.ShipNetId}: попадание в корабль #{shipNetId} " +
                     $"— GodMode, урона нет (tick={tick})");
            return;
        }

        int remaining = FireDamage;
        if (health.Shield > 0)
        {
            int absorbed = System.Math.Min(health.Shield, remaining);
            health.Shield -= absorbed;
            remaining -= absorbed;
        }
        health.Hp = System.Math.Max(0, health.Hp - remaining);
        health.LastDamage = FireDamage;
        health.DamageTakenCount++;
        health.LastDamageTimeSec = Time.GetTicksMsec() / 1000.0;

        // Картинка пирам — тот же паттерн, что RpcOnHitConfirmed: параметры для
        // вспышки, счёт HP/щита — только репликой (риск №5).
        if (Multiplayer.GetMultiplayerPeer() != null)
            Rpc(nameof(RpcOnHitOnShip), shipNetId, FireDamage, hitPoint,
                Net.ShipNetId, tick);
        GD.Print($"ship#{Net.ShipNetId}: попадание в корабль #{shipNetId} " +
                 $"(tick={tick}, щит {health.Shield}/{health.MaxShield}, " +
                 $"HP {health.Hp}/{health.MaxHp})");
        HudEventBus.Report($"Damage {FireDamage} — ship#{shipNetId} " +
                           $"shield {health.Shield}/{health.MaxShield}, " +
                           $"hull {health.Hp}/{health.MaxHp}");
        ShowHitFlash(hitPoint);

        if (health.Hp <= 0 && health.Owner != null && health.Owner.Net != null &&
            health.Owner.Net.ShipNetId != 0 && Net.ShipNetId != health.Owner.Net.ShipNetId)
        {
            GD.Print($"ship#{health.Owner.Net.ShipNetId} уничтожен выстрелом " +
                     $"ship#{Net.ShipNetId}");
            health.Owner.ServerDieShip();
        }
    }

    /// <summary>Фикс урона: попадание лазера по станции. Щит станции
    /// (<see cref="StationState.Shield"/>) гасит урон первым; станция разрушаема
    /// до Hp=0, но НЕ удаляется — это постоянный объект мира.</summary>
    private void ApplyLaserToStation(StationState station, Vector3 hitPoint, uint tick)
    {
        int remaining = FireDamage;
        if (station.Shield > 0)
        {
            int absorbed = System.Math.Min(station.Shield, remaining);
            station.Shield -= absorbed;
            remaining -= absorbed;
        }
        station.Hp = System.Math.Max(0, station.Hp - remaining);

        if (Multiplayer.GetMultiplayerPeer() != null)
            Rpc(nameof(RpcOnHitOnStation), station.StationNetId, FireDamage,
                hitPoint, Net.ShipNetId, tick);
        GD.Print($"ship#{Net.ShipNetId}: попадание в станцию #{station.StationNetId} " +
                 $"(tick={tick}, щит {station.Shield}/{station.MaxShield}, " +
                 $"HP {station.Hp}/{station.MaxHp})");
        HudEventBus.Report($"Damage {FireDamage} — station#{station.StationNetId} " +
                           $"shield {station.Shield}/{station.MaxShield}, " +
                           $"hull {station.Hp}/{station.MaxHp}");
        ShowHitFlash(hitPoint);
    }

    /// <summary>Шаг 3.6: подтверждение попадания от сервера всем пирам.
    /// `RpcMode.Authority` — право отправлять только у авторитета узла (сервер,
    /// id=1); Reliable, канал 1 (как служебные события). Параметры — КАРТИНКЕ:
    /// вспышка в `hitPoint`; счёт HP — только репликой `Hp` (риск №5: параметр
    /// `damage` здесь не трогает локальный Hp — второго источника истины нет).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcOnHitConfirmed(int targetNetId, int damage, Vector3 hitPoint,
                                  int shooterNetId, uint tick)
    {
        // Только картинка: вспышка у чужого выстрела тоже видна (в т.ч. стрелку).
        ShowHitFlash(hitPoint);
        GD.Print($"[hit] ship#{shooterNetId} → мишень #{targetNetId}: " +
                 $"-{damage} (HP реплицируется), tick={tick}");
    }

    /// <summary>Фикс урона: картинка попадания в чужой корабль для всех пиров.
    /// Тот же контракт, что у <see cref="RpcOnHitConfirmed"/>: только вспышка,
    /// счёт Hp/щита — исключительная реплика ShipHealthState (риск №5).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcOnHitOnShip(int shipNetId, int damage, Vector3 hitPoint,
                               int shooterNetId, uint tick)
    {
        ShowHitFlash(hitPoint);
        GD.Print($"[hit] ship#{shooterNetId} → корабль #{shipNetId}: " +
                 $"-{damage} (Hp/щит реплицируются), tick={tick}");
    }

    /// <summary>Фикс урона: картинка попадания в станцию. Контракт тот же:
    /// Hp/щит станции — только репликой StationState.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcOnHitOnStation(int stationNetId, int damage, Vector3 hitPoint,
                                  int shooterNetId, uint tick)
    {
        ShowHitFlash(hitPoint);
        GD.Print($"[hit] ship#{shooterNetId} → станция #{stationNetId}: " +
                 $"-{damage} (Hp/щит реплицируются), tick={tick}");
    }

    /// <summary>Краткая эмиссивная сфера в точке попадания (картинка, шаг 3.6).
    /// Живёт ~0.3 с и удаляется сама — приём тот же, что у луча прицела.</summary>
    private void ShowHitFlash(Vector3 at)
    {
        var parent = GetParent();
        if (parent == null || !IsInsideTree())
            return;
        var flash = new MeshInstance3D
        {
            Name = "HitFlash",
            Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1f, 0.9f, 0.4f, 1f),
                EmissionEnabled = true,
                Emission = new Color(1f, 0.8f, 0.2f, 1f),
                EmissionEnergyMultiplier = 2f,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        };
        parent.AddChild(flash);
        flash.GlobalPosition = at;
        var timer = GetTree()?.CreateTimer(0.3);
        if (timer != null)
        {
            timer.Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(flash)) flash.QueueFree();
            };
        }
        else
        {
            flash.QueueFree();
        }
    }

    /// <summary>Построить/обновить визуальный луч от ствола до точки прицела.
    /// Точка прицела = луч камеры через курсор, умноженный на длину луча (пока без
    /// пересечения с мишенями — это добавит шаг 3.5). Без камеры (headless) —
    /// просто вперёд по носу. Узел называется BeamVisual — на него смотрит тест п.17.</summary>
    private void ShowAimingBeam()
    {
        Vector3 start = BeamOriginGlobal();
        Vector3 fwd = -GlobalTransform.Basis.Z; // тот же курс, что у серверного хитскана

        // Фиксируем КОНЕЦ в мировом луче в момент выстрела (а не «дочерним узлом,
        // который наследует поворот корабля»): раньше узел был обычным child'ом
        // корабля, поэтому при довороте носа к курсору (PilotAim) луч «ехал» за
        // курсором. Направление — вдоль НОСА (`fwd`), как у реального выстрела,
        // дальше луч обрезаем первым препятствием по `LaserMask` (визуал = трасса
        // выстрела), иначе фиксированная длина `AimingBeamLength`.
        Vector3 end = start + fwd * AimingBeamLength;
        var space = GetWorld3D()?.DirectSpaceState;
        if (space != null)
        {
            var exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var query = PhysicsRayQueryParameters3D.Create(
                start, start + fwd * LaserRange, CollisionLayers.LaserMask, exclude);
            Godot.Collections.Dictionary hit = space.IntersectRay(query);
            if (hit.Count > 0)
                end = hit["position"].AsVector3(); // зафиксированная точка попадания
        }

        Vector3 dir = end - start;
        float len = dir.Length();
        if (len < 0.001f)
            return;

        // Узел живёт фиксированное время и удаляется сам (SceneTreeTimer). Новый
        // выстрел убирает старый луч, чтобы не плодить узлы при спаме ЛКМ.
        _aimBeam?.QueueFree();
        var beam = new MeshInstance3D
        {
            Name = "BeamVisual",
            // TopLevel: НЕ наследует трансформацию родителя (корабля) — узел
            // остаётся child'ом корабля (тест п.17 ищет BeamVisual под ним), но
            // стоит в МИРОВЫХ координатах и не поворачивается вместе с носом.
            TopLevel = true,
        };
        var cyl = new CylinderMesh
        {
            TopRadius = AimingBeamRadius,
            BottomRadius = AimingBeamRadius,
            Height = len,
        };
        beam.Mesh = cyl;
        // Светящийся белый материал — картинка, не физический объект (нет коллизии).
        var mat = new StandardMaterial3D
        {
            EmissionEnabled = true,
            Emission = new Color(1, 1, 1),
            EmissionEnergyMultiplier = 2.0f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        beam.MaterialOverride = mat;

        AddChild(beam);
        // Ориентация: локальная Y цилиндра — вдоль луча. LookAt даёт -Z, поэтому
        // доворачиваем базис: Y←нормаль луча. start/end мировые и ЗАФИКСИРОВАНЫ —
        // луч стоит на месте, куда стреляли, пока корабль доворачивается.
        beam.GlobalPosition = (start + end) * 0.5f;
        Vector3 up = dir.Normalized();
        // Строим базис, где Y=up, а X/Z — любые перпендикуляры (радиус симметричен).
        Vector3 refAxis = Mathf.Abs(up.Dot(Vector3.Right)) < 0.9f ? Vector3.Right : Vector3.Up;
        Vector3 x = up.Cross(refAxis).Normalized();
        Vector3 z = x.Cross(up).Normalized();
        beam.GlobalTransform = new Transform3D(new Basis(x, up, z), beam.GlobalPosition);

        _aimBeam = beam;

        // Самоудаление: вдвое короче прежнего (`AimingBeamLifetime / 2`) — вспышка
        // выстрела короткая, не «висит» до конца кулдауна. Замыкание ловит конкретный
        // узел: если его уже сняли раньше (новый выстрел) — проверка IsValid.
        double secs = AimingBeamLifetime * 0.5;
        var timer = GetTree()?.CreateTimer(secs);
        if (timer != null)
        {
            timer.Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(beam))
                    beam.QueueFree();
                if (ReferenceEquals(_aimBeam, beam))
                    _aimBeam = null;
            };
        }
    }

}
