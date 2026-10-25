using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Корабль (сцена scenes/Ship.tscn). Сети — шаг 2 переделки стадии 1 (§3.1, §2.5).
///
/// Разделение «истина / картинка»:
///   - тело `Ship` симулирует ТОТ пир, который является мультиплеер-авторитетом
///     узла; все остальные пиры держат тело замороженным (`FreezeMode.Static`) и
///     лишь интерполируют картинку `ShipHull` по реплике `NetState`;
///   - проверять надо `IsMultiplayerAuthority()`, а не
///     `GetMultiplayerAuthority() == 1`: при dedicated-модели `1` бывает и у
///     серверного тела («истина — я»), и у клиента («истина на сервере, я только
///     интерполирую»), а при смене пилота номер вообще меняется в рантайме (§4 В3=B).
///     В offline авторитет = 1 и `IsMultiplayerAuthority()` = true, поэтому
///     поведение стадии 1 сохраняется целиком.
///   - `ApplyImpulse` и прочие силовые вызовы — только авторитету (§2.5 п.6);
///     в offline цепочка сворачивается в один локальный вызов.
///
/// AttachPoints — узлы, к которым может быть пристёгнут игрок (см. Player.AttachTo).
/// Они лежат под `ShipHull`: игрок в кабине обязан совпадать с картинкой, а не с
/// физическим корпусом (решение §3.3), поэтому физически игрок — часть иерархии
/// картинки, а не тела.
/// </summary>
public partial class Ship : RigidBody3D
{
    /// <summary>Буфер сетевого состояния (истина для репликации). Обязателен:
    /// без него корабль некому реплицировать (§3.2). Публичен: реестр
    /// NetworkManager читает из него ShipNetId.</summary>
    public NetState? Net { get; private set; }

    /// <summary>Истинный авторитет узла на этом пиру (кэш ApplyAuthority).</summary>
    protected bool HasAuthority { get; private set; }

    /// <summary>Последний известный пилот корабля (id пира; 1 = сервер/автопилот).
    /// Обновляется на всех пирах через ApplyRemoteAuthority; нужен, чтобы при
    /// отстёжке пилота сервер мог вернуть авторитет именно этому кораблю (§3.5 п.8).</summary>
    public int PilotPeerId { get; private set; } = 1;

    /// <summary>Последний принятый сервером SimTick пилотского репорта: репорты
    /// старше отбрасываются — окно рассинхрона при смене авторитета (§4 В3 п.6).</summary>
    private int _lastAcceptedReportTick;

    // --- интерполяция картинки (шаг 8, §2.5/§3.3) ---------------------------
    //
    // Не-авторитет не симулирует корабль (тело заморожено), поэтому его задача —
    // нарисовать плавную КАРТИНКУ по приходящим снимкам NetState. Делается это на
    // ShipHull (обычный Node3D), а не на трансформе тела: прямая запись в
    // RigidBody3D.Transform сломала бы интегрирование и столкновения (§1.6).
    //
    // Метод — интерполяция с задержкой (snapshot interpolation): рисуем картинку
    // на `InterpDelay` секунд в прошлом, между двумя ближайшими снимками. Задержка
    // нужна, чтобы между приходом пакетов (30 Гц) всегда было чем интерполировать.
    // «Откат ввода» (client-side prediction) здесь не применяется: не-авторитет не
    // подаёт на корабль локального ввода (пилотирует только авторитет, §2.3),
    // поэтому откатывать нечего — достаточно интерполяции.

    /// <summary>Задержка интерполяции, сек. Больше 2×ReplicationInterval (1/30),
    /// чтобы кадр всегда попадал внутрь окна двух снимков.</summary>
    private const double InterpDelay = 0.1;

    /// <summary>Буфер снимков (кольцевой по времени): (момент приёма, позиция,
    /// поворот). Храним локальное время приёма, потому что снимки приходят
    /// неравномерно, а интерполировать надо по местным часам клиента.</summary>
    private readonly List<Snapshot> _interpBuf = new();
    private int _lastNetTickSeen = -1;

    private readonly struct Snapshot
    {
        public readonly double Time;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public Snapshot(double time, Vector3 position, Quaternion rotation)
        {
            Time = time;
            Position = position;
            Rotation = rotation;
        }
    }

