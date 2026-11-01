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

    /// <summary>Задержка интерполяции, сек. Больше 2×ReplicationInterval (1/30),
    /// чтобы кадр всегда попадал внутрь окна двух снимков. Это БАЗА/минимум:
    /// реальная задержка адаптивна (§3.9, долг §2.7) — см. `_interpDelay`.</summary>
    private const double InterpDelay = 0.1;

    // §3.9 (долг §2.7): адаптивная задержка интерполяции. При ≥3 кораблях
    // пропускная способность на корабль падает: период приёма снимков растёт до
    // ~0,25 с (на замере LATENCY=100 3 игрока). Фиксированный `InterpDelay = 0,1`
    // становится МЕНЬШЕ периода снимков → render-момент (`now - delay`) выезжает
    // за самый свежий снимок: картинка мёрзнет в последнем снимке, а когда приходит
    // следующий — прыгает назад в сегмент с α≈0,6 (скачок 1,0–1,9 м, критерий (в)
    // §2.6 провален). Лечение: задержка = 2×измеренного периода снимков (кадр
    // всегда внутри окна), буфер держим достаточно глубоким, чтобы окно накрывало
    // задержку. Период меряем EMA по моментам постановки снимков в `_interpBuf`.
    private double _interpDelay = InterpDelay;
    private double _snapPeriodEma = -1.0;   // <0 — период ещё не измерен
    // Пик-холд межприходного зазора: render-цель должна накрывать ХУДШИЙ недавний
    // зазор ( jitter/потеря пакета), иначе в «голодный» промежуток render=now-delay
    // выезжает за самый свежий снимок → картинка мёрзнет, а со следующим снимком
    // прыгает на весь пройденный за зазор путь (скачок больше шагового). Пик
    // держим с медленным спадом, чтобы единичный выброс не задрал задержку навсегда.
    private double _snapGapPeak = -1.0;
    private const double SnapPeriodMin = 1.0 / 30.0;  // физ. потолок частоты снимков
    private const double SnapPeriodMax = 1.0;         // защитный потолок (потеря пакетов)
    private const double AdaptiveDelayFactor = 2.0;   // delay = factor × период
    private const double AdaptiveDelayMax = 0.6;      // верхняя граница задержки
    // Максимум, на который задержка сдвигается за один принятый снимок: скачок
    // задержки сдвигает render-цель и сам даёт скачок hull (v×Δdelay), поэтому
    // адаптация идёт плавно, а не рывком (риск: лечение вносит новый артефакт).
    private const double MaxDelayStepPerSnap = 0.02;  // 20 мс за кадр (спад)
    private const double MaxDelayStepUpPerFrame = 0.06; // 60 мс за кадр (рост — лечить голодание сразу)
    // Мёртвая зона гистерезиса: пока |target-current| в этих пределах — задержку
    // не трогаем (дрожание EMA не двигает картинку). Вверх реагируем на 50 мс
    // раньше, чем вниз: голодание лечим быстро, перерасход снимаем осторожно.
    private const double DelayRampUpBand = 0.05;
    private const double DelayReleaseBand = 0.15;

    /// <summary>Текущая (возможно адаптивная) задержка интерполяции — для панели
    /// и автотеста (§3.9).</summary>
    public double InterpDelaySec => _interpDelay;

    /// <summary>Измеренный период приёма снимков, сек (<0 — ещё не измерялся).</summary>
    public double SnapPeriodSec => _snapPeriodEma;


    /// <summary>Включена ли интерполяция картинки не-авторитета. Выключена — hull
    /// ставится прямо в ПОСЛЕДНИЙ принятый снимок (без сдвига на `InterpDelay` и без
    /// смешивания двух): картинка прыгает дискретно, зато совпадает с сетью по времени.
    /// Переключается кнопкой `Toggle Interpolation` в дебаг-панели (stage2-plan §2.5,
    /// автотест п.13).</summary>
    private bool _interpolationEnabled = true;

    /// <summary>Состояние интерполяции (для панели и автотеста).</summary>
    public bool InterpolationEnabled => _interpolationEnabled;

    /// <summary>Включить/выключить интерполяцию картинки не-авторитета.</summary>
    public void SetInterpolation(bool enabled) => _interpolationEnabled = enabled;

    /// <summary>Переключить интерполяцию (для кнопки дебаг-панели).</summary>
    public void ToggleInterpolation() => _interpolationEnabled = !_interpolationEnabled;

    /// <summary>Шаг 4.9: переключить god mode. Идёт в единственный серверный путь
    /// записи (`ShipHealthState.SetGodModeServerOnly`), который сам гейтится по
    /// авторитету. Кнопка регистрируется только серверному процессу (см. _Ready),
    /// RPC нет вовсе — с клиента запрос не принимается (п.23/п.24).</summary>
    public void ToggleGodMode()
    {
        if (GetNodeOrNull<ShipHealthState>("ShipHealthState") is { } h)
            h.SetGodModeServerOnly(!h.GodMode);
    }

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

    // ------------------------------------------------------------------
    // Пилотирование 6DoF (стадия 2, шаг 2.2)
    // ------------------------------------------------------------------

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

    // --- Этап 4, шаг 4.7: серверный учёт контакта со станцией ----------------
    // Урон и отскок считает ТОЛЬКО авторитет в _IntegrateForces (клиент их не
    // трогает — шаг 4.11). Обнаружение — PhysicsDirectSpaceState3D.IntersectShape
    // по маске Station (проба tests/probes/ProbeStationPen.cs: там же в C#-биндинге
    // НЕТ ComputePenetration, а в результате IntersectShape есть только ключи
    // rid/collider_id/collider/shape — глубины/нормали нет, поэтому нормаль
    // берём геометрически: из центра станции в корабль).
    /// <summary>Пауза (сек) между повторным учётом урона тому же узлу-пересечению,
    /// чтобы корабль, застрявший в цилиндре станции, не получал урон каждый кадр
    /// (таблица «ломается», строка «застрять → урон капает»).</summary>
    [Export] public float CollisionCooldownSec = 0.5f;
    /// <summary>Сетевой адрес станции, по которому корабль таранит в этом же
    /// физкадре — чтобы тот же контакт в одном кадре не считался дважды.</summary>
    private int _lastContactStationNetId;
    private ulong _lastContactPhysicsFrame;
    /// <summary>Серверные часы последнего учёта урона конкретной станции
    /// (кулдаун CollisionCooldownSec).</summary>
    private double _lastCollisionTimeSec;
    private bool _hasLastCollisionTime;
    /// <summary>Форма корпуса корабля для shape-запроса (кэш CollisionShape3D).</summary>
    private CollisionShape3D? _hullShapeNode;
    /// <summary>Флаг «отскок применён» — метод выставил новое n.LinearVelocity,
    /// инкрематор обязан записать его в `sd` перед шагом 3.</summary>
    private bool _collisionApplied;
    /// <summary>Новая линейная скорость после упругого отскока (валидна, если
    /// <see cref="_collisionApplied"/>).</summary>
    private Vector3 _collisionOutVelocity;

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

    /// <summary>Дистанция камеры-преследования за кормой (шаг 2.2.3).</summary>
    [Export] public float CameraDistance = 12f;

    /// <summary>Подъём камеры над кормой, чтобы корпус не перекрывал вид.</summary>
    [Export] public float CameraHeight = 3f;

    /// <summary>Кадр ввода, собранный для текущего физического шага.</summary>
    private InputFrame _localInput;

    /// <summary>Дельта мыши, накопленная между физшагами: `InputEventMouseMotion`
    /// приходит в `_Input`, а симуляция считается в `_IntegrateForces`.</summary>
    private Vector2 _lookDelta;
    // Шаг 3.3: последний экранный курсор — по нему восстанавливаем луч прицела
    // для визуала (RequestFire вызывается между движениями мыши).
    private Vector2 _lastMousePos;

    /// <summary>Счётчик физических шагов этого процесса = номер тика в кадрах
    /// ввода. Шаг 2.3 добавит серверную сверку тиков, здесь счётчик локальный.</summary>
    private uint _tick;

    // ------------------------------------------------------------------
    // Предсказание и реконсиляция на пилоте (стадия 2, шаг 2.4, §1.6)
    // ------------------------------------------------------------------
    //
    // Пилот-клиент заморожен (не имеет права двигать истину), но обязан вести
    // КАРТИНКУ и камеру по СОБСТВЕННОМУ предсказанию, иначе управление ощущается
    // с задержкой в один RTT. Схема (классический client-side prediction):
    //   (а) каждый физшаг пилот шлёт кадр ввода серверу И тут же интегрирует им
    //       своё предсказанное `FlightState`, а тело ставит в это предсказание
    //       (проба `tests/probes/probe_frozen_move.tscn`: запись в замороженное
    //       тело приживается, а камера и hull — дети тела, поэтому следуют за ним);
    //   (б) кадр кладётся в кольцевой буфер по индексу `tick & 255`, чтобы потом
    //       переиграть его, если серверный эталон покажет расхождение;
    //   (в) когда приходит реплика `NetState` (эталон с `AppliedTick`), пилот
    //       сверяет предсказание с эталоном: расхождение больше порога — откат к
    //       эталону и переигровка кадров с `Tick > AppliedTick`; меньше — только
    //       подтянуть скорости (soft-sync), без телепорта, чтобы не дёргалось.
    //
    // Эталон приходит НЕ отдельным RPC, а теми же репликами `NetState`, что
    // Synchronizer шлёт всем не-авторитетам (§1.5): пилот — не авторитет (В14=B),
    // поэтому получает их наравне с наблюдателями.

    /// <summary>Порог ЖЁСТКОГО отката, метры. Расхождение меньше — считаем шумом
    /// интегрирования/сети и не телепортируем картинку (soft-sync: подтягиваем
    /// только скорости). Порог ПРИРАВНЕН к критерию готовности §2.6 (а)
    /// `predErr < 0,3 м`: рассогласование в пределах допуска предсказания — это
    /// штатная работа, оно гасится мягкой синхронизацией скоростей и НЕ должно
    /// порождать телепорт. Жёсткий откат — только за подлинные расхождения сверх
    /// допуска (стартовый транзиент ~2 м, тесты п.9=5 м / п.11=1 м). Так критерии
    /// (а) и (б) связаны напрямую: пока честная ошибка держится ниже 0,3 м,
    /// жёстких откатов нет вовсе (кроме старта), и `hardRollbacks ≤ 1/сек`
    /// выполняется автоматически. Тесты п.9-11 задают пороги 0.04 (soft) и 1/5 м
    /// (hard) — при 0.3 они проходят как раньше.</summary>
    private const float ReconcileThreshold = 0.3f;

    /// <summary>Размер кольцевого буфера неподтверждённых вводов. 256 кадров =
    /// ~4.3 с на 60 Гц — с запасом перекрывает любой разумный RTT+джиттер.</summary>
    private const int PendingRingSize = 256;

    /// <summary>Кольцевой буфер вводов пилота: индекс = `tick & (PendingRingSize-1)`.
    /// Храним и сам кадр, и его тик, чтобы при переигровке отличить «свой» кадр от
    /// мусора после перезаписи кольца.</summary>
    private readonly InputFrame[] _pending = new InputFrame[PendingRingSize];

    /// <summary>История предсказанных позиций пилота по тикам: индекс = `tick &
    /// (PendingRingSize-1)`, рядом хранится сам тик, чтобы отличить «свою» запись
    /// от мусора после перезаписи кольца. Нужна для ЧЕСТНОЙ метрики предсказания
    /// (§2.6 критерий (а)): сверяя реплику с эталоном, сравниваем серверную позицию
    /// (она соответствует `AppliedTick`) с тем предсказанием, что пилот держал ровно
    /// на `AppliedTick`, а НЕ с текущим предсказанием — иначе ошибка завышается на
    /// «упреждение» скорость×RTT и даёт ложные жёсткие откаты каждый снимок.</summary>
    private readonly Vector3[] _predPosHistory = new Vector3[PendingRingSize];
    private readonly uint[] _predPosTick = new uint[PendingRingSize];

    /// <summary>Скорость предсказания пилота на том же тике, что и `_predPosHistory`
    /// (та же валидность по `_predPosTick`). Нужна для мягкой синхронизации: серверный
    /// снимок несёт скорость на `AppliedTick`, а `_predicted` ушёл вперёд на RTT/2
    /// кадров. Подставлять серверную скорость как АБСОЛЮТ — значит выбросить прирост
    /// скорости за эти кадры: пилот отстаёт, ошибка растает до порога и каждый снимок
    /// даёт жёсткий откат (зубчатый `hardRollbacks`). Поэтому в мягкой сверке берём
    /// РАЗНОСТЬ скоростей (сервер на AppliedTick минус пилот на AppliedTick) и
    /// докладываем её в текущую предсказанную скорость: при детерминизме разница ≈ 0
    /// (зубцов нет), а реальный дрейф (залипание кадра) корректируется.</summary>
    private readonly Vector3[] _predVelHistory = new Vector3[PendingRingSize];
    private readonly Vector3[] _predAngVelHistory = new Vector3[PendingRingSize];

    /// <summary>Предсказанное состояние пилота. Для offline/сервера не используется
    /// (истина ведётся телом через `_IntegrateForces`); значимо только пилоту-клиенту.</summary>
    private FlightState _predicted;

    /// <summary>Есть ли уже валидное предсказание (инициализировано из тела при
    /// входе пилота). До первого шага предсказывать нечего.</summary>
    private bool _hasPrediction;

    /// <summary>Последний `SimTick` эталона, по которому уже сверялись: чтобы не
    /// реконсилироваться по одной и той же реплике многократно.</summary>
    private int _lastReconcileTick = -1;

    /// <summary>Счётчик физшагов для гейта публикации `NetState` (шаг 2.6, метрика
    /// (г) §2.7). Сервер пишет реплику не каждый физшаг (60 Гц), а раз в 2 шага
    /// (30 Гц) — ровно как задумано в §2.9 («30 снимков/с репликации»). Причина:
    /// в Godot 4.7 OnChange-репликация синхронизатора НЕ уважает `replication_interval`
    /// (проверено: interval=10.0 в сцене, а доставка всё равно 60/с — дельты уходят
    /// каждый физшаг, пока меняется свойство). Единственный способ задать частоту
    /// снимков — менять сами свойства реже. Симуляция тела при этом идёт полные
    /// 60 Гц (через `_IntegrateForces`), реже публикуется только сетевой снимок.
    /// Реконсиляция не ломается: `SimTick`/`AppliedTick` в снимке всегда указывают
    /// точный физшаг, к которому он относится, а буфер ввода пилота хранит все
    /// 60-герцовые кадры — переигровка по `Tick > AppliedTick` остаётся точной.</summary>
    private int _netPublishPhase;
    private const int NetPublishEveryN = 2;   // 60 Гц физики / 2 = 30 снимков/с

    /// <summary>Расхождение предсказания с последней принятой репликой, метры —
    /// для дебаг-панели (`Prediction Error`, §1.6).</summary>
    public float PredictionError { get; private set; }

    /// <summary>Счётчик переигранных кадров на последней реконсиляции — для assert'а
    /// п.9 автотеста (переигран ровно N кадров).</summary>
    public int LastReplayedCount { get; private set; }

    // --------------------------------------------------------------- искусственная
    // задержка (шов `TestLatencyMs`, §2.6). Лаг вносится на уровне отправки/приёма
    // на клиенте, НЕ трогая ENet: кадры ввода пилота и снимки `NetState` кладутся в
    // буфер и «дозревают» через `TestLatencyMs` миллисекунд. Так при RTT ≈ 2×latency
    // измеряют `Prediction Error`, число откатов и дельту hull, не правя транспорт.
    // При `TestLatencyMs == 0` (дефолт) буферы пустеют в тот же кадр — поведение
    // идентично прежнему, автотесты п.9-13 не деградируют.

    /// <summary>Буфер исходящих кадров ввода пилота: (момент «доставки», кадр).</summary>
    private readonly Queue<(long DueMs, InputFrame Frame)> _sendBuf = new();

    /// <summary>Буфер входящих снимков `NetState`: (момент «доставки», снимок). Время
    /// в секундах (та же шкала, что `Time.GetTicksMsec()/1000.0` в `_Process`).</summary>
    private readonly Queue<(double DueSec, NetSnap Snap)> _recvBuf = new();

    /// <summary>Копия снимка `NetState` для буфера приёма: `NetState` перезаписывается
    /// репликой каждый кадр, поэтому задерживаемое состояние надо скопировать.</summary>
    private struct NetSnap
    {
        public int SimTick;
        public uint AppliedTick;
        public Vector3 Pos, Lin, Ang;
        public Quaternion Rot;
    }

    /// <summary>Число ЖЁСТКИХ откатов-телепортов (err > порога) за время жизни корабля
    /// — метрика критерия (б) §2.6 (откатов ≤ 1/сек при LATENCY=100).</summary>
    public int HardRollbackCount { get; private set; }

    /// <summary>Максимальная дельта hull наблюдателя между соседними кадрами, метры —
    /// метрика критерия (в) §2.6 (чужой корабль: дельта < 0.15 м, «не резинит»).</summary>
    public float MaxHullDelta { get; private set; }
    private Vector3 _lastHullPos;
    private bool _haveHullPos;

    /// <summary>Таймер печати метрик §2.6 в headless-стенде (раз в секунду).</summary>
    private float _metricsTimer;

    /// <summary>Камера-преследование, которую корабль создаёт себе сам, когда его
    /// пилотирует локальный пир. Ссылка нужна, чтобы погасить её при выходе пилота:
    /// иначе в world-viewport останется вторая current-камера.</summary>
    private Camera3D? _chaseCam;

    /// <summary>Чужую камеру (обычно камеру игрока) глушим при пилотировании и
    /// возвращаем при освобождении камеры корабля.</summary>
    private Camera3D? _silencedCam;

    /// <summary>Ввод из теста (без клавиатуры): если true, `_localInput` берётся
    /// из `_testFrame`, а не из `Input.IsActionPressed`.</summary>
    private bool _testInput;
    private InputFrame _testFrame;

    /// <summary>Шаг из теста: `_IntegrateForces` игнорирует реальные физкадры и
    /// ждёт явного `TestStepForTest()`. Нужен, если в headless физический шаг
    /// этого тела не доходит до интегратора (проверяется пробой
    /// `tests/probes/ProbeInputHeadless.tscn`).</summary>
    private bool _testStep;

    /// <summary>Состояние окна, которое пилотирование временно перехватывает:
    /// режим мыши и включён ли ввод у узла. Возвращаем при освобождении.</summary>
    private Input.MouseModeEnum _prevMouseMode = Input.MouseModeEnum.Visible;
    private bool _capturedInput;

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

    public override void _Ready()
    {
        GravityScale = 0f;
        LinearDamp = 0f;
        AngularDamp = 1.5f;

        // Тело с CustomIntegrator обязано быть активным ВСЕГДА: спящее тело не
        // вызывает _integrate_forces, а значит тяга с клавиш не смогла бы его
        // сдвинуть (пробуждение по импульсу есть, а по нашей тяге — нет).
        CanSleep = false;

        // Параметры модели из свойств тела (см. документацию Flight выше).
        // Тяги подобраны под массу 1200 кг: 90 кН ≈ 75 м/с² — «дистанционный»
        // разгон, при котором корпус 2×1×4 м управляем в висячей сцене Main.
        Flight = new FlightParams
        {
            Mass = Mass,
            LinearThrust = 90000f,
            AngularThrust = 40000f,
            LinearDamping = LinearDamp,   // 0 → инерция сохраняется (тест этапа 1)
            AngularDamping = AngularDamp, // 1.5 → вращение гаснет само
            BoostMultiplier = 2f,
        };

        CollisionLayer = CollisionLayers.Ship;
        CollisionMask = CollisionLayers.ShipMask;

        Net = GetNodeOrNull<NetState>("NetState");
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
                $"frozen = {Freeze}, " +
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

        // Кнопка для проверки инерции: короткий импульс вперёд, дальше тело летит само.
        // Только авторитету: не-авторитет не имеет права вызывать ApplyImpulse (§2.5 п.6).
        // В offline авторитет есть у всех — кнопка остаётся на своём месте.
        if (HasAuthority)
        {
            DebugInfoBus.RegisterDebugButton("Nudge Ship", NudgeShip);
            // Шаг 4.9: god mode включает только серверный процесс. Сервер — всегда
            // авторитет корабля (В14=B), поэтому гейт по HasAuthority = гейт «только
            // сервер» (п.23: у клиента-не-авторитета кнопки нет вовсе).
            DebugInfoBus.RegisterDebugButton("Toggle God Mode", ToggleGodMode);

            // CLI-путь god mode для headless/dedicated (панели нет): `-- --god`
            // идёт в тот же серверный метод, что и кнопка (п.24).
            if (NetworkManager.GodModeCli)
                ToggleGodMode();
        }
        else
        {
            // Кнопка интерполяции картинки не-авторитета (stage2-plan §2.5): имеет
            // смысл только там, где hull рисуется по снимкам (наблюдатель/пилот-
            // предсказание), поэтому и регистрируется только не-авторитету.
            DebugInfoBus.RegisterDebugButton("Toggle Interpolation", ToggleInterpolation);
        }
    }

    public override void _ExitTree()
    {
        // Снимаем источник отладки по ключу Ship#{ShipNetId}: лямбда захватывает
        // этот узел, и после освобождения корабля (DespawnShip, §3.9 п.16) шина
        // звала бы мёртвую лямбду каждый кадр → ObjectDisposedException (§3.11 п.9).
        DebugInfoBus.RemoveInfoSource($"Ship#{Net?.ShipNetId ?? 0}");
        DebugInfoBus.RemoveDebugButton("Nudge Ship");
        DebugInfoBus.RemoveDebugButton("Toggle Interpolation");
        DebugInfoBus.RemoveDebugButton("Toggle God Mode");

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

        // §3.1 п.4: при смене пилота кнопку перерегистрируем вместе с ролью —
        // иначе у бывшего пилота остаётся кнопка, шлющая импульс в никуда.
        DebugInfoBus.RemoveDebugButton("Nudge Ship");
        if (HasAuthority)
            DebugInfoBus.RegisterDebugButton("Nudge Ship", NudgeShip);
        // Кнопка интерполяции живёт только у не-авторитета (картинка по снимкам).
        DebugInfoBus.RemoveDebugButton("Toggle Interpolation");
        if (!HasAuthority)
            DebugInfoBus.RegisterDebugButton("Toggle Interpolation", ToggleInterpolation);

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
        HasAuthority = IsMultiplayerAuthority();

        // Шаг 2.3 (В14=B): авторитет корабля навсегда у сервера (id = 1), поэтому
        // `Freeze = !HasAuthority` = «заморожен у всех, кроме сервера» — ровно
        // правило Freeze из §3.5 п.8. Пилот-клиент заморожен ОБЯЗАТЕЛЬНО: он
        // интегрирует только своё предсказание (шаг 2.4), а не истину (§3.5 п.5
        // плана: забытая заморозка пилота = два интегратора на одном теле).
        Freeze = !HasAuthority;
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
            ReleaseChaseCamera();
            ReleaseMouseCatcher();
        }
        else
        {
            // Пилот уже здесь (например, роль сменилась Offline → Host, а корабль
            // был создан до смены): сборщик мыши и камера нужно и в этом случае.
            EnsureMouseCatcher();
            EnsureChaseCamera();
        }
    }

    // ------------------------------------------------------------------
    // Интегрирование: модель вместо движка (шаг 2.2, В14)
    // ------------------------------------------------------------------

    /// <summary>
    /// Собственный интегратор. Вызывается только там, где включён
    /// `CustomIntegrator`, то есть только авторитету (остальные пиры заморожены,
    /// а у замороженного тела движок `_integrate_forces` не зовёт вовсе).
    ///
    /// Порядок шага установлен пробами `tests/probes/probe_ci_order.tscn` и
    /// `probe_ci_finality.tscn`:
    ///   1) движок сам сдвигает тело скоростью ПРЕДЫДУЩЕГО шага (explicit Euler:
    ///      `pos_at_entry = p0 + v0*dt`), угловой скоростью НЕ вращает
    ///      (`probe_ci_angular.tscn`);
    ///   2) затем зовёт нас, и то, что мы запишем в `sd`, финально для шага
    ///      (`pos_after_step` = ровно наша запись).
    /// Поэтому модель считает шаг ЦЕЛИКОМ сама, а пред-интегрирование движка
    /// отменяется (`t.Origin -= v0 * dt`) — иначе шаг считается дважды и
    /// траектория тела разошлась бы с траекторией чистой модели, по которой
    /// клиент предсказывает и переигрывает (шаг 2.4).
    /// </summary>
    public override void _IntegrateForces(PhysicsDirectBodyState3D sd)
    {
        if (!HasAuthority)
            return; // страховка: CustomIntegrator включён только авторитету
        if (_testStep)
            return; // шаг ведёт TestStepForTest, физкадры пропускаем (иначе двойной шаг)

        float dt = (float)sd.GetStep();
        if (dt <= 0f) dt = FlightModel.FixedDt;

        // --- 1. Состояние НАЧАЛА шага: отменяем пред-интегрирование движка ---
        Vector3 v0 = sd.LinearVelocity;
        Transform3D start = sd.Transform;
        start.Origin -= v0 * dt;

        FlightState s = new FlightState
        {
            Position = start.Origin,
            Rotation = start.Basis.GetRotationQuaternion(),
            LinearVelocity = v0,
            AngularVelocity = sd.AngularVelocity,
        };

        // --- 2. Ввод пилота за этот шаг + шаг модели + тик ---
        // Сервер берёт кадр из очереди пилота (или читает клавиши, если пилот он же);
        // у offline-игры сервер = локальный пир, и поведение стадии 1 сохраняется.
        FlightState n = AdvanceFlight(s, FrameForStep(), dt);

        // --- 2.5. Этап 4, шаг 4.7: серверный учёт контакта со станцией ---
        // Запрашиваем пересечение КОРПУСА в позе КОНЦА шага (n.Position): если
        // корпус вошёл в цилиндр станции — урон по |vn| и отскок. Только на
        // авторитете (метод уже гейтнут HasAuthority выше), клиент сюда не
        // заходит (шаг 4.11). Результат отскока — в n.LinearVelocity ДО записи.
        HandleStationContacts(n.Position, n.LinearVelocity);
        if (_collisionApplied) { n.LinearVelocity = _collisionOutVelocity; _collisionApplied = false; }

        // --- 3. Запись в тело (финальна для шага) ---
        sd.Transform = new Transform3D(new Basis(n.Rotation), n.Position);
        sd.LinearVelocity = n.LinearVelocity;
        sd.AngularVelocity = n.AngularVelocity;
    }

    /// <summary>Общая часть шага: мышь → ориентация, кадр ввода → тяга, тик.
    /// И `_IntegrateForces`, и тестовый шаг идут через неё, иначе тест проверял
    /// бы не тот код, который работает в игре (§3.8).</summary>
    private FlightState AdvanceFlight(FlightState s, InputFrame frame, float dt)
    {
        _localInput = frame;
        FlightState n = StepPrediction(s, frame, dt);
        _tick++;
        return n;
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

    /// <summary>Ядро шага без счётика тика: мышь → ориентация, кадр → тяга.
    /// Вынесено из `AdvanceFlight`, чтобы реконсиляция (шаг 2.4) могла
    /// ПЕРЕИГРАТЬ уже отправленные кадры, не двигая локальный счётчик `_tick`
    /// (у переигрываемых кадров тик уже записан в самом кадре).</summary>
    private FlightState StepPrediction(FlightState s, InputFrame frame, float dt)
    {
        // Мышь: прямой доворот носа (не момент) — ДО шага модели, чтобы
        // ориентация, из которой модель берёт оси тяги, уже была «свежей».
        // Дельта приходит в кадре УЖЕ в радианах: offline-пилот запекает её в
        // `CollectInputFrame` из накопленных пикселей, а сервер с внешним
        // пилотом получает прямо из кадра ввода (`_serverInput`). Интегратор НЕ
        // читает `_lookDelta`: у сервера-авторитета своей мыши по этому кораблю
        // нет, и look прилетает только через кадр (шаг 2.3, §3.5 п.8).
        if (frame.LookYaw != 0f || frame.LookPitch != 0f)
            s.Rotation = ApplyLookRadians(s.Rotation, frame.LookYaw, frame.LookPitch);

        return FlightModel.Step(s, Flight, frame, dt);
    }
    /// <summary>Собрать кадр ввода с реальных клавиш (только пилоту, см.
    /// `IsLocalPilot`). Дискретная модель: каждое действие — бит, поэтому
    /// `Input.IsActionPressed`, а не аналоговые оси.</summary>
    private InputFrame CollectInputFrame()
    {
        uint mask = 0;
        if (Input.IsActionPressed("thrust_f")) mask |= InputMask.ThrustF;
        if (Input.IsActionPressed("thrust_b")) mask |= InputMask.ThrustB;
        if (Input.IsActionPressed("thrust_l")) mask |= InputMask.ThrustL;
        if (Input.IsActionPressed("thrust_r")) mask |= InputMask.ThrustR;
        if (Input.IsActionPressed("thrust_u")) mask |= InputMask.ThrustU;
        if (Input.IsActionPressed("thrust_d")) mask |= InputMask.ThrustD;
        // Режим взаимодействия: корабль перестаёт управляться по тангажу/рысканью
        // (и мышью — см. `HandleMouseMotion`, и клавишами Z/X/Q/E — здесь). Тяга
        // (WASD/boost) остаётся: игрок освобождает мышь для интерфейса, а не глушит
        // двигатели.
        if (!IsInteractionMode)
        {
            if (Input.IsActionPressed("torque_pitch_up")) mask |= InputMask.TorquePitchUp;
            if (Input.IsActionPressed("torque_pitch_down")) mask |= InputMask.TorquePitchDown;
            if (Input.IsActionPressed("torque_yaw_left")) mask |= InputMask.TorqueYawLeft;
            if (Input.IsActionPressed("torque_yaw_right")) mask |= InputMask.TorqueYawRight;
        }
        if (Input.IsActionPressed("boost")) mask |= InputMask.Boost;

        // Автопилот headless-стенда (§2.6): в --headless никто не жмёт WASD, поэтому
        // пилот сам гонит тягу и периодически доворачивает рысканием — иначе
        // `Prediction Error` и число откатов в логе всегда нулевые и метрики §2.6
        // нечем измерить.
        //
        // ВАЖНО: в космосе нет трения, поэтому «просто жми тягу вперёд» разгоняет
        // корабль до сотен м/с (стенд улетал на 13 000 м за 16 с, ~190 м/с). При
        // такой скорости даже субкадровый рассинхрон тиков пилота и сервера даёт
        // доли метра (1 кадр на 190 м/с = 3,2 м), и критерий §2.6 (а) `predErr <
        // 0,3 м` становится недостижимым физически, а не из-за бага. Поэтому
        // автопилот держит ЦЕЛЕВУЮ СКОРОСТЬ: тяга вперёд, пока медленнее цели, и
        // торможение назад, когда быстрее. Скорость ограничена ~25 м/с → 1 кадр =
        // 0,42 м, субкадровый дрейф = сантиметры, метрика осмысленна.
        //
        // Автопилот считает решение по СКОРОСТИ ПИЛОТА (_predicted.LinearVelocity):
        // он работает только на пилоте (сервер-авторитет для чужого корабля читает
        // кадры из очереди, а не крутит автопилот), решение запекается в маску кадра
        // и сервер интегрирует ровно те же кадры — детерминизм сохраняется.
        // ГАТ ПО РОЛИ (фикс OBS-002): автопилот — это стендовый пилот, он
        // имеет право крутить тягу ТОЛЬКО там, где локальный пир реально ведёт
        // предсказание и шлёт кадры (Offline, Host, Client). На dedicated-
        // сервере локальный пир (SelfId=1) никогда не является целевым пилотом:
        // у нового корабля PilotPeerId=1 по умолчанию (строка 42), поэтому до
        // пристёгивания клиента `IsLocalPilot` ложно == true, и без этого гейта
        // сервер гонял `ThrustF` каждый физшаг ~8 с (в космосе трения нет) —
        // корабль улетал на ~950 м к моменту attach, первая же реконсиляция
        // давала `err ≈ 955 м` → жёсткий откат и резонанс bang-bang-регулятора
        // с частотой снимков (hardRollbacks 21…79/22 с, метрики §2.6 нерабочи).
        // Сервер-авторитет обязан брать ввод из очереди кадров пилота
        // (`FrameForStep`), а не из автопилота. См. bugs.md OBS-002.
        if (NetworkManager.Autopilot &&
            NetworkManager.Instance?.Role != NetworkManager.NetRole.Dedicated)
        {
            // Цель 6 м/с. Две планки связывают выбор скорости:
            //  • критерий (в) §2.6 `maxHullDelta < 0,15 м` меряет смещение hull
            //    наблюдателя между СОСЕДНИМИ кадрами отрисовки. У плавно летящего
            //    корабля это просто скорость/частота_отрисовки. Замер в headless-
            //    демке дал ~50 к/с (не 60): при 8 м/с это 0,16 м — ПОРОГ ПРОБИТ,
            //    метрика теряет смысл (дёргается даже без «резинга»). Значит
            //    крейсерская скорость стенда обязана быть < 0,15×50 = 7,5 м/с;
            //    берём 6 м/с с запасом: 0,12 м при 50 к/с, 0,10 м при 60 к/с.
            //  • критерии (а)/(б): чем медленнее, тем меньше субкадровый дрейф
            //    тиков пилот↔сервер (1 кадр = 0,10 м) — заведомо под порогом.
            // Мёртвая зона
            // (coast) между «тяга» и «тормоз» гасит чаттер bang-bang: без неё на
            // границе скорости пилот каждый кадр переключает тягу/тормоз, а это
            // разрывы ускорения, которые накладываются на фазовый сдвиг тиков и
            // дают ошибки у порога (зубцы hardRollbacks). Тянем, пока медленнее
            // цели; тормозим, только когда быстрее цели × 1,6; в промежутке — накат.
            const float TargetSpeed = 6f;      // м/с — крейсерский ход стенда (см. планку (в) выше)
            const float BrakeBand = 1.6f;      // тормозим, когда скорость > цели × 1,6
            float speed = _predicted.LinearVelocity.Length();
            if (speed < TargetSpeed)
                mask |= InputMask.ThrustF;
            else if (speed > TargetSpeed * BrakeBand)
                mask |= InputMask.ThrustB;
            // Редкие довороты рысканием — чтобы в метриках жил и вращательный канал
            // (угловая скорость в снимке, реконсиляция ориентации). Фаза каждые 90
            // тиков, поворот только на нечётных фазах, чтобы не мешать удержанию курса.
            int phase = (int)(_tick / 90) % 4;
            if (phase == 1) mask |= InputMask.TorqueYawRight;
            else if (phase == 3) mask |= InputMask.TorqueYawLeft;
        }

        // Оси восстанавливает FromMask: в провод уходит только tick+mask+мышь
        // (16 байт, §1.3), а сервер, прочитав маску, обязан получить ровно тот
        // же кадр, что интегрировал клиент, — иначе предсказание (шаг 2.4)
        // разойдётся с серверной истиной на ровном месте.
        InputFrame f = InputFrame.FromMask(_tick, mask,
            yaw: _lookDelta.X * MouseSensitivity,
            pitch: _lookDelta.Y * MouseSensitivity);
        // Накопленную мышиную дельту съедаем ровно один раз: иначе следующий
        // физкадр повторно довернул бы нос теми же пикселями (шаг 2.3: look
        // теперь применяется из кадра, а не из `_lookDelta` напрямую).
        _lookDelta = Vector2.Zero;
        return f;
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
        // Шаг 3.3: запоминаем экранный курсор ДО гейтов — выстрел (RequestFire)
        // восстанавливает луч по последней позиции курсора, даже если в момент
        // клика мышь не двигалась.
        _lastMousePos = m.Position;
        if (!IsLocalPilot || m.Relative == Vector2.Zero)
            return;
        // Режим взаимодействия: мышь свободна и кликает по интерфейсу — она НЕ
        // должна доворачивать нос, даже если курсор случайно проходит над кораблём.
        if (IsInteractionMode)
            return;
        if (!AimOnShip(m.Position))
            return;
        _lookDelta += m.Relative;
    }

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
        if (!AimOnShip(_lastMousePos))
            return;
        ShowAimingBeam();
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

            // Мишень опознаётся по дочернему буферу состояния, а не по имени:
            // станция (TestStand, слой Station) тоже попала бы под LaserMask, но
            // у неё нет `TargetState` — значит это НЕ попадание в цель (п.22).
            TargetState? state = body.GetNodeOrNull<TargetState>("TargetState");
            if (state == null || state.TargetNetId == 0)
                continue;

            // Шаг 3.6, риск №7: реестр — единственный владелец живых мишеней.
            // Если мишень уже снята (удаляется в этом же кадре / гонка с чужим
            // выстрелом), FindTarget вернёт null — это warning, а не исключение
            // (п.25). Урон наносим по записи реестра, а не по «сырому» телу.
            var nm = NetworkManager.Instance;
            if (nm != null && nm.FindTarget(state.TargetNetId) == null)
            {
                GhostHitsIgnored++;
                GD.PushWarning($"ship#{Net.ShipNetId}: хитскан в мишень " +
                               $"#{state.TargetNetId}, её НЕТ в реестре — попадание " +
                               $"игнорируется (риск №7: удаление во время полёта)");
                continue;
            }
            TargetState target = state;

            Vector3 hitPoint = hit["position"].AsVector3();
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
        Vector3 end;
        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam != null)
        {
            Vector3 o = cam.ProjectRayOrigin(_lastMousePos);
            Vector3 d = cam.ProjectRayNormal(_lastMousePos);
            end = o + d * AimingBeamLength;
        }
        else
        {
            // Без камеры прицел не определить — ведём луч вдоль носа (страховка headless).
            end = start - GlobalTransform.Basis.Z * AimingBeamLength;
        }

        Vector3 dir = end - start;
        float len = dir.Length();
        if (len < 0.001f)
            return;

        // Узел живёт фиксированное время и удаляется сам (SceneTreeTimer). Новый
        // выстрел убирает старый луч, чтобы не плодить узлы при спаме ЛКМ.
        _aimBeam?.QueueFree();
        var beam = new MeshInstance3D { Name = "BeamVisual" };
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
        // доворачиваем базис: Y←нормаль луча.
        beam.GlobalPosition = (start + end) * 0.5f;
        Vector3 up = dir.Normalized();
        // Строим базис, где Y=up, а X/Z — любые перпендикуляры (радиус симметричен).
        Vector3 refAxis = Mathf.Abs(up.Dot(Vector3.Right)) < 0.9f ? Vector3.Right : Vector3.Up;
        Vector3 x = up.Cross(refAxis).Normalized();
        Vector3 z = x.Cross(up).Normalized();
        beam.GlobalTransform = new Transform3D(new Basis(x, up, z), beam.GlobalPosition);

        _aimBeam = beam;

        // Самоудаление через AimingBeamLifetime секунд. Замыкание ловит конкретный
        // узел: если его уже сняли раньше (новый выстрел) — проверка IsValid.
        double secs = AimingBeamLifetime;
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

    /// <summary>
    /// Прямой доворот носа мышью (шаг 2.2.2). Мышь — НЕ момент: угловая
    /// скорость от неё не появляется (иначе демпфирование «догоняло» бы нос, и
    /// прицел разъезжался бы с курсором). Поворот — локальные оси: тангаж вокруг
    /// X, рыскание вокруг Y (крен не управляем: мышь в кабине не кренит).
    ///
    /// Шаг 2.3: ядро переведено в радианы (`ApplyLookRadians`), потому что в
    /// кадре ввода мышь летит уже в радианах — offline-пилот запёк пиксели в
    /// `CollectInputFrame`, а сервер с внешним пилотом получает радианы прямо
    /// из `RpcSubmitInput`. Пиксельная обёртка осталась для тестов-швов.
    /// </summary>
    private Quaternion ApplyLookRadians(Quaternion rot, float yawRad, float pitchRad)
    {
        if (yawRad == 0f && pitchRad == 0f)
            return rot;

        // Знак по Y инвертирован: движение мыши ВВЕРХ (relative.Y < 0) — нос
        // вверх (положительный тангаж вокруг локальной X).
        //
        // Порядок множителей: сначала мировой поворот (рыскание вокруг мировой Y),
        // затем локальный тангаж (вокруг X уже повёрнутого корпуса) — так мышь
        // не даёт крена при сводом в вертикаль взгляде (классический gimbal-фикс
        // для кабели: yaw мировой, pitch локальный).
        var q = new Basis(Vector3.Up, -yawRad) * new Basis(Vector3.Right, -pitchRad);
        return (new Basis(rot) * q).GetRotationQuaternion().Normalized();
    }

    /// <summary>Пиксельная обёртка над `ApplyLookRadians`: переводит дельту
    /// курсора в радианы через `MouseSensitivity`. Нужна швам теста
    /// (`TestAddLookDelta`), которые мыслят пикселями, как и реальный ввод.</summary>
    private Quaternion ApplyLook(Quaternion rot, Vector2 deltaPixels)
    {
        Vector2 rad = deltaPixels * MouseSensitivity;
        return ApplyLookRadians(rot, rad.X, rad.Y);
    }

    // ==================================================================
    // Шаг 2.3: кадры ввода от пилота → серверная симуляция (§3.5 п.8, В14=B)
    // ==================================================================

    /// <summary>Серверная очередь вводов: последний принятый кадр пилота. Он
    /// применяется на каждом физическом шаге сервера. Держим ровно один кадр, а не
    /// историю: при `unreliable`-канале пропущенный кадр = один кадр без силы, и при
    /// 60 Гц ввода это незаметно (в отличие от потерянного снапшота состояния,
    /// stage2-plan §1.4). Залипание клавиш при этом невозможно: пилот шлёт кадр
    /// КАЖДЫЙ физшаг, даже с пустой маской.</summary>
    private InputFrame _serverInput;

    /// <summary>Серверная ОЧЕРЕДЬ принятых кадров пилота (§2.6, фикс расхождения
    /// предсказания при задержке). Пилот интегрирует ровно один кадр за физшаг;
    /// чтобы серверная истина совпадала с предсказанием пилота, сервер обязан
    /// применять кадры в том же порядке и тоже по одному за шаг. При RTT кадры
    /// приходят пачками — без очереди сервер «схлопывал» бы пачку в один залипший
    /// кадр и интегрировал бы его многократно, теряя остальные: последовательность
    /// сдвигов на сервере переставала совпадать с пилотской, и ошибка предсказания
    /// уходила в сотни метров. Очередь хранит кадры до их применения; если очередь
    /// пуста (пилот молчит/кадры теряются), шаг берёт последний принятый кадр
    /// (`_serverInput`) — залипание, как и раньше, чтобы тесты п.7/8 (один кадр на
    /// много шагов) не сломались.</summary>
    private readonly Queue<InputFrame> _serverQueue = new();

    /// <summary>Тик кадра, реально применённого сервером на последнем шаге
    /// (снимается из очереди). Именно он идёт в `Net.AppliedTick`: снимок
    /// соответствует состоянию «после применения кадра с этим тиком», и пилот,
    /// сверяя реплику со своим следом предсказаний на этом тике, получает честную
    /// ошибку предсказания (§2.6 (а)).</summary>
    private uint _serverAppliedTick;

    // Диагностика §2.6: сколько шагов сервер взял кадр из очереди (dequeue) и
    // сколько раз залип на последнем кадре (очередь пуста). Если sticky велико,
    // сервер интегрирует дубликаты кадров, которых пилот не слал → предсказание
    // расходится. Временная диагностика, убрать после разбора.
    private int _dbgDequeue, _dbgSticky;
    private int _dbgReconcile;

    /// <summary>Серверный гейт: от какого пилотского тика начали принимать кадры.
    /// При смене пилота (или его переподключении) счётчик тика нового пилота
    /// начинается с нуля, и старые кадры прежнего пилота обязаны отбрасываться —
    /// иначе «мёртвый» пир продолжал бы управлять кораблём опоздавшими пакетами
    /// (аналог гейта по SimTick из удалённого RpcReportNetStateToServer).</summary>
    private uint _minAcceptedPilotTick;

    /// <summary>Последний принятый сервером пилотский тик: кадры старше
    /// отбрасываются (дубликаты и переупорядоченные пакеты).</summary>
    private uint _lastAcceptedPilotTick;

    /// <summary>Состояние очереди ввода на сервере (для автотестов и панели
    /// отладки): последний принятый кадр и нижняя граница тика.</summary>
    public InputFrame ServerInputFrame => _serverInput;
    public uint ServerInputMinTick => _minAcceptedPilotTick;

    /// <summary>
    /// Пилот: отправить серверу кадр ввода за текущий физшаг. Отправка идёт
    /// КАЖДЫЙ шаг, даже при пустой маске, — иначе отпущенная клавиша залипает на
    /// сервере и корабль разгоняется без команды (§1.2 плана).
    ///
    /// Транспорт — `unreliable` на отдельном канале ввода: потеря кадра ввода
    /// дешевле, чем задержка, а надёжный канал при плохой сети собрал бы очередь
    /// из устаревших команд.
    /// </summary>
    private void SubmitInputToServer(InputFrame frame)
    {
        if (Net == null || Net.ShipNetId == 0)
            return; // адрес 0 → RPC уходят в никуда (§3.1 п.8)
        RpcId(1, nameof(RpcSubmitInput), Net.ShipNetId,
              frame.Tick, frame.Mask, frame.LookYaw, frame.LookPitch);
        // Бюджет ввода (В6 = C): кадр ввода считается отдельно от репликации,
        // чтобы метрика (г) «< 50 пакетов/с» читалась по репликации, а не по сумме.
        NetworkManager.Instance?.CountInputSent();
    }

    // ------------------------------------------------------- буферы задержки (§2.6)
    /// <summary>Поставить кадр ввода в буфер исходящих. Без задержки (`TestLatencyMs
    /// == 0`) отправляем сразу — поведение идентично прежнему. С задержкой кадр
    /// «дозревает» через N мс и уходит позже (имитация медленного аплинка).</summary>
    private void EnqueueInputForSend(InputFrame frame)
    {
        int lat = NetworkManager.TestLatencyMs;
        if (lat <= 0)
        {
            SubmitInputToServer(frame);
            return;
        }
        _sendBuf.Enqueue(((long)Time.GetTicksMsec() + lat, frame));
    }

    /// <summary>Отправить все кадры ввода, «дозревшие» к этому моменту.</summary>
    private void FlushSendBuffer()
    {
        long now = (long)Time.GetTicksMsec();
        while (_sendBuf.Count > 0 && _sendBuf.Peek().DueMs <= now)
            SubmitInputToServer(_sendBuf.Dequeue().Frame);
    }

    /// <summary>Скопировать текущий снимок `NetState` в буфер приёма с задержкой.
    /// `NetState` перезаписывается репликой каждый кадр, поэтому состояние копируем.
    /// Без задержки снимок обрабатывается сразу (дренаж в том же кадре).</summary>
    private void EnqueueSnapshotFromNet(double nowSec)
    {
        var snap = new NetSnap
        {
            SimTick = Net.SimTick,
            AppliedTick = (uint)Net.AppliedTick,
            Pos = Net.NetPosition,
            Rot = Net.NetRotation,
            Lin = Net.NetLinearVelocity,
            Ang = Net.NetAngularVelocity,
        };
        int lat = NetworkManager.TestLatencyMs;
        _recvBuf.Enqueue((nowSec + (lat > 0 ? lat / 1000.0 : 0.0), snap));
    }

    /// <summary>
    /// §2.4 (R4): запись принятого из батча снимка в `NetState`. Это ЕДИНСТВЕННЫЙ
    /// путь поставки эталона для кораблей, снятых с `MultiplayerSynchronizer`:
    /// батч (`SnapshotBroadcaster`) декодирует запись и вызывает этот метод, а
    /// дальнейшая доставка идёт по уже существующему конвейеру: страж
    /// `SimTick != _lastNetTickSeen` в `_Process` замечает сдвиг и вызывает
    /// `EnqueueSnapshotFromNet` — тот же код, что работал с репликами
    /// Synchronizer, поэтому реконсиляция/интерполяция не меняются.
    ///
    /// Монотонность: пишем только если снимок НОВЕЕ уже лежащего (`SimTick`
    /// строго больше). Нестабильный порядок unreliable-пакетов не должен
    /// откатывать состояние назад; просроченный снимок просто игнорируется.
    /// </summary>
    public void ApplyBatchRecord(in SpaceCorporation.Net.ShipSnapshotQ s)
    {
        if (HasAuthority)
            return; // авторитет сам пишет NetState каждый физический шаг (§1088)
        if (s.SimTick <= Net.SimTick)
            return; // дубликат/просрочка: порядок на wire не гарантирован

        Net.NetPosition = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
        Net.NetRotation = new Quaternion(s.Rotation.X, s.Rotation.Y, s.Rotation.Z, s.Rotation.W);
        if (!s.FarTier)
        {
            // Дальний тир скоростей не несёт (14 байт на запись, §2.1): оставляем
            // прежние — наблюдателю они нужны лишь для экстраполяции на ~0.25 с.
            Net.NetLinearVelocity = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
            Net.NetAngularVelocity = new Vector3(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z);
        }
        if (s.ForPilot)
        {
            // Реконсиляция пилота: эталон обязан нести тик/маску применённого
            // ввода, иначе переигрывание стартует не оттуда, откуда надо (§1.5).
            Net.AppliedTick = s.AppliedTick;
            Net.AppliedMask = s.AppliedMask;
        }
        Net.SimTick = s.SimTick; // ПИШЕМ ПОСЛЕДНИМ: страж `_Process` читает его
        // и по сдвигу забирает снимок в буфер приёма; до этого момента поля
        // позы должны быть уже целыми (в одном потоке между кадрами — да).
    }

    /// <summary>Полётное состояние из задержанного снимка (для реконсиляции).</summary>
    private static FlightState StateFromSnap(NetSnap s) => new()
    {
        Position = s.Pos,
        Rotation = s.Rot,
        LinearVelocity = s.Lin,
        AngularVelocity = s.Ang,
    };

    /// <summary>Сервер: принять кадр ввода от пилота. Гейты (по образцу
    /// `Player.IsRequestFromOwner`, §2.3): принимать может только сервер; адрес
    /// кадра обязан совпасть с адресом этого корабля (RPC адресуются по
    /// ShipNetId, а не по пути, §4 В4 п.3); отправитель обязан быть пилотом этого
    /// корабля; тик обязан быть не ниже нижней границы и строго расти.</summary>
    // Шаг R8: кадр ввода идёт unreliable на канале 2 (тот же канал, что и
    // репликационные снимки). Причина: надёжный канал при потерях собрал бы
    // очередь устаревших команд — задержка важнее потерянного кадра, а следующий
    // кадр ввода перекрывает предыдущий (состояние корабля интегрируется по
    // тикам, потерянный кадр = один пропущенный шаг, не рассинхрон).
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable, TransferChannel = 2)]
    public void RpcSubmitInput(int shipNetId, uint tick, uint mask, float lookYaw, float lookPitch)
    {
        // Реальный сетевой приём кадра ввода — в бюджет ввода (В6 = C), а не в
        // репликацию. Считаем до гейта: кадр дошёл по сети независимо от того,
        // примет ли его логика (отклонённый кадр всё равно стоил пакет).
        NetworkManager.Instance?.CountInputRecv();
        AcceptInputFrom(Multiplayer.GetRemoteSenderId(), shipNetId, tick, mask, lookYaw, lookPitch);
    }

    /// <summary>Шов автотеста (§3.9 п.15): «кадр пришёл от пира X» без UDP. В
    /// headless-тесте реального соединения нет, а `Multiplayer.GetRemoteSenderId()`
    /// вне RPC возвращает 0, поэтому тест зовёт тот же метод гейта, что и RPC, —
    /// проверяется боевой код, а не его копия (§3.8).</summary>
    public bool TestSubmitInput(int senderPeer, uint tick, uint mask,
                                float lookYaw = 0f, float lookPitch = 0f)
        => AcceptInputFrom(senderPeer, Net?.ShipNetId ?? 0, tick, mask, lookYaw, lookPitch);

    /// <summary>Серверные гейты кадра ввода + приём в очередь. Возвращает true,
    /// если кадр принят. Счётчики принявших/отклонённых кадров — для assert'ов
    /// п.7/п.8 автотеста.</summary>
    private bool AcceptInputFrom(int senderPeer, int shipNetId, uint tick, uint mask,
                                 float lookYaw, float lookPitch)
    {
        if (NetworkManager.Instance is not { IsServer: true })
            return false; // только сервер интегрирует корабль (В14=B)
        if (Net == null || Net.ShipNetId != shipNetId)
            return false; // кадр про другой корабль (адрес важнее пути)
        if (!HasAuthority)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: кадр ввода пришёл пиру, который не является " +
                        $"авторитетом корабля (auth = {GetMultiplayerAuthority()}) — отказ");
            RejectedInputCount++;
            return false;
        }

        if (senderPeer != PilotPeerId)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: кадр ввода от пира {senderPeer}, а пилот = " +
                        $"{PilotPeerId} — отказ (§3.5 п.8)");
            RejectedInputCount++;
            return false;
        }

        if (tick < _minAcceptedPilotTick || tick <= _lastAcceptedPilotTick)
        {
            RejectedInputCount++;
            return false; // устаревший/дублирующийся кадр прежнего пилота
        }

        _lastAcceptedPilotTick = tick;
        // В провод летят только tick+mask+мышь (16 байт, §1.3); оси обязан
        // восстановить сервер — ровно так же, как это делает клиент в
        // `CollectInputFrame` (через `FromMask`). Иначе `FlightModel.Step`,
        // читающий оси (AxisF/…), не увидел бы тягу: кадр с ThrustF дал бы
        // нулевое ускорение (§3.5 п.7).
        _serverInput = InputFrame.FromMask(tick, mask, lookYaw, lookPitch);
        // Кладём кадр в очередь: сервер применит его ровно один раз на своём шаге
        // (в порядке тиков), как пилот применил бы на своём. Так последовательность
        // сдвигов сервера совпадает с предсказанием пилота при любой задержке.
        _serverQueue.Enqueue(_serverInput);
        AcceptedInputCount++;
        LastAcceptedInputTick = (int)tick;
        return true;
    }

    /// <summary>Сколько кадров ввода принял и отклонил сервер (автотесты п.7-п.8).</summary>
    public int AcceptedInputCount { get; private set; }
    public int RejectedInputCount { get; private set; }

    /// <summary>Тик последнего принятого кадра (для панели отладки: по нему видно,
    /// сколько кадров потеряно на unreliable-канале).</summary>
    public int LastAcceptedInputTick { get; private set; }

    /// <summary>Кадр ввода для очередного шага модели: сервер берёт его из очереди
    /// пилота, offline-пилот — читает клавиши и мышь напрямую. Разделение нужно
    /// потому, что сервер физически НЕ имеет доступа к чужой клавиатуре, а
    /// offline-сервер (он же пилот) не должен гонять ввод через RPC-никуда.</summary>
    private InputFrame FrameForStep()
    {
        // Offline: сервер = локальный пир, ввод читается с реальных клавиш.
        if (IsLocalPilot)
            return CollectInputFrame();
        // Сервер с внешним пилотом: применяем кадры пилота по одному за шаг, в
        // порядке тиков (очередь) — так серверная последовательность сдвигов
        // совпадает с предсказанием пилота при любой задержке (§2.6 фикс (а)).
        // Если очередь пуста (пилот молчит/кадры теряются) — залипаем на последнем
        // принятом кадре, чтобы молчание не обнуляло тягу мгновенно и тесты п.7/8
        // (один кадр на много шагов) работали как раньше.
        if (_serverQueue.Count > 0)
        {
            _serverInput = _serverQueue.Dequeue();
            _serverAppliedTick = _serverInput.Tick;
            _dbgDequeue++;
        }
        else
            _dbgSticky++;
        return _serverInput;
    }

    /// <summary>Сервер: начать принимать кадры пилота с его текущего тика.
    /// Вызывается при назначении пилота, чтобы счётчик нового пилота не «отсекся»
    /// гейтом и чтобы кадры прежнего пилота перестали применяться.</summary>
    private void ResetInputQueue()
    {
        _serverInput = default;
        _serverQueue.Clear();
        _serverAppliedTick = 0;
        _lastAcceptedPilotTick = 0;
        // Нижняя граница: тик серверного корабля. Пилотский счётчик в общем случае
        // не совпадает с серверным (у пилота свои физкадры), поэтому шаг 2.4
        // введет единую шкалу тиков; на шаге 2.3 принимаем кадры с любого тика,
        // кроме заведомо старых (0 = кадр до назначения пилота).
        _minAcceptedPilotTick = 1;
    }


    /// <summary>
    /// Физический шаг. С шага 2.3 метод работает для двух ролей (§3.5 п.8):
    ///   - авторитет (сервер): снимает состояние тела в `NetState`, откуда
    ///     `MultiplayerSynchronizer` рассылает снапшоты (30 Гц); саму физику тела
    ///     ведёт `_IntegrateForces`;
    ///   - пилот-клиент: НЕ трогает тело (оно заморожено) — собирает кадр ввода за
    ///     свой физшаг, отправляет его серверу и ведёт собственный счётчик тика.
    ///     Предсказание движения на клиенте — шаг 2.4.
    /// Offline: сервер = локальный пир, поэтому работает только первая ветка.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (Net == null)
            return;

        if (HasAuthority)
        {
            // Шаг 3.5: принятые запросы выстрела исполняются здесь, а не в RPC
            // (риск №3 плана): `_PhysicsProcess` идёт после шага физики, поэтому
            // `IntersectRay` видит актуальные тела, а не позиции прошлого кадра.
            ProcessPendingFires();
            // Шаг 2.6, метрика (г): публикуем сетевой снимок раз в NetPublishEveryN
            // физшагов (30 Гц), а не каждый шаг (60 Гц). OnChange-репликация уходит
            // только когда свойство реально меняется, поэтому «реже пишем» = «реже
            // шлём». Симуляция тела при этом полные 60 Гц (в `_IntegrateForces`),
            // реже публикуется лишь сетевой слепок. См. `_netPublishPhase`.
            if (++_netPublishPhase % NetPublishEveryN == 0)
            {
                Net.NetPosition = GlobalPosition;
                // В буфере лежит Quaternion: Basis.Quat() (в C# GlobalRotation — это эйлеровы
                // углы Vector3, кватернион берётся из базиса глобального трансформ).
                Net.NetRotation = new Quaternion(GlobalTransform.Basis);
                Net.NetLinearVelocity = LinearVelocity;
                Net.NetAngularVelocity = AngularVelocity;
                Net.SimTick = (int)Engine.GetPhysicsFrames();

                // Шаг 2.4, §1.5: пилоту (не-авторитету, которому Synchronizer шлёт
                // реплики) мало знать, ГДЕ корабль, — надо знать, какой кадр ввода
                // сервер применил, чтобы получить это состояние. Иначе реконсиляция
                // не поймёт, какие кадры из буфера предсказания уже учтены эталоном,
                // а какие ещё надо переиграть (§1.6).
                // §2.6 фикс: `_PhysicsProcess` выполняется ДО `_IntegrateForces` этого
                // шага, значит позиция тела в снимке уже включает все кадры, снятые из
                // очереди на ПРЕДЫДУЩИХ шагах. Поэтому AppliedTick = `_serverAppliedTick`
                // (тик последнего реально проинтегрированного кадра), а не
                // `_lastAcceptedPilotTick` (тик последнего ПРИНЯТОГО, но ещё не применённого).
                // Пилот, глянув в свой след предсказаний на этом тике, сравнивает
                // однородные состояния — честная ошибка предсказания без упреждения.
                Net.AppliedTick = (int)_serverAppliedTick;
                Net.AppliedMask = _serverInput.Mask;
            }

            // Диагностика §2.6: dequeue vs sticky на сервере (временная).
            if (NetworkManager.IsHeadlessCli())
            {
                _metricsTimer += (float)delta;
                if (_metricsTimer >= 1f)
                {
                    _metricsTimer = 0f;
                    GD.Print($"[srv-metrics ship#{Net.ShipNetId}] dequeue={_dbgDequeue} sticky={_dbgSticky} " +
                             $"q={_serverQueue.Count} accepted={AcceptedInputCount} appliedTick={_serverAppliedTick}");
                }
            }

            // Шаг 2.3 (В14=B): репорт «позиция снизу вверх» удалён. Сервер — единственный
            // интегратор корабля, поэтому принимать чужую позицию ему не от кого: от
            // пилота приходят кадры ВВОДА (`RpcSubmitInput`), а не состояние (§3.5 п.8).
            return;
        }

        // Пилот-клиент (не-авторитет, В14=B): каждый физшаг собирает кадр ввода,
        // кладёт его в кольцевой буфер, шлёт серверу И тут же интегрирует им своё
        // предсказание, а тело (вместе с картинкой и камерой — они дети тела)
        // ставит в это предсказание. Тело заморожено (`Freeze = Static`), поэтому
        // физически оно никуда не «летит» само: позиция — только то, что мы сами
        // в него запишем (проба `tests/probes/probe_frozen_move.tscn`: запись в
        // замороженное тело приживается). Пустая маска тоже отправляется (иначе
        // отпущенная клавиша залипнет на сервере, §1.2 плана).
        if (IsLocalPilot)
        {
            // Фиксированный шаг, как у сервера (`sd.GetStep()` = 1/60), а НЕ
            // переменный `delta`: предсказание обязано сходиться с серверной
            // истиной побитово при одинаковом вводе (риск №1 плана: при нулевом
            // лаге Prediction Error ≈ 0). Переменный delta (просадки кадров,
            // первый кадр) ломал бы детерминизм и давал бы метры ложной ошибки.
            float dt = FlightModel.FixedDt;
            InputFrame frame = CollectInputFrame();

            // Первое вхождение в пилотство: предсказание стартует из текущего
            // положения тела (истины, которую сервер уже нарисовал репликой).
            if (!_hasPrediction)
            {
                _predicted = FlightState.FromNode(this, LinearVelocity, AngularVelocity);
                _hasPrediction = true;
            }

            // Кадр в кольцо по индексу tick — чтобы переиграть его при расхождении.
            _pending[frame.Tick & (PendingRingSize - 1)] = frame;

            // Отправка ввода серверу. С швом `TestLatencyMs` (§2.6) кадр кладётся в
            // буфер исходящих и «дозревает» через N мс — так измеряется поведение
            // при RTT ≈ 2×N, не трогая ENet. При N=0 буфер пустеет в тот же шаг,
            // поведение идентично прежнему.
            EnqueueInputForSend(frame);
            // Отправить всё, что «дозрело» к этому физшагу (при N=0 — текущий кадр).
            FlushSendBuffer();

            // Локальное предсказание тем же `FlightModel`, что и сервер (один и тот
            // же код = сходимость при одинаковом вводе). `AdvanceFlight` двигает и
            // локальный счётчик `_tick` (кадр уже отправлен с прежним тиком).
            _predicted = AdvanceFlight(_predicted, frame, dt);

            // Запоминаем предсказанную позицию ПИЛОТА на тике этого кадра ввода.
            // Сервер, применив кадр с тиком T, вернёт снимок с AppliedTick = T и
            // позицией «после применения T» — ровно то состояние, что и наше
            // предсказание сразу после `AdvanceFlight(frame T)`. Сверка по этому
            // следу даёт истинную ошибку предсказания (§2.6 (а)), без упреждения.
            _predPosHistory[frame.Tick & (PendingRingSize - 1)] = _predicted.Position;
            _predPosTick[frame.Tick & (PendingRingSize - 1)] = frame.Tick + 1; // +1 = «слот занят» (тик 0 не путать с пустым)
            _predVelHistory[frame.Tick & (PendingRingSize - 1)] = _predicted.LinearVelocity;
            _predAngVelHistory[frame.Tick & (PendingRingSize - 1)] = _predicted.AngularVelocity;

            // Картинка и камера — за предсказанием.
            SetBodyToPrediction();
        }
    }

    /// <summary>Поставить тело (а с ним картинку и камеру — они дети тела) в
    /// предсказанное состояние пилота. Только позиция/ориентация: скорость у
    /// замороженного тела не используется, а писать её было бы ложью (истину
    /// считает сервер).</summary>
    private void SetBodyToPrediction()
    {
        GlobalTransform = new Transform3D(new Basis(_predicted.Rotation), _predicted.Position);
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

        // Метрики §2.6 в headless-стенде: Prediction Error и число жёстких откатов
        // (пилот), макс дельта hull (наблюдатель) — раз в секунду в stdout, чтобы
        // прогон `run-net-demo.sh` читался в логе так же, как дебаг-панель в окне.
        if (NetworkManager.IsHeadlessCli())
        {
            _metricsTimer += (float)delta;
            if (_metricsTimer >= 1f)
            {
                _metricsTimer = 0f;
                GD.Print($"[metrics ship#{Net.ShipNetId}] role={(IsLocalPilot ? "pilot" : "observer")} " +
                         $"predErr={PredictionError:F3} hardRollbacks={HardRollbackCount} " +
                         $"maxHullDelta={MaxHullDelta:F3} interpBuf={_interpBuf.Count} " +
                         $"interpDelay={_interpDelay:F3} snapPeriod={_snapPeriodEma:F3}");
                // Окно метрики (в): за секунду — максимум дельт hull за ЭТУ секунду,
                // дальше сбрасываем, чтобы разовый стартовый скачок не висел в
                // каждой следующей строке (сессия-макс не убывает). hardRollbacks
                // НЕ сбрасываем — это накопительный счётчик, по нему смотрим
                // среднюю частоту откатов за прогон.
                MaxHullDelta = 0f;
            }
        }

        // Пилот-клиент: картинка и камера идут за ПРЕДСКАЗАНИЕМ (тело ставит в
        // него `_PhysicsProcess`), поэтому интерполяция ему НЕ нужна — она бы
        // дралась с предсказанием (двигала бы hull назад, в прошлое). Вместо неё —
        // реконсиляция: как только пришла новая реплика эталона (`SimTick` сдвинулся),
        // сверить предсказание с эталоном и при необходимости откатить/переиграть.
        // Снимок идёт через буфер приёма (`TestLatencyMs`), поэтому сверка происходит
        // в момент «доставки», а не в момент, когда Synchronizer принёс реплику.
        if (IsLocalPilot)
        {
            // Страж «последний поставленный в буфер SimTick» — не `_lastReconcileTick`
            // (тот двигается в момент ДОСТАВКИ, а не постановки, иначе с задержкой
            // один и тот же SimTick попадал бы в буфер каждый кадр).
            if (Net.SimTick != _lastNetTickSeen)
            {
                _lastNetTickSeen = Net.SimTick;
                // Один сдвиг SimTick = один логический репликационный снимок этого
                // корабля (порог (г) §2.7 меряется по ним, а не по wire-пакетам ENet).
                NetworkManager.Instance?.CountReplicationRecv(Net.ShipNetId);
                EnqueueSnapshotFromNet(now);
            }
            DrainRecvBufferForPilot();
            return;
        }

        // Наблюдатель (не пилот): интерполяция картинки по снимкам.
        // Новый снимок (SimTick сдвинулся — Synchronizer принёс обновление)? Кладём
        // его в буфер приёма; в `_interpBuf` он попадёт в момент «доставки».
        if (Net.SimTick != _lastNetTickSeen)
        {
            _lastNetTickSeen = Net.SimTick;
            NetworkManager.Instance?.CountReplicationRecv(Net.ShipNetId);
            EnqueueSnapshotFromNet(now);
        }
        DrainRecvBufferForObserver(now);

        InterpolateHull(now);

        // Метрика критерия (в) §2.6: максимальный скачок hull наблюдателя между
        // соседними кадрами. Если картинка «резинит» (дельта > 0.15 м) — интерполяция
        // не справляется с лагом/потерями.
        //
        // Мерим ТОЛЬКО установившийся полёт, не стартовый снимок: пока буфер
        // интерполяции не наполнился хотя бы двумя снимками, hull делает ОДИН
        // скачок из точки спавна в первую «прогретую» позицию сервера (на стенде
        // при RTT≈200 мс это ~10 м). Это не «резинг», а появление корабля там, где
        // его видит сервер; критерий (в) про плавность В ПОЛЁТЕ. Поэтому счёт
        // дельт включаем, когда `_interpBuf.Count >= 2` (интерполяция уже работает).
        // Значение за секунду — окно, сбрасываемое при печати метрик (см.
        // `_metricsTimer`), чтобы разовый стартовый скачок не «засветился» на всех
        // последующих секундах (сессия-макс никогда не убывает).
        var hullNode = GetNodeOrNull<Node3D>("ShipHull");
        if (hullNode != null && _interpBuf.Count >= 2)
        {
            var hp = hullNode.GlobalPosition;
            if (_haveHullPos)
            {
                float d = hp.DistanceTo(_lastHullPos);
                if (d > MaxHullDelta)
                    MaxHullDelta = d;
            }
            _lastHullPos = hp;
            _haveHullPos = true;
        }
    }

    /// <summary>Дренаж буфера приёма для пилота: каждый «дозревший» снимок — кадр
    /// реконсиляции (откат/переигровка). Снимки, пришедшие задним числом (несколько
    /// дозрели за кадр), обрабатываются по порядку SimTick.</summary>
    private void DrainRecvBufferForPilot()
    {
        double nowSec = Time.GetTicksMsec() / 1000.0;
        while (_recvBuf.Count > 0 && _recvBuf.Peek().DueSec <= nowSec)
        {
            var s = _recvBuf.Dequeue().Snap;
            if (s.SimTick == _lastReconcileTick)
                continue; // дубликат того же тика — сверяться нечем
            _lastReconcileTick = s.SimTick;
            ReconcileWith(StateFromSnap(s), s.AppliedTick);
        }
    }

    /// <summary>Дренаж буфера приёма для наблюдателя: «дозревшие» снимки попадают в
    /// буфер интерполяции по местному времени приёма; старьё за окном режется.</summary>
    private void DrainRecvBufferForObserver(double now)
    {
        while (_recvBuf.Count > 0 && _recvBuf.Peek().DueSec <= now)
        {
            var entry = _recvBuf.Dequeue();
            var s = entry.Snap;
            // §3.9 (ключ): штампим снимок его ЖУРНАЛЬНЫМ временем доставки
            // (`DueSec`), а не текущим `now`. Если после сетевой заминки в одном
            // кадре «дозревает» пачка снимков, при штампе `now` все они получают
            // одну и ту же отметку → вырожденные сегменты (нулевой интервал) и
            // hull прыгает на весь путь пачки за кадр. DueSec сохраняет ровный
            // шаг приёма, каким бы ни был джиттер доставки.
            double stamp = entry.DueSec;
            _interpBuf.Add(new Snapshot(stamp, s.Pos, s.Rot));
            _adaptPending = true; // пересчёт адаптивной задержки ниже, один раз за кадр
        }

        // §3.9: адаптивная задержка — один пересчёт за кадр (не за снимок!),
        // иначе пачка «дозревших» снимков даёт несколько шагов задержки в одном
        // кадре и сама рисует скачок (тот же артефакт, что лечим). Замеряем
        // ровный шаг между отметками снимков в буфере интерполяции.
        AdaptInterpDelay(now);

        // Окно буфера — достаточно глубокое, чтобы накрывать адаптивную
        // задержку: render = now - _interpDelay должен остаться внутри окна.
        // Держим минимум 2 снимка (иначе нечего интерполировать — «ждать»).
        double window = System.Math.Max(InterpDelay, _interpDelay) * 2.5;
        while (_interpBuf.Count > 2 && now - _interpBuf[0].Time > window)
            _interpBuf.RemoveAt(0);
    }

    // Осталось ли что пересчитать за этот кадр (выставляется при дренаже).
    private bool _adaptPending;
    // Первый валидный замер периода — ставим задержку сразу в цель (без рампы),
    // чтобы наблюдатель не оставался голодным несколько кадров на старте.
    private bool _bootstrapDone;
    private int _periodSamples;            // сколько оценок периода уже сделано
    /// <summary>§3.9: пересчёт адаптивной задержки интерполяции (один раз за
    /// кадр). Период меряем по шагу между отметками снимков (`DueSec`), с
    /// пик-холдом по худшему недавнему зазору; задержка = factor × пик периода
    /// (2 снимка в окне), плавно сходится к цели с быстрым ростом и медленным
    /// спадом. Критерий (в) §2.6: render=now-delay всегда внутри окна → нет
    /// скачков hull на наблюдателе при ≥3 кораблях.</summary>
    private void AdaptInterpDelay(double now)
    {
        if (!_adaptPending) return;
        _adaptPending = false;

        // Период — шаг между двумя последними добавленными снимками (по DueSec,
        // ровный). Считаем только валидный положительный шаг.
        if (_interpBuf.Count >= 2)
        {
            double period = _interpBuf[^1].Time - _interpBuf[^2].Time;
            if (period > 0.0 && period < SnapPeriodMax)
            {
                if (_snapPeriodEma < 0.0) _snapPeriodEma = period;
                else _snapPeriodEma = _snapPeriodEma * 0.6 + period * 0.4;

                // Пик-холд почти «липкий»: держим худший недавний зазор очень
                // долго (полупериод спада ~десятки секунд на частоте снимков).
                // Иначе единичный быстрый зазор роняет задержку, render=now-delay
                // снова выезжает за самый свежий снимок → мёрзнет/прыгает —
                // самоподдерживающаяся осцилляция. Держим запас по голоданию.
                if (period > _snapGapPeak || _snapGapPeak < 0.0)
                    _snapGapPeak = period;
                else
                    _snapGapPeak = _snapGapPeak * 0.995 + period * 0.005;

                double worst = System.Math.Max(_snapPeriodEma, _snapGapPeak);
                double target = Mathf.Clamp(
                    (float)(AdaptiveDelayFactor * worst),
                    (float)InterpDelay, (float)AdaptiveDelayMax);

                // Первый замер периода — bootstrap: ставим задержку прямо в цель
                // (без рампы). Рампа нужна только для плавного слежения за
                // ДЖИТТЕРОМ в установившемся режиме; на старте буфер ещё не
                // заполнен и картинка не может «прыгнуть», зато рампа оставляет
                // наблюдателя голодным на несколько кадров (скачок hull при
                // догоняющем render=now-delay). NetTest-шов сюда не заходит
                // (TestPushSnapshot минует дренаж), его математика не тронута.
                if (!_bootstrapDone && ++_periodSamples >= 3)
                {
                    _interpDelay = target;
                    _bootstrapDone = true;
                }
                else if (_bootstrapDone)
                {
                    // ГИСТЕРЕЗИС с мёртвой зоной: держим задержку ПОСТОЯННОЙ против
                    // дрожания EMA. Собственный дрейф задержки сам рисует скачок
                    // hull на v×Δdelay (при 50 м/с Δdelay=0,02 → скачок 1 м, т.е.
                    // лечение вносит ровно тот артефакт, что лечит). Двигаем
                    // задержку только за пределами широкой мёртвой зоны: вверх —
                    // если реальное голодание выросло (недобрли окно), вниз — если
                    // явно переборщили. Внутри зоны — не трогаем вовсе.
                    double delta = target - _interpDelay;
                    if (delta > DelayRampUpBand)
                    {
                        double step = System.Math.Min(delta, MaxDelayStepUpPerFrame);
                        _interpDelay += step;
                    }
                    else if (delta < -DelayReleaseBand)
                    {
                        double step = System.Math.Max(delta, -MaxDelayStepPerSnap);
                        _interpDelay += step;
                    }
                }
            }
        }
    }

    /// <summary>Ядро реконсиляции против переданного эталона (`server`) и тика,
    /// который сервер применил (`appliedTick`). Вынесено из `Reconcile`, чтобы
    /// автотесты п.9-11 могли подать эталон напрямую, без настоящего
    /// `NetState`/Synchronizer (шов `TestReconcile`).</summary>
    private void ReconcileWith(FlightState server, uint appliedTick)
    {
        // Предсказания ещё нет (первая реплика раньше первого физшага) — принять
        // эталон как истину и стартовать от него.
        if (!_hasPrediction)
        {
            _predicted = server;
            _hasPrediction = true;
            SetBodyToPrediction();
            return;
        }

        // ЧЕСТНАЯ метрика предсказания (§2.6 критерий (а), < 0.3 м). Серверный снимок
        // соответствует состоянию «после применения кадра с AppliedTick». Сравнивать
        // его с ТЕКУЩИМ предсказанием пилота (которое ушло вперёд на скорость×RTT) —
        // значит мерить «упреждение», а не ошибку: при 100 мс RTT и десятках м/с это
        // сотни метров ложной ошибки и жёсткий откат на каждом снимке. Поэтому сверяем
        // эталон с тем предсказанием, что пилот ДЕРЖАЛ ровно на AppliedTick (след
        // _predPosHistory). Если такого следа нет (тесты п.9-11 сеют предсказание
        // напрямую, без истории) — откатываемся к прежней мере |текущее − эталон|.
        float err;
        bool hit = TryGetPredictedAt(appliedTick, out Vector3 predAtTick);
        if (hit)
            err = predAtTick.DistanceTo(server.Position);
        else
            err = _predicted.Position.DistanceTo(server.Position);
        PredictionError = err;

        // Диагностика §2.6 (временная): пишем только крупные расхождения (>0.3),
        // чтобы поймать природу пиков в установившемся режиме.
        if (NetworkManager.IsHeadlessCli() && err > 0.3f && _dbgReconcile++ < 30)
            GD.Print($"[recon ship#{Net.ShipNetId}] appliedTick={appliedTick} pilotTick={_tick} " +
                     $"hit={hit} err={err:F3} serverPos={server.Position} curPred={_predicted.Position} " +
                     $"predAtTick={(hit ? predAtTick : Vector3.Zero)}");

        if (err > ReconcileThreshold)
        {
            // Жёсткий откат к эталону + переигровка неподтверждённых кадров.
            _predicted = server;
            ReplayInputsAfter(appliedTick);
            // Метрика критерия (б) §2.6: считаем каждый жёсткий откат-телепорт.
            HardRollbackCount++;
        }
        else
        {
            // Мягкая синхронизация: позиция остаётся предсказанной (она уже в
            // пределах порога от эталона). Скорости снимка относятся к AppliedTick,
            // а _predicted ушёл вперёд на RTT/2 кадров — подставлять серверную
            // скорость как АБСОЛЮТ нельзя: это выбрасывает прирост скорости за эти
            // кадры, пилот отстаёт, ошибка нарастает до порога и каждый снимок даёт
            // жёсткий откат (зубчатый hardRollbacks). Берём РАЗНОСТЬ скоростей на
            // AppliedTick (сервер минус пилот) и докладываем в текущую предсказанную
            // скорость: при детерминизме разница ≈ 0 (зубцов нет), реальный дрейф
            // корректируется. Если следа скорости на AppliedTick нет (тесты п.9-11
            // сеют только позицию) — падаемся на абсолют, как раньше.
            if (hit && TryGetPredictedVelAt(appliedTick, out Vector3 predVelAtTick, out Vector3 predAngAtTick))
            {
                _predicted.LinearVelocity += server.LinearVelocity - predVelAtTick;
                _predicted.AngularVelocity += server.AngularVelocity - predAngAtTick;
            }
            else
            {
                _predicted.LinearVelocity = server.LinearVelocity;
                _predicted.AngularVelocity = server.AngularVelocity;
            }
            // Кадры не переигрывались — сбрасываем счётчик, иначе он остался бы
            // от прошлой жёсткой сверки и автотест п.10 прочитал бы мусор.
            LastReplayedCount = 0;
        }

        SetBodyToPrediction();
    }

    /// <summary>Найти предсказанную позицию пилота на тике `tick` (след истории).
    /// В кольце хранится `tick+1` как признак валидности (0 = слот пуст), чтобы
    /// тик 0 не совпадал с пустым слотом. Возвращает false, если следа нет (кольцо
    /// перемололось или предсказание на этом тике не строилось) — тогда вызывающий
    /// мерит ошибку по-старому.</summary>
    private bool TryGetPredictedAt(uint tick, out Vector3 pos)
    {
        int idx = (int)(tick & (uint)(PendingRingSize - 1));
        if (_predPosTick[idx] == tick + 1)
        {
            pos = _predPosHistory[idx];
            return true;
        }
        pos = Vector3.Zero;
        return false;
    }

    /// <summary>То же, что `TryGetPredictedAt`, но возвращает линейную и угловую
    /// скорости предсказания пилота на тике `tick` (след `_predVelHistory` /
    /// `_predAngVelHistory`, валидность по `_predPosTick`). Нужен для мягкой
    /// синхронизации по РАЗНОСТИ скоростей.</summary>
    private bool TryGetPredictedVelAt(uint tick, out Vector3 vel, out Vector3 angVel)
    {
        int idx = (int)(tick & (uint)(PendingRingSize - 1));
        if (_predPosTick[idx] == tick + 1)
        {
            vel = _predVelHistory[idx];
            angVel = _predAngVelHistory[idx];
            return true;
        }
        vel = Vector3.Zero;
        angVel = Vector3.Zero;
        return false;
    }

    /// <summary>Собрать `FlightState` из принятой реплики `NetState` (эталона).</summary>
    private FlightState SnapshotToFlightState() => new()
    {
        Position = Net.NetPosition,
        Rotation = Net.NetRotation,
        LinearVelocity = Net.NetLinearVelocity,
        AngularVelocity = Net.NetAngularVelocity,
    };

    /// <summary>Переиграть из кольца все кадры с `Tick > appliedTick` и `Tick <
    /// _tick` (последний отправленный кадр = `_tick - 1`). Каждый переигранный кадр
    /// двигает `_predicted` тем же `StepPrediction`, что и обычный шаг. Пустые или
    /// перезаписанные кольцом слоты пропускаются (`f.Tick != t`) — потерянный на
    /// unreliable-канале кадр = дыра, её нечем переиграть, и это нормально (§1.4).</summary>
    private void ReplayInputsAfter(uint appliedTick)
    {
        LastReplayedCount = 0;
        // `_tick` после шага = следующий тик; последний отправленный кадр = _tick-1.
        for (uint t = appliedTick + 1; t < _tick; t++)
        {
            InputFrame f = _pending[t & (PendingRingSize - 1)];
            if (f.Tick != t)
                continue; // слот пуст или перезаписан кольцом — дыра, пропускаем
            _predicted = StepPrediction(_predicted, f, FlightModel.FixedDt);
            // ОБЯЗАТЕЛЬНО обновляем след истории на переигранном тике: он теперь
            // отражает ИСПРАВЛЕННОЕ предсказание (после отката к эталону). Без этого
            // кольцо хранит СТАРОЕ (дооткатное) предсказание, и следующий снимок
            // сверяется с ним → ложная большая ошибка → ещё один жёсткий откат →
            // автоколебание `hardRollbacks` (зубцы ~10/сек при RTT≈0).
            int idx = (int)(t & (uint)(PendingRingSize - 1));
            _predPosHistory[idx] = _predicted.Position;
            _predPosTick[idx] = t + 1;
            _predVelHistory[idx] = _predicted.LinearVelocity;
            _predAngVelHistory[idx] = _predicted.AngularVelocity;
            LastReplayedCount++;
        }
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

        // Интерполяция выключена (кнопка `Toggle Interpolation`): картинка идёт
        // прямо в ПОСЛЕДНИЙ принятый снимок, без сдвига на InterpDelay и без
        // смешивания. Дискретно, но совпадает с сетью по времени (автотест п.13).
        if (!_interpolationEnabled)
        {
            ApplyHull(hull, _interpBuf[^1]);
            return;
        }

        double render = now - _interpDelay;

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

    // ==================================================================
    // Шаг 2.2.3: камера-преследование и захват мыши
    // ==================================================================

    /// <summary>
    /// Камера от третьего лица за кормой (шаг 2.2.3). Камера обязана быть
    /// ДОЧЕРНЕЙ узлом тела: тогда её позиция и ориентация берутся из трансформа
    /// тела автоматически, и писать её каждый кадр не нужно (а писать пришлось
    /// бы, потому что картинка `ShipHull` для не-авторитета интерполируется, а
    /// камера обязана смотреть туда, куда летит ИСТИНА).
    ///
    /// Чужую камеру (обычно `Player/Camera3D`) глушим: две current-камеры в одном
    /// viewport дают случайный вид, а не «последняя победила».
    /// </summary>
    private void EnsureChaseCamera()
    {
        if (_chaseCam == null)
        {
            _chaseCam = new Camera3D
            {
                Name = "ChaseCamera",
                // Смещение назад по -Z корпуса = за кормой (вперёд у Godot = -Z).
                Position = new Vector3(0f, CameraHeight, CameraDistance),
                Current = true,
                // Дальность видимости: без неё корабль улетает за far plane и
                // «исчезает» — выглядит как падение физики.
                Far = 4000f,
            };
            AddChild(_chaseCam);
        }
        else
        {
            _chaseCam.Position = new Vector3(0f, CameraHeight, CameraDistance);
            _chaseCam.Current = true;
        }

        // Камера игрока: гасим, чтобы она не спорила с камерой корабля.
        if (_silencedCam == null || !GodotObject.IsInstanceValid(_silencedCam))
            _silencedCam = GetViewport()?.GetCamera3D() == _chaseCam
                ? null
                : GetViewport()?.GetCamera3D();
        if (_silencedCam != null && _silencedCam != _chaseCam)
            _silencedCam.Current = false;

        // Захват мыши: режим ставим ТОЛЬКО когда его ещё не перехватывали, и
        // обязательно возвращаем прежний при освобождении (иначе после выхода из
        // корабля курсор исчезнет навсегда).
        if (!_capturedInput)
        {
            _capturedInput = true;
            _prevMouseMode = Input.MouseMode;
            // Свежая посадка в пилотское кресло — всегда режим пилотирования
            // (мышь захвачена), даже если в прошлой сессии игрок ушёл в
            // взаимодействие.
            Mode = ControlMode.Pilot;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    /// <summary>Отдать камеру и мыши обратно: корабль перестал пилотироваться
    /// (пассажир, выход, смена авторитета, удаление узла).</summary>
    private void ReleaseChaseCamera()
    {
        if (_chaseCam != null && GodotObject.IsInstanceValid(_chaseCam))
        {
            _chaseCam.Current = false;
            _chaseCam.QueueFree();
            _chaseCam = null;
        }
        if (_silencedCam != null && GodotObject.IsInstanceValid(_silencedCam))
            _silencedCam.Current = true;
        _silencedCam = null;

        if (_capturedInput)
        {
            _capturedInput = false;
            Input.MouseMode = _prevMouseMode;
        }
        _lookDelta = Vector2.Zero;
    }

    // ==================================================================
    // Шаг 2.2.2: сборщик мыши пилота
    // ==================================================================

    /// <summary>
    /// Завести узел-сборщик мыши (шаг 2.2.2). Он нужен потому, что `Ship` — это
    /// `RigidBody3D`, то есть `CollisionObject3D`: у таких узлов метод `_input`
    /// вызывается только когда вьюпорт пикирует именно это тело, и мышиные
    /// события в живой прогон к кораблю не приходят (проба
    /// `tests/probes/ProbeInputHeadless.tscn`: событие дошло до корня сцены и до
    /// дочернего `Node3D`, но не до корабля, даже при `input_ray_pickable = true`).
    /// Обычный `Node3D`-потомок мышь получает всегда, поэтому ввод мыши собирает
    /// он и передаёт кораблю напрямую.
    ///
    /// Узел создаётся один раз и живёт, пока корабль пилотируется; при освобождении
    /// удаляется, чтобы чужой пир не копил дельты.
    /// </summary>
    private ShipMouseCatcher? _mouseCatcher;

    private void EnsureMouseCatcher()
    {
        if (_mouseCatcher != null && GodotObject.IsInstanceValid(_mouseCatcher))
            return;
        _mouseCatcher = new ShipMouseCatcher(this) { Name = "MouseCatcher" };
        AddChild(_mouseCatcher);
    }

    /// <summary>Убрать сборщик мыши (корабль перестал пилотироваться локально).</summary>
    private void ReleaseMouseCatcher()
    {
        if (_mouseCatcher != null && GodotObject.IsInstanceValid(_mouseCatcher))
            _mouseCatcher.QueueFree();
        _mouseCatcher = null;
        _lookDelta = Vector2.Zero;
    }

    /// <summary>
    /// Держатель мыши пилота: обычный `Node3D`, чей `_input` вьюпорт вызывает без
    /// пикинга. Отдаёт дельту кораблю и ловит переключатель режимов (правая кнопка
    /// мыши, действие `toggle_interaction`).
    /// </summary>
    private partial class ShipMouseCatcher : Node3D
    {
        private readonly Ship _ship;

        public ShipMouseCatcher(Ship ship) => _ship = ship;

        public override void _Input(InputEvent @event)
        {
            // Правая кнопка мыши: пилотирование ↔ взаимодействие. Ловим именно
            // здесь, а не в `Ship._Input`: `Ship` — `RigidBody3D`, и его `_input`
            // вьюпорт вызывает только при пикинге тела (проба
            // `tests/probes/ProbeInputHeadless.tscn`), а этот узел получает всё.
            if (@event.IsActionPressed("toggle_interaction"))
            {
                _ship.ToggleInteractionMode();
                return;
            }
            if (@event is InputEventMouseMotion m)
            {
                _ship.HandleMouseMotion(m);
                return;
            }
            // Шаг 3.3: ЛКМ (действие `fire`) — запрос выстрела. Гейты
            // (IsLocalPilot && !Interaction && AimOnShip) внутри RequestFire;
            // ловушка существует только у локального пилота (EnsureMouseCatcher),
            // но гейт на всякий случай продублирован внутри — шов теста дёргает
            // RequestFire напрямую, минуя мышь.
            if (@event.IsActionPressed("fire"))
                _ship.RequestFire();
        }
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
        _lookDelta = Vector2.Zero;

        // Курсор: в Captured он «пришит» к центру окна и физически не дотягивается
        // до элементов интерфейса — поэтому взаимодействиеPossible только с Visible.
        // Ставим режим только если мышь вообще перехватывали (иначе нечего и
        // переключать: окно не наше).
        if (_capturedInput)
            Input.MouseMode = on ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;

        GD.Print($"ship#{Net?.ShipNetId ?? 0}: режим {(on ? "взаимодействия (курсор свободен)" : "пилотирования (мышь захвачена)")}");
    }

    // ==================================================================
    // Швы автотестов шага 2.2 (п.4-п.6)
    // ==================================================================

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
