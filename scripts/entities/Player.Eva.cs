using Godot;

namespace SpaceCorporation;

/// <summary>
/// EVA-режимы локального игрока — eva-plan, Блок C (шаги C1 `Jetpack`,
/// C2 `Boots`, C4 клавиши). Файл — часть `partial class Player`: поля и хуки
/// живут здесь, точки входа вызываются из основного файла (Player.cs).
///
/// ТРИ режима (B1 = «игрок сам выбирает», скафандр носится всегда — ФЛАГ В1):
///   • Seat   — игрок пристёгнут к креслу; EVA-логика этого файла НЕ работает
///              (`StepEva` возвращает false), корабль пилотит по своим кадрам;
///   • Jetpack— свободный 6DoF: тот же `PilotInput` + `FlightModel.Step`, что у
///              корабля, но с параметрами скафандра (8 м/с², 10 м/с, 0.1 рад/с);
///   • Boots  — магнитные ботинки: прилип к нормали поверхности, ходишь в её
///              плоскости, носишься вместе с ней без иерархии (Reparent не нужен).
///
/// ГЛАВНЫЙ ИНВЕРТОР (ФЛАГ В6): в кресле игрок = ребёнок корабля (Reparent),
/// вне кресла — всегда в `_worldRoot` мировой привязкой. Никакого промежуточного
/// состояния: `AttachTo`/`Detach` — единственные места Reparent.
///
/// ФЛАГ В7: `CharacterBody3D.MoveAndSlide` остаётся единственным способом
/// движения: даже прилипание к палубе даёт `Velocity` вдоль нормали + микро-
/// гравитацию к ней — никаких «телепортов по фиксированной длине». Позиционирование
/// ориентации (поворот тела вокруг нормали) — прямая запись `GlobalTransform`,
/// т.к. у `CharacterBody3D` нет кватернионного сеттера (`Rotation` — эйлеры).
///
/// Ключи (C4, InputMap в project.godot):
///   F `detach`       — одна клавиша на два действия (В21): пристёгнут ⇒
///                      отстёжка, свободен ⇒ занять штурвал корабля ПОД КУРСОРОМ.
///                      Читается ОДИН раз в `Player._Input` (detached-развилка
///                      зовёт `OnDetachOrSeatPressed`): у действия один владелец
///                      (В23), а отдельного действия `toggle_seat` в InputMap нет
///                      — F уже занята `detach`, и две акции на одном клавишном
///                      событии стреляли бы дважды за кадр.
///   G `toggle_boots` — boots ⇄ jetpack (В28);
///   N `suit_light`   — налобный фонарь (расход 12 %/мин, Блок E выведет индикатор).
///                      В плане (Б17, В23) была L, но L уже занята рабочим
///                      `hud_labels` (переключает подписи HUD в HudLayer.cs:490) —
///                      молча красть рабочую клавишу нельзя (В23), поэтому фонарь
///                      сел на свободную N.
///
/// Мышь (тот же collector, что у корабля, `PilotInput`): look-канал ПОЗИЦИОННЫЙ
/// (доворот носа к курсору-прицелу), при входе в EVA курсор варпается в центр
/// окна. Перетаскивание ЛКМ не используется: `PilotInput` мыслит позицией
/// курсора (режим пилотирования 2.0), и EVA наследует его байт-в-байт.
///
/// Что осталось на Блок D: поза-реплика для чужих игроков и серверный
/// `SuitState` — локальный скафандр уже считает расходники честно, их просто
/// пока некому транслировать.
/// </summary>
public partial class Player
{
    // ─── режим ────────────────────────────────────────────────────────────────
    private EvaMode _mode = EvaMode.Jetpack;

    /// <summary>Локальный скафандр (расходники, фонарь, ботинки). Удалённые
    /// пирят его по своим путям (Block D), поэтому поле локальное и публичное
    /// только для чтения из этого файла + тестовых швов.</summary>
    public SuitState Suit => _suit;
    private SuitState _suit = SuitState.Fresh();

    /// <summary>Текущий EVA-режим (для HUD, сети и тестов).</summary>
    public EvaMode Mode => _mode;

    // ─── джетпак: модель полёта ───────────────────────────────────────────────
    /// <summary>Шаг физики скафандра. `FlightModel.FixedDt` — эталон 1/60
    /// (тот же, что у корабля), поэтому `dt` в `Step` подаём именно этот
    /// эталон: look-потолок 0.1 рад/с считается по нему (ФЛАГ В23).</summary>
    private const float EvaFixedDt = FlightModel.FixedDt;
    /// <summary>Чувствительность аддитивного пиксельного канала (рад/пиксель) —
    /// та же константа, что у корабля (`Ship.MouseSensitivity`): одинаковый
    /// пиксельный отклик тестовых швов в кресле и в EVA. Позиционный look-канал
    /// при этом меряет окно единой геометрией `PilotAim` (¼ высоты).</summary>
    private const float EvaMouseSensitivity = 0.0025f;
    /// <summary>Длина трассировки «занять кресло под курсором» (В21) — намеренно
    /// велика: клик по кораблю в десяти метрах от игрока — обычная ситуация.</summary>
    private const float SeatRayLength = 30f;

    private PilotInput _evaInput = new();
    private EvaMouseCatcher? _evaCatcher;
    /// <summary>Пилот-ассист джетпака (ФЛАГ В11: константа по умолчанию, T
    /// переключает). В кресле за него отвечает `Ship.Loadout.PilotAssist`.</summary>
    private bool _evaAssist = true;
    /// <summary>Свободный полёт скафандра. Инициализируется ОБЯЗАТЕЛЬНО через
    /// `Park()`: дефолтный `Quaternion` структуры — (0,0,0,0), не идентичность, и
    /// `new Basis(...)` из него даёт нулевую матрицу ⇒ `AffineInverse()` падает, а
    /// трансформ тела становится NaN (баг: игрок спавнился с позой NaN).</summary>
    private FlightState _flight = FlightState.Park();
    private uint _evaTick;
    /// <summary>Угол (рад), приложенный look-каналом за последний шаг — база
    /// вращательного расхода (0.2 %/с на пределе), «довод до центра = 0».</summary>
    private float _lastLookRad;
    /// <summary>Текст причины «клик мимо кресла» — для DebugInfoBus-подсказки,
    /// не чаще одного раза в секунду.</summary>
    private string _lastMissHint = "";
    private float _missHintCooldown;

    // ─── ботинки ──────────────────────────────────────────────────────────────
    private Node3D? _bootsBody;
    private Vector3 _floorNormal = Vector3.Up;
    /// <summary>Точка контакта в ОСЯХ прилипшего тела (не мировая): по ней в
    /// каждом шаге считаем, куда тело уехало, и переносим игрока вместе с ним
    /// (С2 «ключевой шаг»: палуба летящего корабля/станции).</summary>
    private Vector3 _bootsContactPoint;
    /// <summary>Нормаль контакта в ОСЯХ прилипшего тела — по той же причине, что
    /// и точка: при вращении палубы мировая нормаль обязана поехать вместе с ней
    /// (шаг 5.1/Блок C).</summary>
    private Vector3 _bootsContactNormalLocal = Vector3.Up;
    private bool _hasBootsContact;
    private Vector3 _bootsContactWorldLast;
    // Поза прилипшего тела на ПРОШЛОМ шаге — для жёсткого переноса игрока
    // (ΔXform = X_now·X⁻¹_prev; корректен и при сдвиге, и при повороте).
    private Transform3D _bootsBodyLastXform = Transform3D.Identity;
    private float _lastBootsTurnRad;

    /// <summary>Канал поиска опоры под ноги (форма-запрос вокруг капсулы + луч
    /// зазора). По eva-plan строка 178: `BootsMask` = палубы примитивов + корпус
    /// станции; обшивка корабля (1) — не опора, у корабля своя палуба (Блок F),
    /// а собственный корпус цеплял бы ноги изнутри («самолетик» ломался бы о себя).
    /// Слои лучей выбора кресла/панели — `CollisionLayers.EvaRayMask`.</summary>
    private const uint SuitBootsMask = CollisionLayers.BootsMask;