    public override void _Ready()
    {
        GravityScale = 0f;
        LinearDamp = 0f;
        AngularDamp = 1.5f;
        // CustomIntegrator = true включится на этапе 5 (пружина стыковки в _IntegrateForces),
        // и только авторитетному пиру (§3.1 п.7).

        CollisionLayer = CollisionLayers.Ship;
        CollisionMask = CollisionLayers.ShipMask;

        Net = GetNodeOrNull<NetState>("NetState");

        // §3.1 п.8: корабль с нулевым адресом физически живёт в сцене, но все его
        // RPC (репорт NetState, AssignShipAuthority, RequestAttach) уходят в никуда,
        // и выглядит это как «физика сломалась». Ошибка — громкая.
        if (Net == null || Net.ShipNetId == 0)
        {
            GD.PushError("Ship: у NetState нет ShipNetId — сетевой адрес не назначен " +
                         "(в сети сервер обязан выдать его до AddChild, §4 В4 п.3). " +
                         "RPC корабля будут теряться.");
        }

        ApplyAuthority();

        // Ключ источника — Ship#{ShipNetId} (§3.1 п.6): при нескольких кораблях два
        // узла с именем "Ship" зальют в шину один ключ, и половина мира исчезнет из
        // панели. В offline адрес = 1, поэтому и там строка помечена Ship#1.
        DebugInfoBus.RegisterInfoSource($"Ship#{Net?.ShipNetId ?? 0}", () =>
            $"ship#{Net?.ShipNetId ?? 0}: |v| = {LinearVelocity.Length():F2} m/s, " +
            $"|w| = {AngularVelocity.Length():F2} rad/s, " +
            $"mass = {Mass:F1}, " +
            // Без auth/frozen/|err| реконсиляцию и смену авторитета отлаживать нечем (§3.1 п.6).
            $"auth = {GetMultiplayerAuthority()}, frozen = {Freeze}, " +
            $"netid = {Net?.ShipNetId ?? 0}, " +
            $"|err| = {(Net == null ? 0f : Net.NetPosition.DistanceTo(GlobalPosition)):F3}");

        // Реестр кораблей по адресу: RPC про корабль идут по ShipNetId, не по пути (§4 В4 п.3).
        if (Net != null && Net.ShipNetId != 0)
        {
            NetworkManager.Instance?.RegisterShip(this);

            // §4 В4 п.2: AssignShipAuthority мог прийти раньше, чем спавнер создал
            // у этого пира узел корабля, — забираем отложенное назначение сейчас.
            if (NetworkManager.Instance is { } nm &&
                nm.TryGetPendingAuthority(Net.ShipNetId, out int pending) &&
                pending != GetMultiplayerAuthority())
                ApplyRemoteAuthority(pending);
        }

        // Кнопка для проверки инерции: короткий импульс вперёд, дальше тело летит само.
        // Только авторитету: не-авторитет не имеет права вызывать ApplyImpulse (§2.5 п.6).
        // В offline авторитет есть у всех — кнопка остаётся на своём месте.
        if (HasAuthority)
        {
            DebugInfoBus.RegisterDebugButton("Nudge Ship", NudgeShip);
        }
    }

    public override void _ExitTree()
    {
        // Снимаем источник отладки по ключу Ship#{ShipNetId}: лямбда захватывает
        // этот узел, и после освобождения корабля (DespawnShip, §3.9 п.16) шина
        // звала бы мёртвую лямбду каждый кадр → ObjectDisposedException (§3.11 п.9).
        DebugInfoBus.RemoveInfoSource($"Ship#{Net?.ShipNetId ?? 0}");
        DebugInfoBus.RemoveDebugButton("Nudge Ship");

        // §3.9 п.16: игрок, пристёгнутый к точкам этого корабля, — узел внутри
        // ShipHull/AttachPoints, и при удалении корабля был бы удалён вместе с ним.
        // Сначала отпускаем таких игроков в мировой корень (точка крепления к этому
        // моменту уже исчезнет из реестра: AttachPoint._ExitTree идёт раньше, чем
        // освобождается корабль), потом снимаем регистрацию.
        if (Net != null && Net.ShipNetId != 0)
        {
            foreach (var node in GetTree().GetNodesInGroup("player"))
            {
                if (node is Player p && GodotObject.IsInstanceValid(p) &&
                    p.IsAttached && p.AttachedTo!.OwnerNetId == Net.ShipNetId)
                {
                    GD.Print($"ship#{Net.ShipNetId}: удаляется, пристёгнутый игрок " +
                             "отстёгнут до удаления узла (§3.9 п.16)");
                    p.Detach();
                }
            }
            NetworkManager.Instance?.UnregisterShip(this);
        }
    }

