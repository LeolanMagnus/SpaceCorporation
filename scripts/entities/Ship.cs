using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Корабль (сцена scenes/Ship.tscn). Сети — шаг 2 переделки стадии 1 (§3.1, §2.5).
///
/// Разделение «истина / картинка» (шаг 2.3, решение В14 = B):
///   - тело `Ship` симулирует ТОТ пир, который является мультиплеер-авторитетом
///     узла. С шага 2.3 авторитетом корабля ВСЕГДА остаётся сервер (id = 1):
///     только так сервер интегрирует физику сам, а не принимает чужую позицию
///     как факт (§3.5 п.8, В14=B);
///   - пилот — отдельная роль: `PilotPeerId` (его назначает сервер через
///     `NetworkManager.SetPilot`). Пилот НЕ интегрирует тело: он собирает кадры
///     ввода и шлёт их серверу (`RpcSubmitInput`), а сам летает на предсказании
///     своей картинки (шаг 2.4);
///   - все остальные пиры держат тело замороженным (`FreezeMode.Static`) и лишь
///     интерполируют картинку `ShipHull` по реплике `NetState`;
///   - `ApplyImpulse` и прочие силовые вызовы — только авторитету (§2.5 п.6);
///     в offline сервер = локальный пир, и цепочка сворачивается в один вызов.
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
    /// Обновляется на всех пирах через `ApplyPilot`; нужен, чтобы при отстёжке
    /// пилота сервер вернул корабль в автопилот, а серверный гейт `RpcSubmitInput`
    /// отклонял кадры не от своего пилота (§3.5 п.8, шаг 2.3).</summary>
    public int PilotPeerId { get; private set; } = 1;

    /// <summary>Буфер HP («правда сервера»), если он есть на корабле (этап 4, шаг 4.5).
    /// Аксессор — тот же `ShipHealthState`-узел, что читают учёт урона и debug-info.</summary>
    public ShipHealthState? Health => GetNodeOrNull<ShipHealthState>("ShipHealthState");

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

    /// <summary>
    /// Радиус «прицела» мыши, метры (шаг 2.2.2). Мышь пилота — ПРЯМОЙ доворот
    /// носа, поэтому она обязана работать, даже когда курсор (виртуальный, при
    /// `Captured` он сидит в центре окна) не попал в корпус: иначе при быстром
    /// взмахе мышь «отлипает» и нос перестаёт слушаться.
    ///
    /// Порог нужен ровно для одного случая: пилот навёл курсор на UI-панель или на
    /// чужой объект и крутит не корабль. Измеряется НЕ в пикселях, а в метрах от
    /// центра корабля до луча, восстановленного из позиции курсора: пиксельный
    /// размер корпуса зависит от дистанции камеры, метры — нет. Тест п.6 проверяет
    /// именно этот порог.
    /// </summary>
    private const float AimRadiusM = 6f;

    /// <summary>
    /// Параметры полёта МОДЕЛИ (stage2-plan §1.1). Это не настройки движка: при
    /// `CustomIntegrator = true` движок не применяет ни демпфирование, ни
    /// гравитацию (пробы `tests/probes/probe_custom_integrator.*`), поэтому
    /// демпфирование считает `FlightModel`, а его значения берутся из свойств
    /// тела — так `LinearDamp`/`AngularDamp` остаются и настройками модели, и
    /// тем, что проверяет автотест стадии 1.
    /// </summary>
    public FlightParams Flight;

    /// <summary>
    /// Комплектация корабля модулями (Этап 6): единственный источник скоростных
    /// характеристик. `Flight` собирается из неё в `_Ready` и при каждой замене
    /// модуля (`EquipModule`). Меняется до `_Ready` (тесты выключают ассист) и
    /// в рантайме — пересчёт мгновенный.
    /// </summary>
    public ShipLoadout Loadout = new();

    /// <summary>
    /// Поддержка пилотирования (Этап 6, «жидкий космос»): при отпущенных клавишах
    /// тяги скорость гасится тем же ускорением, что набирается. true по умолчанию
    /// (требование задачи); false = классическая инерция Newton's Cradle.
    /// Экспортируется, чтобы тесты и отладка переключали его прямо в сцене.
    /// </summary>
    [Export] public bool PilotAssist
    {
        get => Loadout.PilotAssist;
        set => Loadout.PilotAssist = value;
    }

    /// <summary>
    /// Базовые параметры корпуса до модулей (Mass/демпфирование из свойств тела),
    /// снимок из `_Ready`: `EquipModule` пересобирает `Flight` = Loadout.Build(base).
    /// </summary>
    private FlightParams _baseFlight;

    // ─────────────────── Тип корабля (курьер / грузовик / контейнеровоз) ──────
    // Характеристики полётной модели каждого типа лежат в общем каталоге
    // ShipStatCatalog (scripts/core/ShipTypes.cs) — их наполняют дизайнеры, код
    // корабля их не знает. При спавне Ship берёт запись по типу из реплики
    // NetState.ShipTypeValue (по умолчанию Courier) и строит из неё массу тела,
    // _baseFlight и вид корпуса. Смена типа во время полёта — только через
    // RequestShipType (дебаг-панель локального пилота → RPC → сервер).

    /// <summary>Тип корабля (истина этой копии: у сервера — что он записал,
    /// у пиров — что приехала реплика).</summary>
    public ShipType ShipType => _shipType;
    private ShipType _shipType = ShipStatCatalog.DefaultType;
    /// <summary>Активная запись каталога для текущего типа.</summary>
    private ShipTypeSpec _shipTypeSpec = ShipStatCatalog.Find(ShipStatCatalog.DefaultType);
    /// <summary>Последний увиденный байт реплики типа (255 = ещё не видел).
    /// OnChange-дельта меняет свойство без callback — сравниваем байт, как в
    /// PollAssistReplica.</summary>
    private byte _lastShipTypeReplica = 255;

    /// <summary>Применить запись каталога к ТЕЛУ, корпусу и базе полёта этой
    /// копии. Идемпотентна; вызывается при спавне и при каждой смене типа.</summary>
    private void ApplyShipTypeSpec(ShipTypeSpec spec, bool fromExportCeiling)
    {
        _shipType = spec.Type;
        _shipTypeSpec = spec;

        // Масса тела (RigidBody3D): импульсы, отскоки, тараны (этапы 4/5).
        Mass = spec.Mass;
        // Момент инерции — по форме коробки каталога (Godot ждёт главные оси
        // вектором); иначе у «среднего» корпуса инерция осталась бы курьерская.
        float hx = spec.HullSize.X, hy = spec.HullSize.Y, hz = spec.HullSize.Z;
        float ixx = spec.Mass * (hy * hy + hz * hz) / 12f;
        float iyy = spec.Mass * (hx * hx + hz * hz) / 12f;
        float izz = spec.Mass * (hx * hx + hy * hy) / 12f;
        Inertia = new Vector3(ixx, iyy, izz);

        // Примитивный корпус: габариты — и у коллизии, и у картинки (провалить
        // меш сквозь коллизию нельзя), цвет — из каталога. Узлы переиспользуют
        // созданные в сцене (Ship.tscn), если они уже есть.
        var collision = GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
        if (collision?.Shape is BoxShape3D box)
            box.Size = spec.HullSize;
        var hull = GetNodeOrNull<MeshInstance3D>("ShipHull/Hull");
        if (hull?.Mesh is BoxMesh bm)
            bm.Size = spec.HullSize;
        if (hull != null)
        {
            if (hull.MaterialOverride is not StandardMaterial3D mat)
            {
                mat = new StandardMaterial3D();
                hull.MaterialOverride = mat;
            }
            mat.AlbedoColor = spec.HullColor;
        }
        // Ствол — срез носа: при смене корпуса смещаем Muzzle на новый нос.
        var muzzle = GetNodeOrNull<Node3D>("Muzzle");
        if (muzzle != null)
            muzzle.Position = new Vector3(0f, 0f, -spec.HullSize.Z * 0.5f);
        // Навесные примитивы силуэта (только картинка): пересобираем с нуля —
        // их набор зависит от типа (контейнеры, груз, кили). Узел-обёртка живёт
        // под ShipHull, сами меш-инстансы пересоздаются при каждой смене типа.
        RebuildHullAccessories(spec);

        // Посадочная точка — на крыше корпуса (иначе игрок провалится в трюм).
        var seat = GetNodeOrNull<Node3D>("ShipHull/AttachPoints/Seat");
        if (seat != null)
            seat.Position = new Vector3(0f, spec.HullSize.Y * 0.5f, 0.2f);

        // Потолок угловой скорости корпуса (экспорт до _Ready — см. MakeBaseFlight).
        if (!fromExportCeiling)
            MaxAngularSpeed = spec.Base.MaxAngularSpeed;

        LinearDamp = spec.Base.LinearDamping;
        AngularDamp = spec.Base.AngularDamping;
    }

    /// <summary>Пересобрать навесные примитивы силуэта корпуса (чистая картинка,
    /// без коллизий). Обёртка `HullAccessories` под `ShipHull` создаётся по мере
    /// надобности; содержимое выбрасывается целиком — набор зависит от типа.</summary>
    private void RebuildHullAccessories(ShipTypeSpec spec)
    {
        var hull = GetNodeOrNull<Node3D>("ShipHull");
        if (hull == null)
            return;
        var wrap = hull.GetNodeOrNull<Node3D>("HullAccessories");
        if (wrap == null)
        {
            wrap = new Node3D { Name = "HullAccessories" };
            hull.AddChild(wrap);
        }
        foreach (Node child in wrap.GetChildren())
            child.QueueFree();
        for (int i = 0; i < spec.Accessories.Length; i++)
        {
            var (offset, size, color) = spec.Accessories[i];
            var mi = new MeshInstance3D
            {
                Name = $"Accessory{i}",
                Mesh = new BoxMesh { Size = size },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = color },
                Position = offset,
            };
            wrap.AddChild(mi);
        }
    }

    /// <summary>Установить тип этой копии локально (спавн/применение реплики):
    /// записать тип + спецификацию, применить к телу/корпусу, пересобрать базу.</summary>
    private void SetShipTypeLocal(ShipTypeSpec spec, bool fromExportCeiling = false)
    {
        ApplyShipTypeSpec(spec, fromExportCeiling);
        _baseFlight = spec.Base;
        if (fromExportCeiling)
            _baseFlight.MaxAngularSpeed = MaxAngularSpeed; // экспорт до _Ready (тест п.12)
        else
            _baseFlight.MaxAngularSpeed = spec.Base.MaxAngularSpeed;
        RebuildFlight();
    }

    /// <summary>
    /// Максимальная угловая скорость корабля, рад/с (модуль `AngularVelocity` —
    /// т.е. ограничена и каждая ось, и любая комбинация: тангаж, рыскание, крен).
    /// Это характеристика конкретного корабля: у freighter она меньше, у
    /// истребителя больше, и настраивается прямо в сцене (Ship.tscn) или кодом
    /// перед `_Ready`. Гейтит всё, что крутит корпус:
    ///  • момент с клавиш (Q/E, Z/X/C/V) — через `AngularVelocity` в `FlightModel.Step`;
    ///  • мышь (прямой доворот носа) — через `FlightModel.ClampLookRadians`
    ///    в `StepPrediction`;
    ///  • угловую доводку при стыковке и любую внешнюю запись скорости.
    /// 0 или меньше = без ограничения (так стоят все прогоны, где `Flight`
    /// собирается вручную: структурный ноль не меняет их числа).
    /// </summary>
    [Export] public float MaxAngularSpeed = 4.5f;

    /// <summary>Плечо мыши: радианы поворота носа на 1 пиксель движения мыши.</summary>
    [Export] public float MouseSensitivity = 0.0025f;

    // ── Шаг 3.3: визуал луча прицела (только картинка, без попадания) ──────────
    //
    // Луч рисуется ЛОКАЛЬНО локальному пилоту сразу при нажатии ЛКМ — картинка не
    // знает, было ли попадание (хитскан считает сервер, шаг 3.5). Поэтому узел
    // луча живёт фиксированную длину времени и удаляется сам, независимо от сети.
    /// <summary>Сколько секунд живёт визуальный луч (потом узел удаляется).</summary>
    [Export] public float AimingBeamLifetime = 0.2f;
    /// <summary>Длина отрисовки луча до точки прицела (м), если камера/мишень не дали
    /// closer точку. Реальный хитскан сервера использует свою дальность (LaserRange).</summary>
    [Export] public float AimingBeamLength = 200f;
    /// <summary>Толщина луча (м).</summary>
    [Export] public float AimingBeamRadius = 0.06f;

    /// <summary>
    /// Режим управления локальным пилотом (фича «режим пилотирования / режим
    /// взаимодействия»). Правая кнопка мыши (действие `toggle_interaction`)
    /// переключает:
    ///   `Pilot`        — мышь захвачена (`MouseModeEnum.Captured`), дельта мыши
    ///                    крутит нос (тангаж/рыскание), клавиши тангажа/рыскания
    ///                    работают;
    ///   `Interaction`  — курсор виден (`MouseModeEnum.Visible`), мышь НИЧЕГО не
    ///                    доворачивает, клавиши тангажа/рыскания вырезаны из
    ///                    кадра ввода, и игрок может кликать элементы интерфейса
    ///                    (в `Captured` курсор «пришит» к центру окна и UI
    ///                    недостижим).
    /// Тяга (WASD/boost) в обоих режимах живёт: взаимодействие — это не «стоп-кран»,
    /// а освобождение мыши.
    ///
    /// Флаг живёт у корабля, а не у слоя UI: весь путь мыши (сборщик, гейт
    /// прицела, кадр ввода) уже сосредоточен в `Ship`, и переключателю достаточно
    /// одного места, чтобы влиять и на курсор, и на кадр ввода.
    /// </summary>
    public enum ControlMode { Pilot, Interaction }

    /// <summary>Текущий режим локального пилота (для панели отладки и тестов).</summary>
    public ControlMode Mode { get; private set; } = ControlMode.Pilot;

    /// <summary>Играет ли локальный пир в режиме взаимодействия (курсор свободен).</summary>
    public bool IsInteractionMode => Mode == ControlMode.Interaction;

    /// <summary>Текущий тик корабля (для автотестов и шага 2.4).</summary>
    public uint Tick => _tick;

    // ── Этап 6: переключение пилот-ассиста (клавиша T) ──────────────────────
    // Флаг живёт на СЕРВЕРЕ (NetState.PilotAssist, OnChange-реплика): сервер —
    // единственный писатель (RpcTogglePilotAssist с гейтом «только пилот»),
    // остальные пиры читают реплику. Пилот-клиент для отзывчивости инвертирует
    // флаг ЛОКАЛЬНО в момент нажатия T и ждёт подтверждения:
    //  • значение в реплике == ожидаемому — предсказание уже строится с новым
    //    флагом, расхождения нет, откатов нет (п.13 «жестких откатов нет»);
    //  • сервер сказал ДРУГОЕ (отказ/гонка) — редкий жёсткий откат: локальный
    //    флаг меняется на серверный и вся неподтверждённая история переигрывается
    //    с новым флагом (`ReplayInputsAfter(AppliedTick)` — тот же механизм,
    //    что и у реконсиляции позиции).
    // Кадровая часть: T не уходит в маска-битах (переключение — событие, а не
    // состояние), поэтому после T сервер, применяя кадры из очереди, до применения
    // кадра с тиком ≥ T сам переключает флаг (RPC приходит раньше кадров) и
    // переигрывает историю с тика T. Клиент же обязан моделировать с новым
    // флагом кадры (T, T+AssumeWindow]: на них действует `_assistExpected`
    // (ожидаемое состояние). Разница клиент/сервер по тикам ≤ RTT·60 м/с — на
    // практике 1-2 кадра, ошибка на порядок ниже порога жёсткого откатa.

    /// <summary>Запросить тип корабля (дебаг-панель локального пилота).
    /// Offline/host — применяем сразу; пилот-клиент — предсказание + RPC.</summary>
    public void RequestShipType(ShipType type)
    {
        if (!IsLocalPilot)
        {
            GD.PrintErr($"ship: RequestShipType({type}) — локальный игрок не пилот " +
                        $"этого корабля (pilot = {PilotPeerId}) — отказ");
            return;
        }
        if (Net == null)
        {
            // Offline-корпус без NetState: применяем локально (нет реплики).
            ApplyShipTypeSpec(ShipStatCatalog.Find(type), fromExportCeiling: false);
            _baseFlight = _shipTypeSpec.Base;
            RebuildFlight();
            return;
        }
        if (HasAuthority)
        {
            ApplyServerShipType((byte)type);
            return;
        }
        // Пилот-клиент: БЕЗ предсказания (в отличие от PilotAssist). Смена типа —
        // дебаг-событие: предсказывать тут нечего, а откатывать предсказание при
        // отказе сервера было бы дороже, чем один RTT ожидания реплики — тип
        // приедет OnChange-дельтой и применится у всех пиров за один шаг опроса.
        RpcSetShipTypeRpc((byte)type);
    }

    /// <summary>Серверный приём смены типа от пилота. Гейт и транспорт — как у
    /// RpcTogglePilotAssistRpc: только пилот, надёжный канал 1.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcSetShipTypeRpc(byte typeValue)
    {
        int sender = Multiplayer.GetRemoteSenderId();
        if (sender != PilotPeerId)
        {
            GD.PrintErr($"ship: RpcSetShipType от пира {sender}, а пилот = " +
                        $"{PilotPeerId} — отказ (не пилот не меняет тип корабля)");
            return; // отказ: реплика не меняется, клиент откатится по PollShipTypeReplica
        }
        ApplyServerShipType(typeValue);
    }

    /// <summary>Серверная истина: записать тип в реплику и применить к себе
    /// (реплика разойдётся по пирам сама, spawn + OnChange).</summary>
    private void ApplyServerShipType(byte typeValue)
    {
        var spec = ShipStatCatalog.FindByByte(typeValue);
        if (typeValue != (byte)spec.Type)
            GD.PrintErr($"ship: неизвестный байт типа {typeValue} — применяем " +
                        $"{spec.DisplayName} (запись каталога по умолчанию)");
        if (Net == null) return;
        Net.ShipTypeValue = (byte)spec.Type;
        if (HasAuthority && _shipType != spec.Type)
            SetShipTypeLocal(spec); // сервер: масса/корпус/база полёта сразу
    }

    /// <summary>Опросить байт реплики типа и применить смену у не-авторитета.
    /// Паттерн PollAssistReplica: OnChange-дельта приходит молча (свойство
    /// изменилось без события), сверка байта — копейки за физшаг.</summary>
    private void PollShipTypeReplica()
    {
        if (Net == null || Net.ShipTypeValue == _lastShipTypeReplica)
            return;
        _lastShipTypeReplica = Net.ShipTypeValue;
        // Ложной «смены на спавне» тут не бывает: _Ready уже применил тип из
        // спавн-снимка и запомнил байт как виденный, так что в опрос попадает
        // только настоящая смена сервера.
        if (_shipType != (ShipType)Net.ShipTypeValue)
            SetShipTypeLocal(ShipStatCatalog.FindByByte(Net.ShipTypeValue));
    }

    /// <summary>Клавиша T (`toggle_pilot_assist`): переключение пилот-ассиста.
    /// Берёт только локальный пилот этого корабля — `RequestTogglePilotAssist`
    /// сам отклоняет не-пилота, но проверку делаем ДО вызова, чтобы в сцене с
    /// несколькими кораблями каждый не печатал ошибку на каждое нажатие.</summary>
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo &&
            k.IsActionPressed("toggle_pilot_assist") && IsLocalPilot)
            RequestTogglePilotAssist();
    }

    /// <summary>Кадр ввода, поданный на последнем шаге (для отладки и шага 2.4).</summary>
    public InputFrame LocalInput => _localInput;

    /// <summary>Пилотирует ли этот корабль локальный пир: именно он собирает кадры
    /// ввода и шлёт их серверу. С шага 2.3 это НЕ равно авторитету: авторитет
    /// корабля навсегда у сервера (id = 1), пилот — клиент (§3.5 п.8, В14=B).
    /// В offline SelfId = 1 = PilotPeerId, поэтому поведение стадии 1 сохраняется.</summary>
    public bool IsLocalPilot => PilotPeerId == NetworkManager.Instance?.SelfId;

    /// <summary>Симулирует ли это тело локальный пир (авторитет узла = мы).
    /// С шага 2.3 для корабля это «мы сервер».</summary>
    public bool IsServerAuthority => HasAuthority;

    // ── Этап 6: комплектация модулями ────────────────────────────────────────

    /// <summary>
    /// Пересобрать `Flight` из `Loadout` поверх базовых параметров корпуса.
    /// Вызывается в `_Ready` и при каждой замене модуля / переключении ассиста.
    /// </summary>
    private void RebuildFlight() => Flight = Loadout.Build(_baseFlight);

    /// <summary>
    /// Поставить модуль (по id из каталога) в слот его вида и пересчитать `Flight`.
    /// false — если id неизвестен (модуль не меняется). Этап 6: улучшение корабля
    /// = замена модуля на уровне выше.
    /// </summary>
    public bool EquipModule(string id)
    {
        if (!Loadout.Equip(id)) return false;
        RebuildFlight();
        return true;
    }

    /// <summary>
    /// Включить/выключить поддержку пилотирования (Этап 6, Т) и пересчитать
    /// `Flight`. Вызывается и из сети (RPC-переключение), и из тестов: тестам
    /// арены (таран, стыковочная пружина) нужен прежний режим чистой инерции.
    /// </summary>
    public void SetPilotAssist(bool on)
    {
        Loadout.PilotAssist = on;
        RebuildFlight();
    }

    /// <summary>Вынуть модуль из слота вида `kind` и пересчитать `Flight`.</summary>
    public void UnequipModule(ModuleKind kind)
    {
        Loadout.EquipKind(kind, null);
        RebuildFlight();
    }

    // ── Этап 6: швы для LiquidSpaceTest (п.13–15) ────────────────────────────

    public override void _Ready()
    {
        GravityScale = 0f;
        LinearDamp = 0f;
        AngularDamp = 1.5f;

        // Тело с CustomIntegrator обязано быть активным ВСЕГДА: спящее тело не
        // вызывает _integrate_forces, а значит тяга с клавиш не смогла бы его
        // сдвинуть (пробуждение по импульсу есть, а по нашей тяге — нет).
        CanSleep = false;

        // Тип корабля и его характеристики из общего каталога ShipStatCatalog
        // (требование: набор характеристик полётной модели хранится ОТДЕЛЬНО от
        // кода корабля — его наполняют и балансят дизайнеры). Тип берётся из
        // реплики NetState.ShipTypeValue: у сервера-спавнера это значение до
        // AddChild, у пиров — спавн-снимок репликации (spawn=true). Байт
        // неизвестный/отсутствующий → Courier (значение по умолчанию задачи).
        //
        // Net читаем ДО построения _baseFlight: характеристики типа определяют
        // и массу тела, и базу полётной модели, и вид корпуса.
        Net = GetNodeOrNull<NetState>("NetState");
        _lastShipTypeReplica = Net?.ShipTypeValue ?? 255;
        // Экспорт MaxAngularSpeed, заданный ДО AddChild (шов теста п.12 стадии 2:
        // настройка корабля в сцене/кодом должна доживать до Flight). Он значим
        // только для типа по умолчанию (Courier — тип сцены); для остальных
        // говорит каталог. Снимаем экспорт ДО применения спека (тот перезапишет).
        bool exportCeiling = Net == null || Net.ShipTypeValue == (byte)ShipType.Courier;
        float exportCeilingValue = MaxAngularSpeed;
        SetShipTypeLocal(ShipStatCatalog.FindByByte(Net?.ShipTypeValue
                                                   ?? (byte)ShipType.Courier),
                         fromExportCeiling: false);
        if (exportCeiling && exportCeilingValue != 6f)
        {
            // Ненулевая настройка в сцене/кодом приоритетнее каталога для Courier.
            MaxAngularSpeed = exportCeilingValue;
            _baseFlight.MaxAngularSpeed = exportCeilingValue;
        }
        RebuildFlight();

        CollisionLayer = CollisionLayers.Ship;
        CollisionMask = CollisionLayers.ShipMask;
        // Шаг 3.3: ствол — начало визуального луча (Ship.tscn: Muzzle, pos (0,0,-2)).
        _muzzle = GetNodeOrNull<Node3D>("Muzzle");
        // Этап 4, шаг 4.7: форма корпуса для IntersectShape-запроса контакта со
        // станцией (Ship.tscn: CollisionShape3D, BoxShape3D 2×1×4).
        _hullShapeNode = GetNodeOrNull<CollisionShape3D>("CollisionShape3D");

        // §3.1 п.8: корабль с нулевым адресом физически живёт в сцене, но все его
        // RPC (RequestAttach, рассылка NetState, кадры ввода) уходят в никуда,
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
        {
            // Шаг 4.10 (п.26): в панель дебаг-инфы добавлена строка HP (число) +
            // god mode + счётчик ударов. Health-узел есть у каждого корабля, но
            // защищаемся от null (напр. моментально удалённый ShipHealthState).
            string healthPart = "";
            if (GetNodeOrNull<ShipHealthState>("ShipHealthState") is { } h)
                healthPart = $"HP: {h.Hp}/{h.MaxHp}, god={(h.GodMode ? "on" : "off")}, " +
                             $"dmg#={h.DamageTakenCount}, lastDmg={h.LastDamage:F1}, ";
            return
                $"ship#{Net?.ShipNetId ?? 0}: |v| = {LinearVelocity.Length():F2} m/s, " +
                $"|w| = {AngularVelocity.Length():F2} rad/s, " +
                $"mass = {Mass:F1}, " +
                // Без auth/frozen/|err| реконсиляцию и смену пилота отлаживать нечем (§3.1 п.6).
                $"auth = {GetMultiplayerAuthority()}, pilot = {PilotPeerId}, " +
                $"mode = {(IsInteractionMode ? "INTERACT" : "PILOT")}, " +
                // Этап 6: режим «жидкого космоса» этого корабля (клавиша T):
                // без строки нечем проверить, что реплика флага доехала до пира.
                $"assist = {(PilotAssistLocal ? "ON" : "OFF")}, " +
                $"frozen = {Freeze}, " +
                // Шаг 5.13 (п.35): dock-фаза и адрес станции в сделке — без них
                // сделку в панели отладить нечем. Ключи добавлены, прежние
                // (mode/auth/frozen/HP) не переписаны — assert'ы 3/4 стадий целы.
                $"dock = {((DockState)(Net?.DockStateValue ?? 0))}, " +
                $"dockStn = {(Net != null && Net.DockStationNetId != 0 ? Net.DockStationNetId.ToString() : "—")}, " +
                // frame-plan шаг 10: активная фаза захвата — `cap: #child→#parent
                // v_rel=(…)` (кадр родителя и локальная скорость в нём). Без
                // захвата ключа cap в строке нет (не зашумлять панель).
                (Net != null && Net.CaptureParentNetId != 0
                    ? $"cap: #{Net.ShipNetId}→#{Net.CaptureParentNetId} v_rel=({LocalVelocity.X:F1},{LocalVelocity.Y:F1},{LocalVelocity.Z:F1}) m/s, "
                    : "") +
                $"netid = {Net?.ShipNetId ?? 0}, " +
                // Показатели stage2-plan §2.5: Server Tick (тикет серверной физики из
                // реплики NetState), Prediction Error (расхождение предсказания пилота
                // с эталоном), Interp Buffer (число снимков в буфере интерполяции) и
                // состояние интерполяции. Пинг (RTT) печатает строка «Net» NetworkManager.
                $"srvTick = {(Net == null ? "—" : Net.SimTick.ToString())}, " +
                $"predErr = {PredictionError:F3}, " +
                $"interpBuf = {_interpBuf.Count}, interpOn = {_interpolationEnabled}, " +
                healthPart +
                $"|err| = {(Net == null ? 0f : Net.NetPosition.DistanceTo(GlobalPosition)):F3}";
        });

        // Реестр кораблей по адресу: RPC про корабль идут по ShipNetId, не по пути (§4 В4 п.3).
        if (Net != null && Net.ShipNetId != 0)
        {
            NetworkManager.Instance?.RegisterShip(this);

            // §4 В4 п.2: сервер мог назначить пилота раньше, чем спавнер создал у
            // этого пира узел корабля, — забираем отложенное назначение сейчас.
            if (NetworkManager.Instance is { } nm &&
                nm.TryGetPilot(Net.ShipNetId, out int pending) &&
                pending != PilotPeerId)
                ApplyPilot(pending);
        }

        // Кнопки этого корабля под текущую роль (авторитет / не-авторитет).
        // Регистрация и снятие — ТОЛЬКО по ручкам этого корабля: см. _nudgeHandle.
        BuildDebugButtons();
    }

    public override void _ExitTree()
    {
        // Снимаем источник отладки по ключу Ship#{ShipNetId}: лямбда захватывает
        // этот узел, и после освобождения корабля (DespawnShip, §3.9 п.16) шина
        // звала бы мёртвую лямбду каждый кадр → ObjectDisposedException (§3.11 п.9).
        DebugInfoBus.RemoveInfoSource($"Ship#{Net?.ShipNetId ?? 0}");
        // added 2026-08: узел уходит — глушить chase-камеру и отпускать мышь
        // нужно и при удалении корабля пилотом: иначе current-камера игрока,
        // погашенная при V, так и осталась бы погашенной без владельца.
        ReleaseChaseCamera();
        ReleasePilotInput();
        // Кнопки — только свои, по ручкам (BUG-008-рецидив): снятие по метке задевает
        // кнопки других кораблей (у них та же метка законно). Ручки покрывают и
        // «страховку» из баг-листа: отложенная заявка в _pendingButtons убирается
        // ручкой же, узел-сирота с колбэком на мёртвый корабль не остаётся.
        RemoveDebugButtons();

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

    /// <summary>Смена пилота, инициированная сервером (`NetworkManager.SetPilot`,
    /// §4 В3 п.3 + В14=B): назначаются ПИЛОТЫ, а не авторитет — авторитет корабля
    /// у сервера не меняется НИКОГДА. Поэтому здесь НЕ вызывается
    /// SetMultiplayerAuthority: он рассылался бы по пирам и пересаживал бы
    /// физическую симуляцию на клиента, а сервер принял бы его позицию за факт.
    /// Меняются только роль пилота, ввод, камера и кнопка импульса.</summary>
    public void ApplyPilot(int peerId)
    {
        PilotPeerId = peerId;

        // Смена пилота: очередь ввода обнуляется, нижняя граница тика поднимается —
        // опоздавшие кадры прежнего пилота (unreliable-канал) не должны управлять
        // кораблём после отстёжки (§3.5 п.8; аналог удалённого гейта по SimTick).
        ResetInputQueue();

        ApplyAuthority();

        // §3.1 п.4: при смене пилота кнопки перерегистрируем вместе с ролью —
        // иначе у бывшего пилота остаётся кнопка, шлющая импульс в никуда.
        //
        // Во время `_Ready()` этот метод вызывается ДО BuildDebugButtons (реестр
        // пилотов читаем сразу после RegisterShip, а роль корабля в момент чтения
        // ещё не подтверждена) — тогда пересборку делает идущий следом
        // BuildDebugButtons, а здесь трогать кнопки нельзя: иначе каждая смена
        // пилота при спавне добавляла бы второй набор кнопок этого корабля.
        //
        // Пересобираем ТОЛЬКО если набор кнопок реально не соответствует роли
        // (_debugButtonsAuthority): сами кнопки гейтуются авторитетом, а при В14=B
        // пилот авторитет не получает (§3.5 п.8), так что обычная пристёжка/отстёжка
        // набор не меняет. Пересборка «на всякий случай» переставляла кнопку в
        // конец панели и ломала автотест инерции (Stage1Test п.25).
        // Кнопки пилота (ассист, выбор типа корабля) существуют только для
        // ЛОКАЛЬНОГО пилота — значит смена пилота сама по себе меняет набор,
        // даже когда авторитет не менялся (В14=B). Держим роль под руками:
        // сравнение с прежним пиром дешевле, чем пересборка на всякий случай.
        if (_debugButtonsBuilt && (_debugButtonsAuthority != HasAuthority
                                   || _debugButtonsPilot != PilotPeerId))
            BuildDebugButtons();

        GD.Print($"ship#{Net?.ShipNetId ?? 0}: pilot -> {peerId}, " +
                 $"local pilot = {IsLocalPilot}, auth = {GetMultiplayerAuthority()}, " +
                 $"frozen = {Freeze}");
    }

    /// <summary>
    /// Пересобрать поведение узла под текущий авторитет (§3.1 п.2). Вызывается из
    /// `_Ready()` и (шаг 3b) из обработчика смены авторитета — роль нельзя читать
    /// один раз, при В3=B авторитет меняется в рантайме.
    /// </summary>
    protected void ApplyAuthority()
    {
        // `IsMultiplayerAuthority()` при снятом peer (после Disconnect:
        // MultiplayerPeer = null) печатает ERROR «No multiplayer peer». Авторитет
        // узла при этом сохраняется — сравниваем номер напрямую.
        HasAuthority = Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer
            ? GetMultiplayerAuthority() == (NetworkManager.Instance?.LocalAuthorityId ?? 1)
            : IsMultiplayerAuthority();

        // Шаг 2.3 (В14=B): авторитет корабля навсегда у сервера (id = 1), поэтому
        // `Freeze = !HasAuthority` = «заморожен у всех, кроме сервера» — ровно
        // правило Freeze из §3.5 п.8. Пилот-клиент заморожен ОБЯЗАТЕЛЬНО: он
        // интегрирует только своё предсказание (шаг 2.4), а не истину (§3.5 п.5
        // плана: забытая заморозка пилота = два интегратора на одном теле).
        //
        // Шаг 5.10 (п.29): dock-замок. Единственная точка записи Freeze — здесь,
        // поэтому «сервер ставит Freeze = true на Docked» живёт тоже здесь:
        // иначе ApplyPilot (→ ApplyAuthority) во время сделки молча сотрёт замок.
        // Проба tests/probes/probe_freeze_integrator.tscn доказала: у Frozen-тела
        // Godot НЕ вызывает `_IntegrateForces` (0 вызовов за 30 кадров), поэтому
        // замок — только на `Docked`, НЕ на `Latched`: в Capturing/Latched тело
        // обязана интегрировать серверная пружина (шаг 5.8), а плановое
        // «DockIsLatched()» из stage5-plan:116 убило бы доводку на 3 метрах.
        bool dockLocked = Net != null && (DockState)Net.DockStateValue == DockState.Docked;
        Freeze = !HasAuthority || dockLocked;
        FreezeMode = FreezeModeEnum.Static;

        // Страховка: если кто-то вернёт старую схему (авторитет = пилот-клиент),
        // сервер перестанет быть интегратором, и это молча сломает авторитетную
        // симуляцию. Такое изменение должно быть видно сразу.
        if (NetworkManager.Instance is { HasRealPeer: true } nm && nm.IsServer &&
            GetMultiplayerAuthority() != nm.LocalAuthorityId)
            GD.PrintErr($"ship#{Net?.ShipNetId ?? 0}: авторитет корабля = " +
                        $"{GetMultiplayerAuthority()}, а должен быть сервер " +
                        $"({nm.LocalAuthorityId}) — сервер потерял истину (§3.5 п.8)");

        // Сервер снимает состояние тела в `NetState` на физическом шаге (истина).
        // Пилоту физический процесс тоже нужен: на каждом своём шаге он отправляет
        // кадр ввода и ведёт локальный тик (§1.4/§1.6).
        SetPhysicsProcess(HasAuthority || IsLocalPilot);
        // Не-авторитет: процесс нужен для интерполяции ShipHull (шаг 8 плана);
        // в offline интерполяция не требуется, но и не мешает.
        SetProcess(!HasAuthority);

        // Шаг 2.2 (В14): движение считает FlightModel, а не движок. Флаг
        // `CustomIntegrator` включает вызов `_IntegrateForces`; включаем его
        // ТОЛЬКО авторитету, потому что у замороженного тела (`Freeze = true`
        // у не-авторитета) движок интегратор не зовёт вовсе, а два интегратора
        // на одном теле дали бы двойной шаг.
        CustomIntegrator = HasAuthority;

        // Мышь пилота приходит в `_Input`; ввод нужен ТОЛЬКО локальному пилоту
        // (шаг 2.3): сервер-авторитет читает кадры из очереди, остальные пиры —
        // тем более нет. Выключение ввода не-пилоту снимает риск, что чужой пир
        // накопит `_lookDelta` и отправит его в никуда.
        SetProcessInput(IsLocalPilot);
        if (!IsLocalPilot)
        {
            // Роли локального пилота нет: ни ввода, ни камеры; режим вида сбрасываем.
            ReleaseChaseCamera();
            ReleasePilotInput();
            _pilotChaseView = false;
        }
        else
        {
            // Пилот уже здесь (например, роль сменилась Offline → Host, а корабль
            // был создан до смены): сборщик мыши нужен и в этом случае.
            EnsurePilotInput();
            // Камера: по умолчанию — вид «из глаз» пристёгнутого пилота (его
            // Camera3D), chase-камера включается только по V (added 2026-08).
            ApplyPilotCamera();
        }
    }

    // ------------------------------------------------------------------
    // Интегрирование: модель вместо движка (шаг 2.2, В14)
    // ------------------------------------------------------------------

    /// <summary>Маска последнего кадра ввода, потреблённого на боевом шаге
    /// (пишет CacheFrameMask из FrameForStep — ровно один кадр за шаг). Хранится,
    /// чтобы правило захвата НЕ звало FrameForStep повторно (у сервера он изымает
    /// кадр из очереди). Значение 0 (нет шага) трактуется как «не двигается» =
    /// крейсер по предикату маски (cruise-capture-plan §2).</summary>
    private uint _lastFrameMask;

    /// <summary>Запомнить маску потреблённого шага и вернуть кадр как есть.</summary>
    private InputFrame CacheFrameMask(InputFrame f)
    {
        _lastFrameMask = f.Mask;
        return f;
    }

    /// <summary>
    /// Переключить режим пилотирования/взаимодействия (правая кнопка мыши).
    /// Публично — чтобы режим мог переключать не только мышь, но и UI-кнопка
    /// или тест. Работает только у локального пилота: у остальных пилотов мышь
    /// и курсор их окна к этому кораблю отношения не имеют.
    /// </summary>
    public void ToggleInteractionMode() => SetInteractionMode(!IsInteractionMode);

    /// <summary>Явно задать режим (шов тестов и UI-кнопок).</summary>
    public void SetInteractionMode(bool on)
    {
        if (!IsLocalPilot)
            return;
        if ((Mode == ControlMode.Interaction) == on)
            return;

        Mode = on ? ControlMode.Interaction : ControlMode.Pilot;

        // Накопленную мышь выбрасываем в обе стороны: в Interaction она не
        // нужна, а при возврате в Pilot старые пиксели (протасканные курсором
        // по UI) довернули бы нос в момент переключения.
        _pilotInput.InteractionMode = on;
        _pilotInput.ClearLookDelta();

        // Курсор в пилотировании 2.0 всегда свободен и виден (он и есть прицел),
        // поэтому переключать MouseMode между Visible/Captured не нужно. При
        // возврате в режим пилотирования возвращаем прицел-курсор в центр —
        // иначе нос рванул бы к точке, где курсор остался после кликов по UI.
        if (_capturedInput && !on)
            WarpPilotCursorToCenter();

        GD.Print($"ship#{Net?.ShipNetId ?? 0}: режим {(on ? "взаимодействия (прицел-курсор свободен)" : "пилотирования (доворот на курсор)")}");
    }

    // ==================================================================
    // Швы автотестов шага 2.2 (п.4-п.6)
    // ==================================================================

}