    /// <summary>Веер зондов для `ActivateBoots`: «под ногами» (-Up) плюс
    /// наклонённые вниз диагонали и горизонталь — чтобы «липнуть» не только к
    /// полу, но и к нижней кромке борта шлюза сбоку (В8). Порядок важен лишь
    /// для детерминизма при равных расстояниях; выбор — ближайшая точка.</summary>
    private static readonly Vector3[] BootsProbeDirections =
    {
        Vector3.Down,
        new Vector3( 0.5f, -0.7071f,  0.5f).Normalized(),
        new Vector3( 0.5f, -0.7071f, -0.5f).Normalized(),
        new Vector3(-0.5f, -0.7071f,  0.5f).Normalized(),
        new Vector3(-0.5f, -0.7071f, -0.5f).Normalized(),
        new Vector3( 0.7071f, -0.7071f, 0f),
        new Vector3(-0.7071f, -0.7071f, 0f),
        new Vector3( 0f, -0.7071f,  0.7071f),
        new Vector3( 0f, -0.7071f, -0.7071f),
        Vector3.Left,
        Vector3.Right,
        Vector3.Forward,
        Vector3.Back,
    };

    // ─── тестовые швы (Stage7Test) ────────────────────────────────────────────
    /// <summary>Подменённый кадр ввода следующего шага (null = собрать реально).</summary>
    private InputFrame? _testInput;
    /// <summary>Последний собранный EVA-кадр — для проверки маски/осей (п.7).</summary>
    private InputFrame _lastFrame;

    // ==================================================================
    // Точки входа из Player.cs
    // ==================================================================

    /// <summary>Авторитетна ли ЭТА сторона по расходникам скафандра: сервер или
    /// offline (клиент — нет: у него `HasRealPeer` и не `IsServer`). По плану
    /// расход/заправку считает сервер, клиент рисует реплику (риск 4: иначе
    /// «заправка при ложном пристёгивании» = чит-дыра). Блок D добавит реплику
    /// `SuitState` и канал ввода, после чего эта развилка останется единственной.
    /// </summary>
    private bool IsSuitAuthoritySide()
    {
        var nm = NetworkManager.Instance;
        return nm == null || !nm.HasRealPeer || nm.IsServer;
    }

    /// <summary>После `AttachTo` (любой путь: дебаг-кнопка, F, автостыковка на
    /// спавне, ApplyAttach-реплика): крепление = режим кресла. Вызывается из
    /// `AttachTo` — единственного места Reparent (С3: «Seat без изменений»).</summary>
    private void OnAttachedEvaMode()
    {
        _mode = EvaMode.Seat;
        if (!_isLocal)
            return;
        ReleaseEvaInput();
        DeactivateBoots(keepPosition: true);
        // Б2: мгновенная заправка реактивной массы при креплении к КОРАБЛЮ
        // (станция не заправляет — ФЛАГ В25), дальше батарею заряжает
        // `SuitModel.StepRecharge` (её зовёт ветка сидения в _PhysicsProcess).
        // Гейт по авторитету (риск 4 eva-plan): локальный КЛИЕНТ заправляться не
        // должен, иначе «пристегнулся локально» = бесконечное топливо; у клиента
        // значение приходит репликой (Блок D).
        if (AttachedTo != null && OwnerIsShip(AttachedTo) && IsSuitAuthoritySide())
            SuitModel.ApplyShipAttach(ref _suit);
        SyncFlightFromBody();
    }

    /// <summary>После `Detach`: игрок свободен ⇒ джетпак (B1: выбор режима у
    /// игрока, но «сесть в кресло» и «выйти из него» — разные команды; выход
    /// всегда оставляет самый свободный режим, чтобы игрок не залип в ботинках
    /// без поверхности).</summary>
    private void OnDetachedEvaMode()
    {
        if (!_isLocal)
            return;
        _mode = EvaMode.Jetpack;
        DeactivateBoots(keepPosition: true);
        EnsureEvaInput();
        SyncFlightFromBody();
        // Курсор-прицел в центр окна: доворот стартует «на нуле», без рывка
        // (тот же приём, что посадка в кресло у корабля).
        _evaInput.WarpCursorToCenter(EvaViewportSize);
    }

    /// <summary>Шаг EVA в физическом процессе игрока. Возвращает true, если
    /// режим EVA сам двигает тело — тогда основной `_PhysicsProcess` не должен
    /// ничего добавлять (ни пыли, ни auto-attach, ни обычной ходьбы).</summary>
    private bool StepEva(float dt)
    {
        if (_mode == EvaMode.Seat || !_isLocal)
            return false;

        // Режим без мыши-сборщика (headless-сервер, сбойная сцена) не двигается:
        // иначе молча «плывёт» без ввода.
        Vector2 vp = EvaViewportSize;
        bool ui = GuiFocusOrNull() != null || _evaInput.InteractionMode;

        // Сборщик активен только без UI-фокуса (C4). Кадр собираем в ЛЮБОМ
        // случае: залипших бит быть не должно, а при UI-фокусе кадр пустой —
        // пилот-ассист просто гасит инерцию (та же «жидкость», что у корабля).
        _evaInput.Active = !ui && _evaCatcher != null;
        InputFrame f = _testInput ?? _evaInput.Collect(++_evaTick, vp,
            SuitModel.SuitMaxAngularSpeed, dt, EvaMouseSensitivity);
        _testInput = null;
        _lastFrame = f;

        if (_mode == EvaMode.Jetpack)
        {
            StepJetpack(f, dt);
            return true;
        }

        StepBoots(f, ui, dt);
        return true;
    }

    /// <summary>Сфокусирован ли GUI (линейка/чат/меню): под фокусом EVA-ввод
    /// молчит, иначе WASD бегут и в поле ввода, и в джетпак одновременно.</summary>
    private Node? GuiFocusOrNull()
    {
        Viewport? vp = GetViewport();
        return vp?.GuiGetFocusOwner();
    }

    // ==================================================================
    // C4 — клавиши: F (Seat), G (Boots), L (фонарь), T (ассист)
    // ==================================================================

    /// <summary>Клавиши EVA в физическом процессе игрока (C4). Вызывается из
    /// `Player._Input` через `_isLocal`-гейт; `toggle_seat` уже читает
    /// `_Input` (action-события), здесь — G/L/T. Порядок: `toggle_boots`
    /// раньше обычного — он меняет режим, и следующий блок должен видеть новый.</summary>
    private void HandleEvaKeys()
    {
        if (Input.IsActionJustPressed("toggle_boots"))
            ToggleBoots();

        if (Input.IsActionJustPressed("suit_light"))
            ToggleSuitLight();

        // `toggle_pilot_assist` (T) — у корабля и у джетпака ОДНА клавиша: в
        // кресле пилота её читает Ship (гейт `Ship.IsLocalPilot`, Ship.cs:393),
        // в EVA — мы: пока игрок не пристёгнут, ни один корабль не пилотируется
        // локально и Ship событие не забирает. В Boots ассист нечем гасить
        // (ходьба), поэтому в этом режиме клавиша молчит.
        if (_mode == EvaMode.Jetpack && Input.IsActionJustPressed("toggle_pilot_assist"))
        {
            _evaAssist = !_evaAssist;
            DebugInfoBus.Report($"EVA: пилот-ассист {(_evaAssist ? "включён" : "выключён")} (T)");
        }
    }

    /// <summary>Один обработчик `detach`/`toggle_seat` на два действия (В21):
    /// пристёгнут ⇒ отстёжка (кейс Stage1Test 16), свободен ⇒ занять штурвал
    /// корабля ПОД КУРСОРОМ (raycast от курсора, `EvaRayMask`). Вызывается из
    /// `_Input` при `IsActionJustPressed` — гейт `_isLocal` уже стоит там.</summary>
    private void OnDetachOrSeatPressed()
    {
        if (IsAttached)
        {
            // Тот же сетевой маршрут, что у кнопки Detach (Player.cs
            // `OnButtonDetach`): RPC несёт ТОЛЬКО настоящий клиент; хост и
            // offline выполняют серверную логику напрямую — иначе offline-вызов
            // `RequestDetach` (RPC с CallLocal=false) отрабатывает локально,
            // серверного guarda нет, и отстёжка тихо отклоняется.
            if (NetworkManager.Instance is { IsClient: true })
                RequestDetach();
            else
                DetachServerSide();
            return;
        }

        AttachPoint? seat = SeatPointUnderCursor();
        if (seat == null)
        {
            // Пустой клик НЕ должен глотаться молча: игрок не понимает, почему
            // «F не работает» (UX-замечание C4).
            MissHint($"F: под курсором нет свободного кресла ({_lastMissHint})");
            return;
        }
        AttachToPointRouted(seat);
    }