    /// <summary>Смена авторитета, инициированная сервером (AssignShipAuthority,
    /// §4 В3 п.3): SetMultiplayerAuthority сам по себе другим пирам НЕ рассылается
    /// и дочерним узлам не наследуется — поэтому явный RPC на каждый пир, и здесь
    /// перестраиваются ВСЁ: авторитет узла (рекурсивно — точки крепления внутри
    /// картинки), Freeze и кнопка импульса.</summary>
    public void ApplyRemoteAuthority(int peerId)
    {
        PilotPeerId = peerId;
        SetMultiplayerAuthority(peerId, recursive: true);
        ApplyAuthority();

        // §3.1 п.4: при смене авторитета кнопку перерегистрируем вместе с ролью —
        // иначе у бывшего авторитета остаётся кнопка, шлющая импульс в никуда.
        DebugInfoBus.RemoveDebugButton("Nudge Ship");
        if (HasAuthority)
            DebugInfoBus.RegisterDebugButton("Nudge Ship", NudgeShip);

        GD.Print($"ship#{Net?.ShipNetId ?? 0}: authority -> {peerId}, " +
                 $"local = {HasAuthority}, frozen = {Freeze}");
    }

    /// <summary>
    /// Пересобрать поведение узла под текущий авторитет (§3.1 п.2). Вызывается из
    /// `_Ready()` и (шаг 3b) из обработчика смены авторитета — роль нельзя читать
    /// один раз, при В3=B авторитет меняется в рантайме.
    /// </summary>
    protected void ApplyAuthority()
    {
        HasAuthority = IsMultiplayerAuthority();

        // Не-авторитет: тело заморожено и не участвует в контактах. Static, а не
        // Kinematic: кинематическое замороженное тело при перемещении расталкивает
        // по пути то, чего на авторитете нет (§2.5 п.4).
        Freeze = !HasAuthority;
        FreezeMode = FreezeModeEnum.Static;

        // Авторитет: снимает NetState на физическом шаге (истина).
        SetPhysicsProcess(HasAuthority);
        // Не-авторитет: процесс нужен для интерполяции ShipHull (шаг 8 плана);
        // в offline интерполяция не требуется, но и не мешает.
        SetProcess(!HasAuthority);
    }

    /// <summary>Авторитет: снять состояние тела в `NetState` на каждом физическом
    /// шаге. `MultiplayerSynchronizer` разошлёт снимки по своему интервалу (30 Гц).
    /// Метод вызывается только там, где включён физический процесс, то есть только
    /// авторитету (§3.1 п.2).</summary>
    public override void _PhysicsProcess(double delta)
    {
        if (Net == null)
            return;
        Net.NetPosition = GlobalPosition;
        // В буфере лежит Quaternion: Basis.Quat() (в C# GlobalRotation — это эйлеровы
        // углы Vector3, кватернион берётся из базиса глобального трансформ).
        Net.NetRotation = new Quaternion(GlobalTransform.Basis);
        Net.NetLinearVelocity = LinearVelocity;
        Net.NetAngularVelocity = AngularVelocity;
        Net.SimTick = (int)Engine.GetPhysicsFrames();

        // §3.1 п.5: репорт на сервер (rpc_id(1)) — только авторитету и только если
        // авторитет НЕ сервер: это единственное место, где позиция летит «снизу
        // вверх», и только от того, кто её вычислял (§2.3). В offline сервера нет.
        if (NetworkManager.Instance is { HasRealPeer: true } nm && !nm.IsServer)
            RpcId(1, nameof(RpcReportNetStateToServer),
                  Net.ShipNetId, Net.NetPosition, Net.NetRotation,
                  Net.NetLinearVelocity, Net.NetAngularVelocity, Net.SimTick);
    }