    /// <summary>Тот же сетевой маршрут, что у кнопки Attach (Player.cs
    /// `OnButtonAttach`), но точка берется не из реестра «ближайшая», а из
    /// EVA-луча под курсором. Чистый `AttachTo` здесь запрещён: он делает
    /// Reparent только локально и не зовёт `SetPilot` — сервер не узнал бы о
    /// занятии штурвала, и у пира осталось бы своё мнение о дереве (§3.5 п.6).</summary>
    private void AttachToPointRouted(AttachPoint seat)
    {
        // NetId точки — экспорт сцены, у кабины Ship.tscn он законно равен 0
        // (AttachPoint.cs: «у кабины корабля — 0», NetTest крепит парой (2,0)).
        // Отказывать надо только при незарегистрированности: OwnerNetId<=0 —
        // владелец без адреса, NetId<0 — невозможный экспорт.
        if (seat.NetId < 0 || seat.OwnerNetId <= 0)
        {
            MissHint("точка не зарегистрирована в реестре — F не сработает");
            return;
        }
        if (NetworkManager.Instance is { IsClient: true })
            RequestAttach(seat.OwnerNetId, seat.NetId);  // клиент: RPC → сервер
        else
            AttachServerSide(seat.OwnerNetId, seat.NetId); // хост/offline: локально
    }

    /// <summary>G: boots ⇄ jetpack (В28). В Boots — всегда выход в Jetpack
    /// (В13=B: скафандр остаётся единственным «реактивным» режимом, ботинки
    /// летать не умеют). В Jetpack — попытка прилипнуть; неудача НЕ меняет
    /// режим, только подсказку.</summary>
    private void ToggleBoots()
    {
        if (_mode == EvaMode.Boots)
        {
            _mode = EvaMode.Jetpack;
            DeactivateBoots(keepPosition: true);
            SyncFlightFromBody();
            MissHint("Ботинки выключены — джетпак");
            return;
        }
        if (_mode != EvaMode.Jetpack)
            return; // в Seat ботинки недоступны (игрок в кресле)

        TryActivateBoots();
    }

    /// <summary>Активировать ботинки и ПЕРЕКЛЮЧИТЬСЯ в режим Boots, если
    /// поверхность найдена. Единая точка перехода «нашёл опору ⇒ в ботинки»:
    /// и реальная G, и тестовые шовы идут через неё — иначе шов включил
    /// контакт, а режим остался Jetpack (расхождениеModes/BootsOn, баг
    ///Block C: `ActivateBoots()` менял только контакт).</summary>
    private bool TryActivateBoots()
    {
        if (!ActivateBoots())
            return false;
        _mode = EvaMode.Boots;
        _suit.BootsOn = true;
        return true;
    }

    /// <summary>L: налобный фонарь (расход 12 %/мин считает SuitModel).
    /// При севшей батарее не включается (StepConsumables уже погасил свет).</summary>
    private void ToggleSuitLight()
    {
        if (!_suit.LightOn && _suit.Battery <= 0f)
        {
            MissHint("Фонарь: батарея села");
            return;
        }
        _suit.LightOn = !_suit.LightOn;
    }

    // ==================================================================
    // C1 — Jetpack
    // ==================================================================

    /// <summary>Шаг джетпака: та же модель полёта, что у корабля, с параметрами
    /// скафандра, и расход по ФАКТУ движения (С1). Порядок кадров — как в
    /// `Ship.Net.StepPrediction`: look-доворот ДО `Step` (иначе расход и
    /// ориентация отстают на кадр, и тесты «довёл до центра = 0» врут).</summary>
    private void StepJetpack(InputFrame f, float dt)
    {
        var p = SuitParams;
        // 6DoF-раскладка скафандра = те же биты, что у корабля (eva-plan В16):
        // линейная тяга — LinearBits (WASD + Space/Ctrl), вращение — AngularBits
        // (Z/X/C/V/Q/E). Своего «LookFree» в маске нет: look-канал живёт в
        // осях кадра (LookYaw/LookPitch), его гейтим отдельно, обнуляя сами оси.
        const uint linBits = InputMask.LinearBits;
        const uint rotBits = InputMask.AngularBits;

        bool dry = _suit.Fuel <= 0f && ((f.Mask & (linBits | rotBits)) != 0u);
        if (dry)
        {
            // Бак пуст ⇒ джетпа нет вообще, включая look-доворот (нос — тоже
            // реактивное сопло). Кадры при этом продолжают собираться и
            // уходить в тик-счётчик — залипших бит нет.
            f.Mask &= ~(linBits | rotBits);
            f.LookYaw = 0f;
            f.LookPitch = 0f;
            MissThrottled("Джетпак: реактивная масса кончилась");
        }

        Vector2 look = FlightModel.ClampLookRadians(
            new Vector2(f.LookYaw, f.LookPitch), p.MaxAngularSpeed, dt);
        _flight.Rotation = ApplyLookLocal(_flight.Rotation, look.X, look.Y);
        _lastLookRad = Mathf.Abs(look.X) + Mathf.Abs(look.Y);

        _flight = FlightModel.Step(_flight, p, f, dt);

        // Тело — единственный исполнительный орган (В7): модель считает
        // свободный полёт, столкновения разберёт MoveAndSlide.
        Velocity = _flight.LinearVelocity;
        GlobalTransform = new Transform3D(new Basis(_flight.Rotation), GlobalPosition);
        ResetPhysicsInterpolation();
        MoveAndSlide();

        SuitModel.StepDrain(ref _suit,
            LinearThrust01(f), _lastLookRad, SuitModel.SuitMaxAngularSpeed, dt);
    }

    /// <summary>Линейная тяга «по факту»: 1, если хоть одна линейная ось
    /// затронута (ТЗ: 1 %/с при манёвре), иначе 0.</summary>
    private static float LinearThrust01(InputFrame f)
    {
        return (f.Mask & InputMask.LinearBits) != 0u ? 1f : 0f;
    }

    /// <summary>Доворот скафандра мышью на готовые радианы — та же формула, что
    /// у корабля (`Ship.PilotCam.cs:170`): рыскание вокруг МИРОВОЙ Y, тангаж
    /// вокруг локальной X уже повёрнутого тела, крен мышь не даёт. Держим свою
    /// копию, а не просим у Ship: игрок в EVA может быть пристёгнут к чужому
    /// кораблю, и лезть в его приватный метод неправильно (Б2).</summary>
    private static Quaternion ApplyLookLocal(Quaternion rot, float yawRad, float pitchRad)
    {
        if (yawRad == 0f && pitchRad == 0f)
            return rot;
        var q = new Basis(Vector3.Up, -yawRad) * new Basis(Vector3.Right, -pitchRad);
        return (new Basis(rot) * q).GetRotationQuaternion().Normalized();
    }

    /// <summary>Параметры скафандра (ФЛАГ В26: единый FlightParams на кресло и
    /// EVA, здесь — цифры скафандра). `PilotAssist = true` по умолчанию — та же
    /// «жидкая» эргономика, что у корабля, T переключает (ФЛАГ В11: константа,
    /// без доводки/стабилизации).</summary>
    private FlightParams SuitParams => new()
    {
        ThrustAccel = _drySuit ? 0f : SuitModel.SuitAccel,
        AngularAccel = Mathf.DegToRad(30f),
        MaxLinearSpeed = SuitModel.SuitMaxLinearSpeed,
        MaxAngularSpeed = SuitModel.SuitMaxAngularSpeed,
        // Следящий режим по времени установки (те же 0.4 с до 95 %, что у
        // кораблей Этапа 6): при look-канале 0.1 рад/с и доводке «нос к
        // курсору» ключевой тест С1 («довёл до центра = расход 0») обязан
        // гасить вращение ДОСРОЧНО — при τ-режиме демпфер в это время не
        // участвует, а при импульсном (AngularRiseTime = 0) момент и
        // демпфер дают перелёт, и расход перестаёт быть нулевым.
        AngularRiseTime = 0.4f,
        LinearDamping = 0.1f,
        PilotAssist = _evaAssist,
        AssistDragAccel = SuitModel.SuitAccel,
    };

    /// <summary>«Сухой» ли джетпак по ресурсу (для нулевой тяги в параметрах —
    /// страховка на случай, если кадр ввода пришёл без проверки `dry`).</summary>
    private bool _drySuit => _suit.Fuel <= 0f;

    /// <summary>Снять состояние полёта с ТЕЛА (после Attach/Detach/выхода из
    /// ботинок): иначе модель унаследует скорость старого режима и игрок
    /// «прыгнет» при смене режима.</summary>
    private void SyncFlightFromBody()
    {
        _flight.LinearVelocity = Velocity;
        _flight.Rotation = GlobalTransform.Basis.GetRotationQuaternion();
        _flight.AngularVelocity = Vector3.Zero;
        _lastLookRad = 0f;
    }

    // ==================================================================
    // C2 — магнитные ботинки
    // ==================================================================

    /// <summary>Попытка прилипнуть к поверхности (В28 → G). Успех = true.
    ///
    /// Геометрия решения (eva-plan §2): из центра капсулы вниз (по −Y тела) и
    /// во все стороны ищем ближайшую точку слоя `SuitBootsMask` в радиусе
    /// `BootsMaxStickDist`; нормаль найденной поверхности — вертикаль нового
    /// режима. Отказ, если: поверхность круче `BootsMaxSlopeDeg` (ФЛАГ: тот же
    /// угол, что у гейта пола, — игрок не должен «прилипать» к борту), либо
    /// между капсулой и поверхностью нет тела (ноги висят в вакууме).
    ///
    /// Ключ инварианта С2: иерархия НЕ меняется (`Reparent` только у Attach/Detach).
    /// «Носиться вместе с палубой» даёт шаг `StepBoots`: контакт хранится в осях
    /// прилипшего тела, и каждый шаг мировая позиция контакта пересчитывается из
    /// его текущей трансформации — куда уехала палуба, туда же приехал игрок.</summary>
    private bool ActivateBoots()
    {
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null)
            return false;

        Vector3 origin = GlobalPosition + Vector3.Up * 0.2f;
        Vector3 hitPos = Vector3.Zero;
        Vector3 hitNormal = Vector3.Zero;
        Node3D? hitBody = null;
        float bestDist = float.MaxValue;

        // Контакт ищем веером ЛУЧЕЙ, а не сферой: нам обязательна НОРМАЛИ
        // поверхности (гейт наклона В13=B), а словарь `IntersectShape` в Godot 4.x
        // её не отдаёт (в нём только collider/shape — позиция и нормаль бывают лишь
        // у `IntersectRay`). Прежний код индексировал col["position"]/col["normal"] и
        // падал KeyNotFoundException при первом реальном касании: тесты ботинок шли
        // через прямой шов `EvaTestStickTo` и баг не ловили, а путь настоящей G
        // (Sw2) его вскрыл. Веер покрывает «под ногами» и «в метре сбоку» (низ
        // шлюза) — берём ближайшую из подходящих точек в радиусе BootsMaxStickDist.
        float reach = SuitModel.BootsMaxStickDist + 0.1f;
        var exclude = new Godot.Collections.Array<Rid> { GetRid() };
        foreach (Vector3 dir in BootsProbeDirections)
        {
            var rq = PhysicsRayQueryParameters3D.Create(
                origin, origin + dir * reach, SuitBootsMask, exclude);
            var res = space.IntersectRay(rq);
            if (res.Count == 0)
                continue;
            if (!res.TryGetValue("collider", out Variant cv)
                || cv.As<GodotObject>() is not Node3D body)
                continue;
            Vector3 point = res["position"].AsVector3();
            Vector3 normal = res["normal"].AsVector3();
            if (normal.LengthSquared() < 0.5f)
                normal = -dir; // вырожденная нормаль — предполагаем вдоль луча
            float dist = point.DistanceTo(GlobalPosition);
            if (dist >= bestDist)
                continue;
            // Гейт наклона: скафандр не прилипает к почти отвесной стене — иначе
            // «ходьба» превращается в соскальзывание по борту (В13=B, общий
            // предикат `SuitModel.CanStepOn` — его же сверяет тест п.24).
            if (!SuitModel.CanStepOn(normal))
                continue;
            bestDist = dist;
            hitPos = point;
            hitNormal = normal;
            hitBody = body;
        }

        if (hitBody == null)
        {
            MissThrottled("Ботинки: рядом нет подходящей поверхности");
            return false;
        }

        _bootsBody = hitBody;
        _floorNormal = hitNormal;
        // Контакт — в осях тела: `ToLocal` от нынешней трансформации тела, а не
        // от моей: дальше палуба может уехать, и именно это смещение нас вернёт.
        _bootsContactPoint = hitBody.ToLocal(hitPos);
        // Нормаль — ТОГДА ЖЕ в осях тела (шаг 5.1/Блок C): world-нормаль каждого
        // шага пересчитывается из неё базисом тела, поэтому вращение палубы
        // (стыковка, вращение станции) уносит и опору — иначе скафандр стоит
        // «прямо» над покатавшейся стеной. Basis ортонормален ⇒ AffineInverse =
        // транспонирование.
        _bootsContactNormalLocal = hitBody.GlobalTransform.AffineInverse().Basis * hitNormal;
        _hasBootsContact = true;
        _bootsContactWorldLast = hitPos;
        _bootsBodyLastXform = hitBody.GlobalTransform;
        _lastBootsTurnRad = 0f;