    /// <summary>Приём пилотского репорта на сервере (§3.1 п.5). Сервер — детектор, а
    /// не корректор: пишет принятый снимок в NetState (оттуда его берёт картинка и
    /// будущий автопилот), но своё замороженное тело не трогает. Устаревшие снимки
    /// из окна смены авторитета отбрасываются по SimTick (§4 В3 п.6).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcReportNetStateToServer(int shipNetId, Vector3 position, Quaternion rotation,
                                          Vector3 linearVelocity, Vector3 angularVelocity, int simTick)
    {
        if (NetworkManager.Instance is not { IsServer: true })
            return; // принимать репорты может только сервер
        if (Net == null || Net.ShipNetId != shipNetId)
            return; // репорт про другой корабль — игнор (адрес важнее пути, §4 В4 п.3)
        if (HasAuthority)
            return; // сервер сам авторитет (автопилот): снизу позицию принимать не от кого
        if (simTick <= _lastAcceptedReportTick)
            return; // устаревший снимок (§4 В3 п.6)

        _lastAcceptedReportTick = simTick;
        Net.NetPosition = position;
        Net.NetRotation = rotation;
        Net.NetLinearVelocity = linearVelocity;
        Net.NetAngularVelocity = angularVelocity;
        Net.SimTick = simTick;
        // Серверский Synchronizer (PublicVisibility = true) рассылает принятый снимок
        // остальным пирам-неавторитетам: картинка сходится через тот же канал.
    }

    /// <summary>Не-авторитет: интерполяция картинки `ShipHull` по снимкам `NetState`
    /// (ring buffer, §2.5/§3.3). Тело заморожено, поэтому рисуем только картинку.
    /// Снимки приходят от Synchronizer'а (или через серверский репорт) в `NetState`;
    /// здесь мы складываем их в буфер по локальному времени и рисуем картинку на
    /// `InterpDelay` секунд в прошлом — между двумя ближайшими снимками.</summary>
    public override void _Process(double delta)
    {
        if (Net == null || HasAuthority)
            return; // авторитет рисует картинку телом, интерполяция не нужна

        double now = Time.GetTicksMsec() / 1000.0;

        // Новый снимок (SimTick сдвинулся — Synchronizer принёс обновление)?
        if (Net.SimTick != _lastNetTickSeen)
        {
            _lastNetTickSeen = Net.SimTick;
            _interpBuf.Add(new Snapshot(now, Net.NetPosition, Net.NetRotation));
            // Режем старьё: за окном интерполяции снимки уже не нужны.
            const double window = InterpDelay * 4.0;
            while (_interpBuf.Count > 2 && now - _interpBuf[0].Time > window)
                _interpBuf.RemoveAt(0);
        }

        InterpolateHull(now);
    }

    /// <summary>Поставить `ShipHull` в интерполированное глобальное состояние на
    /// моменте `now - InterpDelay`. Вызывается и из _Process, и из автотеста
    /// (шов без UDP). Отдельный метод — чтобы тест мог задать буфер и проверить
    /// результат без гонки по времени.</summary>
    public void InterpolateHull(double now)
    {
        var hull = GetNodeOrNull<Node3D>("ShipHull");
        if (hull == null || _interpBuf.Count == 0)
            return;

        double render = now - InterpDelay;

        // Один снимок или кадр раньше самого старого — рисуем самый старый.
        if (_interpBuf.Count == 1 || render <= _interpBuf[0].Time)
        {
            ApplyHull(hull, _interpBuf[0]);
            return;
        }

        // Кадр позже самого свежего — не экстраполируем (иначе картинка обгоняет
        // сеть и дёргается при потере пакета): рисуем самый свежий.
        if (render >= _interpBuf[^1].Time)
        {
            ApplyHull(hull, _interpBuf[^1]);
            return;
        }

        // Ищем два снимка, между которыми попал render-момент.
        for (int i = 0; i < _interpBuf.Count - 1; i++)
        {
            Snapshot a = _interpBuf[i], b = _interpBuf[i + 1];
            if (render >= a.Time && render <= b.Time)
            {
                double span = b.Time - a.Time;
                double alpha = span > 1e-6 ? (render - a.Time) / span : 0.0;
                var pos = a.Position.Lerp(b.Position, (float)alpha);
                var rot = a.Rotation.Slerp(b.Rotation, (float)alpha);
                hull.GlobalTransform = new Transform3D(new Basis(rot), pos);
                return;
            }
        }
    }

    private static void ApplyHull(Node3D hull, Snapshot s)
    {
        hull.GlobalTransform = new Transform3D(new Basis(s.Rotation), s.Position);
    }

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

    // ------------------------------------------------------ силовые вызовы

    /// <summary>Единственная точка приложения импульса (§3.9 п.10): публичных
    /// `ApplyImpulse`/`ApplyTorque` у `Ship` быть не должно — не-авторитет, дёрнув
    /// их, рассинхронит картинку с истиной, а `Freeze` это не лечит (заморозка
    /// игнорирует силы только у себя, а не у того, кто их приложил). В offline
    /// авторитет есть всегда — гейтинг прозрачен.</summary>
    public void ApplyImpulseAuthorityOnly(Vector3 impulse)
    {
        if (!HasAuthority)
        {
            GD.PrintErr($"ship#{Net?.ShipNetId ?? 0}: импульс запросил не авторитет " +
                        $"(авторитет = {GetMultiplayerAuthority()}) — отказ");
            return;
        }
        ApplyImpulse(impulse);
    }

    /// <summary>То же для крутящего импульса (§3.9 п.10).</summary>
    public void ApplyTorqueImpulseAuthorityOnly(Vector3 torqueImpulse)
    {
        if (!HasAuthority)
        {
            GD.PrintErr($"ship#{Net?.ShipNetId ?? 0}: крутящий импульс запросил не " +
                        $"авторитет (авторитет = {GetMultiplayerAuthority()}) — отказ");
            return;
        }
        ApplyTorqueImpulse(torqueImpulse);
    }

    private void NudgeShip()
    {
        var impulse = -GlobalTransform.Basis.Z * Mass * 5f;
        ApplyImpulseAuthorityOnly(impulse);
        // Скорость обновится только на следующем шаге физики, поэтому печатаем импульс.
        GD.Print($"ship nudged, impulse = {impulse.Length():F1} Н*с (ожидается |v| ≈ 5 м/с)");
    }
}