        // Ориентация: −Y тела вдоль нормали контакта, нос — к ближайшему
        // «горизонтальному» направлению моего прежнего взгляда (игрок не должен
        // внезапно развернуться на 180°).
        AlignBodyToFloor();
        SyncFlightFromBody();
        Velocity = Vector3.Zero;
        DebugInfoBus.Report($"EVA: ботинки включены (наклон " +
            $"{Mathf.RadToDeg(Vector3.Up.AngleTo(hitNormal)):F0}°)");
        return true;
    }

    /// <summary>Выключить ботинки. `keepPosition` — не телепортировать тело
    /// (выход в джетпак: игрок уже стоит там, где его держала палуба).</summary>
    private void DeactivateBoots(bool keepPosition)
    {
        if (!_hasBootsContact && _bootsBody == null)
            return;
        _hasBootsContact = false;
        _bootsBody = null;
        _floorNormal = Vector3.Up;
        _suit.BootsOn = false;
        if (!keepPosition)
            return;
        SyncFlightFromBody();
    }

    /// <summary>Шаг ботинок (С2). Порядок шага:
    ///  1) «перенос» — куда уехало тело, туда же едет игрок (главный пункт C2:
    ///     палуба летящей станции/корабля уносит игрока без иерархии);
    ///  2) ходьба — ввод в плоскости нормали (те же 6DoF-биты, что у джетпака:
    ///     W/S/A/D дают скольжение по плоскости, Q/E — «притянуться/отлипнуть»);
    ///  3) липкость — Velocity вдоль нормали = 0, плюс микро-прижатие к поверхности,
    ///     иначе на выпуклостях капсула «слетает» (ФЛАГ В7: только Velocity,
    ///     никаких фиксированных перестановок).
    /// Ввод под UI-фокусом не читается (C4), а прилипание работает: игрок не
    /// «отваливается» от палубы, пока открыта линейка.</summary>
    private void StepBoots(InputFrame f, bool ui, float dt)
    {
        if (!_hasBootsContact || _bootsBody == null || !GodotObject.IsInstanceValid(_bootsBody))
        {
            // Поверхность исчезла (корабль/станцию удалили из дерева) — режим
            // обязан погаснуть сам, иначе шаг уходит в никуда.
            DeactivateBoots(keepPosition: true);
            _mode = EvaMode.Jetpack;
            SyncFlightFromBody();
            MissThrottled("Ботинки: поверхность исчезла — джетпак");
            return;
        }

        // ── 1. перенос вместе с телом ────────────────────────────────────────
        // Жёсткое перемещение: игрок «прилип» к палубе, значит при ЛЮБОМ
        // изменении её позы (сдвиг И поворот) тело обязано проследовать тем же
        // аффинным преобразованием: pos' = ΔXform · pos, ΔXform = X_now ·
        // X⁻¹_prev. Прежний «перенос точки контакта» (contactNow − contactPrev)
        // при повороте давал неверный рычаг: точка контакта в полуметре от
        // оси вращения уносилась на 25 м/с и тащила игрока за десятки метров.
        Transform3D bodyXform = _bootsBody.GlobalTransform;
        Transform3D delta = bodyXform * _bootsBodyLastXform.AffineInverse();
        Vector3 carry = delta * GlobalPosition - GlobalPosition;
        _bootsBodyLastXform = bodyXform;
        Vector3 contactNow = bodyXform.Origin + bodyXform.Basis * _bootsContactPoint;
        _bootsContactWorldLast = contactNow;
        // Нормаль опоры — из «текущего» базиса тела, а не из прошлого мирового:
        // так вращение палубы (корабль крутится, станция кренится) уносит и опору.
        _floorNormal = bodyXform.Basis * _bootsContactNormalLocal;

        // Липкость ориентации (В7): магнитные ботинки держат тело перпендикулярно
        // опоре — «up» тела = нормаль палубы. При крене/движении носителя игрок
        // обязан довернуться вслед за опорой, иначе «прямо стоит над покатавшейся
        // стеной» (Boot6). Держим горизонтальную составляющую прежнего носа.
        if (GlobalTransform.Basis.Y.Dot(_floorNormal) < 0.999f)
        {
            GlobalTransform = new Transform3D(
                AlignBasisToFloor(GlobalTransform.Basis), GlobalPosition);
            ResetPhysicsInterpolation();
        }

        // ── 2. ходьба в плоскости нормали ────────────────────────────────────
        Vector3 wish = Vector3.Zero;
        if (!ui)
        {
            // Базис «палубы»: нормаль = вертикаль, нос тела = «вперёд».
            Vector3 fwd = -GlobalTransform.Basis.Z;
            fwd = fwd - _floorNormal * fwd.Dot(_floorNormal);
            if (fwd.LengthSquared() < 1e-4f)
                fwd = _bootsBody.GlobalTransform.Basis.Z;
            fwd = fwd.Normalized();
            Vector3 right = fwd.Cross(_floorNormal).Normalized();

            // Клавиши те же, что в джетпаке (В16: единая раскладка 6DoF):
            // W/S — вдоль носa по палубе, A/D — вбок, Space/Ctrl — вдоль нормали
            // (прижаться/отжаться; в ботинках «вверх» = нормаль опоры).
            if ((f.Mask & InputMask.ThrustF) != 0u) wish += fwd;
            if ((f.Mask & InputMask.ThrustB) != 0u) wish -= fwd;
            if ((f.Mask & InputMask.ThrustR) != 0u) wish += right;
            if ((f.Mask & InputMask.ThrustL) != 0u) wish -= right;
            if ((f.Mask & InputMask.ThrustU) != 0u) wish += _floorNormal;
            if ((f.Mask & InputMask.ThrustD) != 0u) wish -= _floorNormal;
        }
        if (wish.LengthSquared() > 1e-6f)
            wish = wish.Normalized() * SuitModel.BootsWalkSpeed;

        // look-доворот: в ботинках нос поворачиваем вокруг нормали палубы —
        // «оглядеться стоя». Расход при этом не считаем: скафандр в ботинках
        // реактивным не является (В13=B), ориентация — кинематика, как у кресла.
        Vector2 look = FlightModel.ClampLookRadians(
            new Vector2(f.LookYaw, f.LookPitch), SuitModel.SuitMaxAngularSpeed, dt);
        _lastBootsTurnRad = Mathf.Abs(look.X) + Mathf.Abs(look.Y);
        if (_lastBootsTurnRad > 0f)
        {
            Quaternion twist = new Quaternion(_floorNormal, look.X + look.Y);
            Basis b = new Basis(twist) * GlobalTransform.Basis;
            b = AlignBasisToFloor(b);
            GlobalTransform = new Transform3D(b, GlobalPosition);
            ResetPhysicsInterpolation();
        }

        // ── 3. липкость: держим зазор + едем по палубе ───────────────────
        // Нормальная компонента — серво к зазору `BootsStickOffset`: раньше
        // прижатие добавлялось вдоль нормали, а следующей строкой СНИМАЛОСЬ
        // как нормальная же компонента цели (цепочка «add → strip» гасила друг
        // друга, капсула висела над палубой, и на наклоне/движении носителя
        // игрок слетал). Теперь нормалью управляет только серво (пропорция от
        // ошибки зазора), а касательной — ходьба + перенос носителя.
        Vector3 carryTan = carry - _floorNormal * carry.Dot(_floorNormal);
        Vector3 tangent = wish + carryTan / Mathf.Max(dt, 1e-5f);
        float gap = SurfaceGap();
        float normalCmd = 0f;
        if (float.IsFinite(gap))
        {
            float err = gap - SuitModel.BootsStickOffset; // >0 — висим выше держимого
            normalCmd = Mathf.Clamp(-err * 10f,
                -SuitModel.BootsMaxStickDist, SuitModel.BootsMaxStickDist);
        }
        Velocity = tangent + _floorNormal * normalCmd;
        MoveAndSlide();
        SyncFlightFromBody();

        // Расходники в ботинках тоже точатся: обогрев/фонарь — энергопотребители
        // и работают при любом режиме (п.1: скафандр живёт всегда). Реактивную
        // массу ботинки НЕ жгут (В13=B: единственный реактивный режим — джетпак),
        // поэтому тяга в формуле — нулевая, Battery тает, Fuel стоит.
        SuitModel.StepDrain(ref _suit, 0f, 0f, SuitModel.SuitMaxAngularSpeed, dt);
    }

    /// <summary>Зазор от низа капсулы до прилипшей поверхности вдоль нормали
    /// (в метрах). `float.PositiveInfinity`, если луч вниз ничего не нашёл —
    /// тогда прижатие не добавляется (иначе игрок «нырял» бы в пустоту).</summary>
    private float SurfaceGap()
    {
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null || !_hasBootsContact)
            return float.PositiveInfinity;

        Vector3 from = GlobalPosition + _floorNormal * 0.2f;
        Vector3 to = GlobalPosition - _floorNormal * (CapsuleHeight * 0.5f + 0.5f);
        var q = PhysicsRayQueryParameters3D.Create(from, to, SuitBootsMask,
            new Godot.Collections.Array<Rid> { GetRid() });
        var hit = space.IntersectRay(q);
        if (hit.Count == 0)
            return float.PositiveInfinity;
        Vector3 p = hit["position"].AsVector3();
        // Зазор мерим от ПОВЕРХНОСТИ капсулы, а не от центра: сравнение с
        // BootsStickOffset (равновесие = капсула в 5 см над палубой) иначе
        // всегда завышено на половину высоты и липкость не держит.
        return from.DistanceTo(p) - 0.2f - CapsuleHeight * 0.5f;
    }

    /// <summary>Поставить тело так, чтобы его −Y совпал с нормалью палубы,
    /// а нос сохранился по возможности (вход в ботинки).</summary>
    private void AlignBodyToFloor()
    {
        Basis b = AlignBasisToFloor(GlobalTransform.Basis);
        GlobalTransform = new Transform3D(b, GlobalPosition);
        ResetPhysicsInterpolation();
    }

    /// <summary>Базис, у которого −Y = нормаль палубы (нос = горизонтальная
    /// компонента прежнего «вперёд», при её вырождении — нос тела-палубы).</summary>
    private Basis AlignBasisToFloor(Basis b)
    {
        Vector3 fwd = -b.Z;
        fwd = fwd - _floorNormal * fwd.Dot(_floorNormal);
        if (fwd.LengthSquared() < 1e-4f)
            fwd = _bootsBody != null ? _bootsBody.GlobalTransform.Basis.Z : Vector3.Forward;
        fwd = fwd.Normalized();
        // Порядок именно такой (fwd × up), иначе базис зеркальный (det = −1) и
        // тело в ботинках отражается зеркалом — «левое стало правым».
        Vector3 right = fwd.Cross(_floorNormal).Normalized();
        return new Basis(right, _floorNormal, -fwd);
    }

    // ==================================================================
    // Seat: расходники скафандра в кресле (Б2) — зарядка и обогрев
    // ==================================================================

    /// <summary>Скафандр в кресле не «замораживается»: батарея заряжается, если
    /// кресло принадлежит КОРАБЛЮ (ФЛАГ В25: станция не заправляет и не заряжает),
    /// обогрев ест энергию всегда. Зовётся из `_PhysicsProcess` на ветке «сидим»
    /// (в `StepEva` мы не участвуем — кресло двигает игрок сам).</summary>
    private void StepSuitInSeat(float dt)
    {
        bool onShip = AttachedTo != null && OwnerIsShip(AttachedTo);
        SuitModel.StepRecharge(ref _suit, onShip, dt);
        SuitModel.StepDrain(ref _suit, 0f, 0f, SuitModel.SuitMaxAngularSpeed, dt);
    }

    /// <summary>Владелец точки крепления — корабль? Идём вверх по предкам:
    /// у станции/палубы `OwnerNetId` тоже ненулевой, поэтому признак именно
    /// типа `Ship`, а не адреса.</summary>
    private static bool OwnerIsShip(AttachPoint point)
    {
        for (Node n = point.GetParent(); n != null; n = n.GetParent())
            if (n is Ship)
                return true;
        return false;
    }

    // ==================================================================
    // Подсказки промаха (C4): «F не работает» не должно быть молча
    // ==================================================================

    /// <summary>Одноразовая подсказка в шину дебага (строка видна в F3-оверлее).</summary>
    private void MissHint(string text)
    {
        _lastMissHint = text;
        _missHintCooldown = 1f;
        DebugInfoBus.Report($"EVA: {text}");
    }

    /// <summary>То же, но не чаще раза в секунду (иначе при пустом баке строка
    /// печатается каждый физический шаг и забивает оверлей).</summary>
    private void MissThrottled(string text)
    {
        _lastMissHint = text;
        if (_missHintCooldown > 0f)
            return;
        _missHintCooldown = 1f;
        DebugInfoBus.Report($"EVA: {text}");
    }

    // ==================================================================
    // Тестовые швы (Block C чек-листа eva-plan, тест Stage7Test п.1–3/7–10).
    // Как `Ship.TestSetInputForTest`: задают/подменяют ввод и условия без
    // клавиатуры и мыши (их в headless-тесте сгенерировать нельзя).
    // ==================================================================

    /// <summary>Задать кадр ввода для следующего шага EVA (`collect == null` —
    /// следующий шаг соберёт ввод сам, как в бою).</summary>
    public void EvaTestSetInput(InputFrame? frame) => _testInput = frame;

    /// <summary>Сбросить внутреннюю модель полёта на текущее состояние тела
    /// (тест: обнуляет `Velocity` снаружи и обязан обнулить и `_flight`, иначе
    /// следующий `StepJetpack` унаследует старую скорость — нарушение условия
    /// «равные параметры» в инварианте детерминизма п.3).</summary>
    public void EvaTestSyncFlightFromBody() => SyncFlightFromBody();

    /// <summary>Одна физическая итерация EVA-шага (тот же вход, что у
    /// `_PhysicsProcess`: шаг с `delta == 0` Godot не двигает тело).</summary>
    public void EvaTestStep() => StepEva(FlightModel.FixedDt);

    /// <summary>Тот же шаг, но с фиксированной длительностью — для чистых
    /// проверок расхода (без разгона физикой).</summary>
    public void EvaTestStep(float dt) => StepEva(dt);

    /// <summary>Пауза/возобновление физического шага стендовым тестом: пока
    /// тест строит сцену или «летит» между фазами, реальный `_PhysicsProcess`
    /// гонял бы EVA-шаг на реальном delta с пустым input-деревом и разгонял
    /// игрока в произвольном направлении (дрейф ломает позиционные проверки).
    /// `EvaTestStep`/`EvaTestSuitSeatStep` работают и на паузе — это те же
    /// `StepEva`/`StepSuitInSeat`, вызванные тестом явно.</summary>
    public void EvaTestSetPhysicsPaused(bool paused) => SetPhysicsProcess(!paused);

    /// <summary>Одна физическая итерация скафандра, стоящего в кресле (Б2).</summary>
    public void EvaTestSuitSeatStep() => StepSuitInSeat(FlightModel.FixedDt);
    public void EvaTestSuitSeatStep(float dt) => StepSuitInSeat(dt);

    /// <summary>Команда «сесть/выйти» (та же, что клавиша F).</summary>
    public void EvaTestDetachOrSeat() => OnDetachOrSeatPressed();

    /// <summary>Симулировать нажатие клавиш (G — ботинки, N — фонарь).</summary>
    public void EvaTestKeyPress(string action)
    {
        switch (action)
        {
            case "toggle_boots":
                // та же реальная G-ветка (Seat-гейт и «неудача не меняет
                // режим» — внутри ToggleBoots)
                ToggleBoots();
                break;
            case "suit_light":
                ToggleSuitLight();
                break;
        }
    }

    /// <summary>Сменить режим напрямую (для стендов: «встал на палубу, потом
    /// отцепился»). Возвращает false, если переход невозможен (кресло).</summary>
    public bool EvaTestSetMode(EvaMode mode)
    {
        if (mode == EvaMode.Seat || !_isLocal)
            return false;
        if (mode == EvaMode.Boots)
            ActivateBoots();
        else
        {
            DeactivateBoots(keepPosition: true);
            _mode = EvaMode.Jetpack;
        }
        return _mode == mode;
    }

    /// <summary>Кресло под «курсором» — стендовый луч (в headless курсора нет).</summary>
    public AttachPoint? EvaTestSeatPointUnderCursor() => SeatPointUnderCursor();

    /// <summary>Последняя причина отказа F/ботинок (текст подсказки).</summary>
    public string EvaTestLastMiss() => _lastMissHint;

    /// <summary>Последний собранный EVA-кадр (п.7: маска/оси корректны).</summary>
    public InputFrame EvaTestLastFrame() => _lastFrame;

    /// <summary>Последний присвоенный номер EVA-кадра (детерминизм: у шага без
    /// входа счётчик всё равно идёт).</summary>
    public uint EvaTestTick() => _evaTick;

    /// <summary>Состояние расходников извне — задать (осторожно: только тест).</summary>
    public void EvaTestSetSuit(SuitState state) => _suit = state;

    /// <summary>Скафандр как он есть (только чтение) — тест смотрит гистерезис
    /// предупреждений и флаги `BootsOn` без правки состояния.</summary>
    public SuitState EvaTestSuit() => _suit;

    /// <summary>Вкл/выкл ботинок без поиска поверхности (п.9/п.15: сам переход
    /// C1↔C2 проверяется без геометрии). false → выход в джетпак с сохранением
    /// позиции.</summary>
    public void EvaTestSetBoots(bool on)
    {
        if (on)
            ActivateBoots();
        else
            DeactivateBoots(keepPosition: true);
    }

    /// <summary>Прилипнуть к конкретной поверхности без физики сцены (п.12:
    /// палуба уезжает, контакт должен ехать следом — нужен КОНКРЕТНЫЙ тестовый
    /// StaticBody3D и его точка контакта, который физически стоит в начале).</summary>
    public void EvaTestStickTo(StaticBody3D body, Vector3 worldPoint, Vector3 worldNormal)
    {
        _bootsBody = body;
        _floorNormal = worldNormal;
        _bootsContactPoint = body.ToLocal(worldPoint);
        _bootsContactNormalLocal = body.GlobalTransform.AffineInverse().Basis * worldNormal;
        _hasBootsContact = true;
        // Шов обязан поступать как реальный `ActivateBoots`: луч к ПОВЕРХНОСТИ
        // тела. Точка теста может быть внутри корпуса (грубый стендовый «прямоугольник»
        // под игроком) — ищем честную точку попадания луча сверху вниз и ставим
        // капсулу в равновесие: половина высоты + прилипательный зазор. Иначе
        // `SurfaceGap` её не видит (∞), липкость не держит, а перенос точки
        // контакта даёт фиктивный снос.
        Vector3 anchor = worldPoint;
        var space = GetWorld3D()?.DirectSpaceState;
        if (space != null)
        {
            var q = PhysicsRayQueryParameters3D.Create(
                worldPoint + worldNormal * 2f, worldPoint - worldNormal * 2f,
                SuitBootsMask, new Godot.Collections.Array<Rid> { GetRid() });
            var hit = space.IntersectRay(q);
            if (hit.Count > 0)
                anchor = hit["position"].AsVector3();
        }
        _bootsContactPoint = _bootsBody.ToLocal(anchor);
        _bootsContactWorldLast = anchor;
        _bootsBodyLastXform = _bootsBody.GlobalTransform;
        GlobalPosition = anchor + worldNormal * (CapsuleHeight * 0.5f + SuitModel.BootsStickOffset);
        ResetPhysicsInterpolation();
        Velocity = Vector3.Zero;
        _lastBootsTurnRad = 0f;
        _suit.BootsOn = true;
        _mode = EvaMode.Boots;
        AlignBodyToFloor();
        SyncFlightFromBody();
        Velocity = Vector3.Zero;
    }

    /// <summary>Актуальная мировая нормаль опоры (у ботинок — из локализованной
    /// нормали тела, пересчитанной на каждом шаге).</summary>
    public Vector3 EvaTestFloorNormal() => _floorNormal;

    /// <summary>Зазор до прилипшей поверхности (диагностика тестов).</summary>
    public float EvaTestGapForTest() => SurfaceGap();

    /// <summary>Тело, к которому прилип (null = контакта нет) — тест п.12/п.14.</summary>
    public Node3D? EvaTestBootsBody() => _bootsBody;

    /// <summary>Посадка в КОНКРЕТНУЮ точку (п.12/п.14: нужен тестовый стул,
    /// а не кресло корабля). Возвращает тот же код отказа, что и F-клавиша.</summary>
    public bool EvaTestSitAt(AttachPoint point)
    {
        if (point.Kind != AttachPoint.KindOf.Seat)
        {
            MissThrottled("Это не кресло");
            return false;
        }
        if (IsSeatTakenBy(point, out bool byMe))
        {
            MissThrottled(byMe ? "Ты уже в кресле" : "Кресло занято");
            return false;
        }
        AttachTo(point, snapToAttachPoint: true);
        return AttachedTo == point;
    }

    // ==================================================================

    /// <summary>Размер окна для look-канала (`PilotAim` мерит ¼ ВЫСОТЫ окна).
    /// `GetViewport().GetVisibleRect().Size`, а не `GetVisibleSize()`: у окна без
    /// растяжимости это то же число, а в тестовой обёртке headless-размер именно
    /// здесь и корректен.</summary>
    private Vector2 EvaViewportSize
    {
        get
        {
            Viewport? vp = GetViewport();
            Vector2 s = vp != null ? vp.GetVisibleRect().Size : Vector2.Zero;
            return s.X > 0f && s.Y > 0f ? s : new Vector2(1152f, 720f);
        }
    }

    /// <summary>Завести сборщик мыши игрока. Тот же приём, что у корабля
    /// (`Ship.ShipMouseCatcher`): у `Player` — `CharacterBody3D`, то есть
    /// `CollisionObject3D`, и его `_input` вьюпорт вызывает только при пикинге
    /// тела (проба `tests/probes/ProbeInputHeadless.tscn`), поэтому мышь собирает
    /// обычный `Node3D`-потомок и передаёт ввод напрямую.</summary>
    private void EnsureEvaInput()
    {
        if (_evaCatcher != null && GodotObject.IsInstanceValid(_evaCatcher))
            return;
        _evaCatcher = new EvaMouseCatcher(this) { Name = "EvaMouseCatcher" };
        AddChild(_evaCatcher);
        _evaInput.Active = true;
    }

    /// <summary>Убрать сборщик (игрок в кресле / узел не локальный): дельты
    /// чужого пира копиться не должны.</summary>
    private void ReleaseEvaInput()
    {
        if (_evaCatcher != null && GodotObject.IsInstanceValid(_evaCatcher))
            _evaCatcher.QueueFree();
        _evaCatcher = null;
        _evaInput.Release();
    }

    /// <summary>Кресло под курсором (В21): луч от камеры через курсор ищет
    /// ближайшую точку крепления на слое `EvaRayMask`... но точки крепления — это
    /// `Area3D`/`StaticBody3D` сцены корабля, и на слой `Station` они не ложатся.
    /// Поэтому луч ищет ТЕЛО-владельца (корпус корабля/станцию), а кресло
    /// берётся из группы `attach_point` по ближнему к лучу кандидату.
    ///
    /// Возвращает null и заполняет `_lastMissHint` причиной, иначе игрок видит
    /// только «F ничего не делает».</summary>
    private AttachPoint? SeatPointUnderCursor()
    {
        Vector2 cursor = _evaInput.HasCursor ? _evaInput.CursorPx : EvaViewportSize * 0.5f;
        Camera3D? cam = GetActiveCamera();
        if (cam == null)
        {
            _lastMissHint = "нет активной камеры";
            return null;
        }
        // Пикирование по пикселю окна даёт мировой луч ровно через курсор.
        var from = cam.ProjectRayOrigin(cursor);
        var to = from + cam.ProjectRayNormal(cursor) * SeatRayLength;

        var free = NearestFreeSeatOnRay(from, to, out string why);
        if (free == null)
        {
            _lastMissHint = why;
            return null;
        }
        return free;
    }

    /// <summary>Ближайшая СВОБОДНАЯ точка-кресло на отрезке луча: перебор группы
    /// `attach_point` + расстояние до луча (пикировать саму точку нельзя — она
    /// `Node3D`, без формы). Свободна = ни один игрок к ней сейчас не пристёгнут
    /// (занятость кресла по сети — Блок D, здесь локальный признак `AttachedTo`).</summary>
    private AttachPoint? NearestFreeSeatOnRay(Vector3 from, Vector3 to, out string why)
    {
        AttachPoint? best = null;
        float bestDist = float.MaxValue;
        int seats = 0, taken = 0, offRay = 0;
        Vector3 dir = to - from;
        float dirLen = dir.Length();
        if (dirLen < 1e-4f)
        {
            why = "нулевой луч курсора";
            return null;
        }
        dir /= dirLen;

        foreach (Node node in GetTree().GetNodesInGroup(AttachPoint.GroupName))
        {
            if (node is not AttachPoint point || !GodotObject.IsInstanceValid(point))
                continue;
            if (point.Kind != AttachPoint.KindOf.Seat)
                continue;
            seats++;
            if (IsSeatTakenBy(point, out bool byMe))
            {
                taken++;
                // «Моё» кресло — не отказ: игрок уже в нём, и повторная F-команда
                // это отстёжка (ветка IsAttached выше), сюда мы не попадаем.
                why = byMe ? "ты уже в этом кресле" : "кресло занято другим игроком";
                continue;
            }
            // Расстояние точки до луча (не до отрезка: кресло в 5 м за камерой —
            // тоже «под курсором», если игрок навёл камеру назад).
            Vector3 v = point.GlobalPosition - from;
            float along = v.Dot(dir);
            if (along < 0f || along > dirLen)
            {
                offRay++;
                continue;
            }
            float perp = (v - dir * along).Length();
            if (perp > SeatPickRadiusPx)
            {
                offRay++;
                continue;
            }
            float score = along; // ближние к игроку кресла выигрывают
            if (score >= bestDist)
                continue;
            bestDist = score;
            best = point;
        }

        if (best != null)
        {
            why = "";
            return best;
        }
        why = seats == 0 ? "в сцене нет кресел" :
              taken > 0 && offRay == 0 ? "все кресла заняты" :
              "ни одно кресло не попало под курсор";
        return null;
    }

    /// <summary>Радиус «попадания по креслу» в метрах (перпендикулярно лучу):
    /// кресло — узел без формы, поэтому попадание меряется расстоянием до луча.
    /// 0.6 м — ширина кресла из Ship.tscn, чтобы промах по креслу был промахом,
    /// а не лотереей.</summary>
    private const float SeatPickRadiusPx = 0.6f;

    /// <summary>Кресло занято? Локальный признак (Блок D добавит сетевой):
    /// игрок пристёгнут именно к этой точке. `byMe` — тем же игроком, что зовёт
    /// проверку (для подсказки «ты уже в кресле»).
    ///
    /// Перебор игроков сцены, а не поля точки: у `AttachPoint` нет обратного
    /// ссылки на сидельца, а Reparent уже отражает факт крепления.</summary>
    private bool IsSeatTakenBy(AttachPoint point, out bool byMe)
    {
        byMe = false;
        // Группа «player» (единственная в проекте — WorldSpawner.Spawn.cs:272), не
        // «players»: опечатка в имени группы превращает проверку занятости в
        // «все кресла свободны» (BUG-регресс В14=B / Stage1Test кейс 16).
        foreach (Node node in GetTree().GetNodesInGroup("player"))
        {
            if (node is not Player p || !GodotObject.IsInstanceValid(p))
                continue;
            if (p.AttachedTo != point)
                continue;
            byMe = p == this;
            return true;
        }
        return false;
    }

    // ==================================================================
    // eva-plan Блок E (Е2): hover панели управления
    // ==================================================================

    /// <summary>Дальность hover-подсказки панели (Д2-B / eva-plan §константы:
    /// 12 м — «не на весь космос»).</summary>
    public const float PanelHoverRangeM = 12f;

    /// <summary>Один собранный hover-хит по панели (п.5/п.6): позиция попадания,
    /// панель, адрес кресла, дистанция, занятость. `Seat` — ссылка на кресло
    /// панели (net-id берём из неё, адрес не дублируем).</summary>
    public struct PanelHover
    {
        public ControlPanel Panel;
        public AttachPoint Seat;
        public Vector3 HitPos;
        public float Dist;
        public bool IsBusy;
    }

    private PanelHover? _panelHover;

    /// <summary>Активен ли сбор hover: EVA-режимы (Jetpack/Boots), свободный
    /// игрок, локальность. В кресле панель своей подсказки не просим (В14=B:
    /// одно кресло — один пилот).</summary>
    private bool PanelHoverGate() =>
        _isLocal && !IsAttached && (_mode == EvaMode.Jetpack || _mode == EvaMode.Boots);

    /// <summary>Собрать hover по лучу (from → from+dir*PanelHoverRangeM).
    /// Возвращает null и причину отказа в `why`, иначе хит. Гейты:
    /// панель обязана быть ПЕРВЫМ попаданием луча на `EvaRayMask` — иначе
    /// подсказка всплывала бы сквозь корпуса/палубы (п.5 «луч реально попал»).</summary>
    private PanelHover? HoverPanelOnRay(Vector3 from, Vector3 dir, out string why)
    {
        ControlPanel? panel = ControlPanelOnRay(from, from + dir * PanelHoverRangeM, out why);
        if (panel == null)
            return null;
        AttachPoint? seat = panel.Seat;
        if (seat == null || seat.NetId < 0 || seat.OwnerNetId <= 0)
        {
            why = "панель без зарегистрированного кресла";
            return null;
        }
        bool busy = IsSeatTakenBy(seat, out _);
        return new PanelHover
        {
            Panel = panel,
            Seat = seat,
            HitPos = from + dir * (from.DistanceTo(panel.GlobalPosition)),
            Dist = from.DistanceTo(panel.GlobalPosition),
            IsBusy = busy,
        };
    }

    /// <summary>Ближайшая по лучу панель (слой `ControlPanel` ПЕРВОМ попаданием
    /// на `EvaRayMask`). Ближайшее попадание спрашиваем у физического пространства
    /// целиком (маска EVA), и требуем, чтобы им оказалась панель: луч, сначала
    /// зацепивший корпус, панель «сквозь корпус» не показывает.</summary>
    private ControlPanel? ControlPanelOnRay(Vector3 from, Vector3 to, out string why)
    {
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null)
        {
            why = "нет физического пространства";
            return null;
        }
        var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.EvaRayMask);
        // Панель — `Area3D` (вложенный в `Ship`-`RigidBody3D` физический узел:
        // `StaticBody3D` под телом-родителем в пространство не регистрируется и
        // луч его не видит). Поэтому луч обязан цеплять и области, а не только тела.
        query.CollideWithAreas = true;
        var hit = space.IntersectRay(query);
        if (hit.Count == 0)
        {
            why = "луч ни во что не попал";
            return null;
        }
        // Ближайшее попадание — не панель ⇒ панель перекрыта (корпус/палуба/станция).
        if (hit["collider"].As<GodotObject>() is not ControlPanel panel
            || (panel.CollisionLayer & CollisionLayers.ControlPanel) == 0u)
        {
            why = "панель перекрыта другим объектом";
            return null;
        }
        why = "";
        return panel;
    }

    /// <summary>Подпись в подсказке: клавиша действия `detach` (F/геймпад) из
    /// InputMap — п.С4: текст обязан меняться при ремапе. Первая клавиша
    /// без модификаторов, иначе «?». `keylabel=` — чтобы тест п.5 сверял.</summary>
    public static string PanelKeyText()
    {
        string key = "?";
        foreach (InputEvent ev in InputMap.ActionGetEvents("detach"))
        {
            if (ev is InputEventKey k && !k.ShiftPressed && !k.CtrlPressed && !k.AltPressed
                && !k.MetaPressed)
            {
                // KeyLabel — Key-enum с учётом раскладки/Shift; строку даёт OS.
                key = OS.GetKeycodeString(k.KeyLabel);
                break;
            }
        }
        return $"keylabel={key}";
    }

    /// <summary>Активация hover-кресла: тот же сетевой маршрут, что у F
    /// (`AttachToPointRouted`) — п.6. Отказ занятых/незарегистрированных точек
    /// обрабатывает сам маршрут (`MissHint` + без SetPilot).</summary>
    private bool ActivateHoverSeat(PanelHover hover)
    {
        if (hover.Seat == null || hover.Panel == null)
            return false;
        // Занятость перечитываем СВЕЖИМ, а не берём из хита: между сбором
        // подсказки и активацией кто-то успел сесть (гонка двух игроков —
        // UX-п.6: подсказка устаревает, а крепление нет).
        if (IsSeatTakenBy(hover.Seat, out bool byMe))
        {
            MissHint(byMe ? "ты уже в этом кресле" : "кресло занято другим игроком");
            return false;
        }
        AttachToPointRouted(hover.Seat);
        return IsAttached;
    }

    /// <summary>Шов п.4/п.5: panel-hover по явному лучу (headless-стенд без мыши).
    /// Возвращает собранный хит или null; причину отказа — `EvaTestLastMiss`.</summary>
    public PanelHover? EvaTestHoverAt(Vector3 from, Vector3 dir)
    {
        if (!PanelHoverGate())
        {
            _lastMissHint = "hover активен только у локального свободного игрока в EVA";
            return null;
        }
        dir = dir.LengthSquared() < 1e-8f ? Vector3.Zero : dir.Normalized();
        PanelHover? h = HoverPanelOnRay(from, dir, out string why);
        if (h == null)
            _lastMissHint = why;
        _panelHover = h;
        return h;
    }

    /// <summary>Актуальный hover-хит (для HUD-виджета и теста п.5: текст
    /// подсказки строится из последнего хита + литеры InputMap).</summary>
    public PanelHover? EvaTestPanelHoverNow() =>
        _panelHover is { } h && GodotObject.IsInstanceValid(h.Seat) &&
        GodotObject.IsInstanceValid(h.Panel) ? h : null;

    /// <summary>Текст подсказки по hover-хиту (п.5/Е2): занятая точка — «кресло
    /// занято», свободная — «<литера> сесть за штурвал» + `keylabel=` (шов
    /// теста: литеры из InputMap). Пустая строка — хита нет.</summary>
    public static string PanelHoverHint(PanelHover hover)
    {
        string key = PanelKeyText(); // "keylabel=F"
        char letter = key.Length > "keylabel=".Length
            ? key["keylabel=".Length] : '?';
        return hover.IsBusy
            ? $"кресло занято ({key})"
            : $"{letter} сесть за штурвал ({key})";
    }

    /// <summary>Текст последней активной подсказки ("" — хита нет): шов теста
    /// п.5 и источник для HUD-виджета (Е2).</summary>
    public string EvaTestPanelHintText() =>
        EvaTestPanelHoverNow() is { } h ? PanelHoverHint(h) : "";

    /// <summary>Шов п.6: «клик по подсказке/клавиша» — активировать кресло хита;
    /// true ⇒ игрок пристёгнут и стал пилотом (проверяет тест).</summary>
    public bool EvaTestActivateHover(PanelHover hover)
    {
        if (!PanelHoverGate())
        {
            MissHint("hover активен только у локального свободного игрока в EVA");
            return false;
        }
        return ActivateHoverSeat(hover);
    }

    // ==================================================================
    // Держатель мыши EVA
    // ==================================================================

    /// <summary>Сборщик мыши локального игрока (тот же приём, что
    /// `Ship.ShipMouseCatcher`): `Player` — `CollisionObject3D`, его `_input`
    /// вьюпорт зовёт только при пикинге тела, поэтому дельту/позицию курсора
    /// собирает этот `Node3D` и отдаёт игроку напрямую.</summary>
    private partial class EvaMouseCatcher : Node3D
    {
        private readonly Player _player;

        public EvaMouseCatcher(Player player) => _player = player;

        public override void _Input(InputEvent @event)
        {
            if (@event is InputEventMouseMotion m)
            {
                _player._evaInput.HandleMouse(m, _player.EvaViewportSize);
                return;
            }
            // RMB в EVA = режим взаимодействия (освободить мышь под UI) — та же
            // клавиша, что у корабля, и то же поле `InteractionMode`: игрок
            // переключает «управляю» ⇄ «пользуюсь интерфейсом» одинаково везде.
            if (@event.IsActionPressed("toggle_interaction"))
            {
                _player._evaInput.InteractionMode = !_player._evaInput.InteractionMode;
                DebugInfoBus.Report($"EVA: режим {(_player._evaInput.InteractionMode
                    ? "взаимодействие (мышь свободна)" : "пилотирование")}");
            }
        }
    }
}
