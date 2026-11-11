using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial Player: локальная визуальная мишура — «космическая пыль» вокруг игрока.
//
// Зачем: при полёте (особенно в chase-камере) фон — далёкие звёзды со скайбокса,
// а скайбокс бесконечный: параллакса нет, и скорость сцены «не читается».
// Поле мелких неподвижных в МИРЕ точек вокруг игрока даёт тот самый эффект:
// корабли прошивает сквозь пыль, точки проносятся назад на 1-2 пикселя —
// направление и скорость видны периферийным зрением.
//
// Ключевое свойство частицы — она живёт в фиксированной точке МИРА и НИКУДА не
// летит сама (скорость 0). Движение относительно пыли создаёт ЭМИТТЕР (игрок),
// а не сама частица — поэтому пыль «размазывается» назад ровно по вектору
// движения и направление/скорость читаются периферией.
//
// ПОЧЕМУ НЕ GpuParticles3D (как было раньше): у GPU-частиц ВЕКТОР ЖИЗНИ задаётся
// при спавне (Lifetime) и НЕ связан с тем, где частица. При фиксированном
// Lifetime=1.8с любая точка умирала ровно через 1.8с — даже если она прямо перед
// носом и должна ещё долго быть видна, и даже если игрок стоит на месте. Плюс
// fade-in/out по времени жизни заставлял точки «мигать» и появляться из ниоткуда.
// Это прямо противоречит физике пыли: dust не мигает и не рождается на глазах.
//
// ЧТО ЗДЕСЬ: ручной CPU-пул слотов поверх MultiMeshInstance3D.
//   • частица занимает постоянную точку мира и существует ПОСТОЯННО, пока не
//     ВЫЛЕТИТ за пределы области генерации (игрок отдалился дальше DustRadius);
//   • в ПОКОЕ дистанции до частиц малы и не растут → слоты не освобождаются →
//     поле статично и не исчезает (dust не мигает);
//   • при ДВИЖЕНИИ точки позади выходят за радиус области → слот освобождается и
//     заполняется НОВОЙ точкой впереди курса (со слабым fade-in) — новые частицы
//     появляются ровно при движении и ровно там, куда летит корабль.
// Гистерезис (радиус спавна < радиуса смерти) гасит дёрганье на границе области.
//
// КООРДИНАТЫ: позиции храним в МИРОВом пространстве (_dustPos — абсолютные точки
// мира), а в MultiMesh пишем ЛОКАЛЬНЫЕ относительно узла трансформы
// (worldToNode * worldXform). Узел — ребёнок игрока и двигается вместе с ним,
// поэтому пересчёт мировой→локальный идёт КАЖДЫЙ кадр: именно так точка
// «прикована» к миру и не едет за кораблём (даёт параллакс).
//
// Видимость: узел создаётся ТОЛЬКО локальному игроку (`_isLocal`, §3.5 п.2 — тот
// же гейт, что у кнопок и камеры): чужим игрокам и dedicated-серверу визуальная
// мишура не нужна, а на сервере поле только жрало бы CPU. При репаренте
// (пристёгнут/отстёгнут) дети едут за узлом автоматически, повторного создания не
// требуется (_Ready не вызывается заново — см. комментарий к _ExitTree в Player.cs).
public partial class Player
{
    /// <summary>Радиус СМЕРТИ частицы: за его пределы частица считается «вылетевшей
    /// из области генерации» и слот переиспользуется. Chase-камера сидит в 12 м за
    /// кормой (Ship.PilotCam), вид «из глаз» — в кабине; радиус 15 м покрывает обе
    /// камеры, чтобы пыль была и перед кораблём, и сбоку (боковые точки дают
    /// ощущение скорости на периферии экрана).</summary>
    public const float DustRadius = 28f;

    /// <summary>Радиус СПАВНА новой точки (< радиуса смерти): гистерезис. Если бы
    /// спавн и смерть были на одном радиусе, частица на границе мигала бы каждый
    /// кадр «смерть↔спавн». Разрыв 28/22 м исключает дёрганье.</summary>
    public const float DustSpawnRadius = 22f;

    /// <summary>Минимальный радиус спавна: не ставим точку прямо в камеру/пиксель
    /// игрока, иначе она «хлопнет» по экрану.</summary>
    private const float DustSpawnMinRadius = 2f;

    /// <summary>Число частиц — «немного»: поле редкое, одиночные точки, без
    /// «метели». Пыли стало меньше (80 вместо 150), но радиус обитания вырос
    /// (22/28 м вместо 12/15) — при том же числе это плотнее лишь у самого
    /// корабля, а вдали поле заметно реже. ~0,006 точки на м³. Пользователь вручную обновил до 10</summary>
    public const int DustAmount = 10;

    // --- След от точки при быстром пролёте (motion streak) ---
    // Пыль — неподвижные точки мира, поэтому на высокой скорости игрок пролетает
    // их и глаз видит не точку, а вытянутую «полосу» вдоль экранныго направления
    // пролёта. Направление и длина определяются РЕАЛЬНЫМ экранным движением точки:
    // это проекция скорости игрока на плоскость, перпендикулярную взгляду
    // (screenVel). Точка, пролетающая точно вдоль линии взгляда, экран почти не
    // пересекает (screenVel≈0) → остаётся точкой, следа нет — физически верно.
    // Вместо авто-билборда ориентацию квада строим сами (см. ComputeStreakFrame):
    // нормаль — на камеру, длинная сторона — вдоль screenVel, короткая (=толщина)
    // — исходный размер точки (чтобы «штрих» был тонким). Длина следа = физическое
    // размытие движения: screenVel × «выдержка» τ, растёт ЛИНЕЙНО со скоростью, т.е.
    // чем быстрее едем — тем длиннее и заметнее штрих (главное ощущение скорости).
    // Ниже порога SpeedMin след выключен (длина = размер → чистая точка).
    //
    // ВАЖНО про τ (выдержку): она должна превышать шаг кадра τ ≫ dt, иначе штрих
    // короче прыжка точки за кадр и след распадается на точечные «пунктиры» —
    // глаз читает мигание вместо движения. При 60 к/с (dt≈0,0167) τ=0,14 даёт
    // штрих ≈ 8× прыжка → сплошная непрерывная линия, направление которой и есть
    // вектор скорости. MaxLen — потолок (чтобы на экстремальных скоростях штрих не
    // уползал через весь экран); при типичных скоростях потолок не достигнут и
    // длина честно пропорциональна скорости.
    // Порог в м/СКОРОСТИ, не в «общем screenVel»: у каждой точки СВОЯ составляющая
    // скорости, перпендикулярная её лучу взгляда (см. ComputeStreak), и она растёт
    // от «впереди по курсу» (≈0) к «сбоку» (≈ вся скорость). Ниже порога — точка.
    public const float DustStreakSpeedMin = 12f;   // ниже — обычная точка
    private const float DustStreakBlurSec = 0.08f;  // эффективная «выдержка» τ, сек
    private const float DustStreakMaxLen = 4.5f;    // потолок длины следа, метры

    /// <summary>Константа сглаживания ИЗМЕРЕННОЙ скорости (сек). Быстрая EMA: за ~τ
    /// секунд догоняет реальную скорость. Держим маленьким (0.1 с) — след обязан
    /// мгновенно реагировать на ручку газа; крупный τ дал бы «вязкий», отстающий от
    /// картинки стрейк. Нужен лишь чтобы погасить покадровый физический джиттер.</summary>
    private const float DustVelSmoothTau = 0.1f;

    /// <summary>Секунды плавного появления НОВОЙ точки (только fade-IN; fade-OUT по
    /// времени — это и было «миганием», его нет). Исчезновение точки происходит на
    /// внешнем крае области через edge-fade по дистанции (см. UpdateDust).</summary>
    private const float DustFadeInSec = 0.7f;

    /// <summary>Базовая альфа точки (полупрозрачная, как и раньше).</summary>
    private const float DustBaseAlpha = 0.55f;

    /// <summary>Насколько новый спавн смещается вперёд по курсу (0..1). При нулевой
    /// скорости направление движения неопределено и смещения нет; в полёте новые
    /// точки рождаются чуть впереди — ровно туда, куда летит корабль. Держим МАЛЫМ
    /// (0.2): радиальный веер следа читается только когда точки есть ПО ВСЕМУ шару,
    /// особенно сбоку (луч ⊥ скорости). Сильный bias (был 0.6) сгребал точки в
    /// «передний конус», где луч ∥ скорости → они всегда точки → штрихов не видно.</summary>
    private const float DustSpawnForwardBias = 0.2f;

    private MultiMeshInstance3D? _dustField;
    private MultiMesh? _dustMesh;

    // Пул слотов (CPU-частицы). Индекс = слот MultiMesh. Мировые координаты
    // хранятся АбСОЛЮТНО (pos — точка мира), в меши пишем локальные относительно
    // узла трансформы (см. комментарий к КООРДИНАТАМ выше).
    private readonly Vector3[] _dustPos = new Vector3[DustAmount];
    private readonly float[] _dustAge = new float[DustAmount];
    private readonly float[] _dustFadeIn = new float[DustAmount];
    private readonly float[] _dustScale = new float[DustAmount];
    private readonly bool[] _dustActive = new bool[DustAmount];
    // Последняя ЗАПИСАННАЯ в MultiMesh альфа слота — для условной перезаписи цвета
    // (SetInstanceColor — межъязыковой вызов, он в горячем цикле стоит дороже самой
    // математики; пока альфа не меняется, цвет не трогаем). См. UpdateDust.
    private readonly float[] _dustAlpha = new float[DustAmount];
    private readonly Queue<int> _dustFree = new();
    private readonly RandomNumberGenerator _dustRng = new();

    /// <summary>Сколько секунд корабль стоит без движения/спавна — для гейта покоя
    /// в UpdateDust. Пока поле двигалось, таймер обнуляется; как только стоим дольше
    /// DustFadeInSec, гарантировано все fade-in добраны, дальше поле статично и
    /// переписывать MultiMesh незачем (см. гейт в начале UpdateDust).</summary>
    private float _dustRestTimer;

    /// <summary>Трансформа узла DustField в ПРОШЛОМ кадре UpdateDust — для гейта
    /// покоя: если узел не сдвинулся и не повернулся относительно мира, world→node
    /// тот же → локальные трансформы точек те же → переписывать MultiMesh не нужно.
    /// Инициализируется identity; первый кадр всё равно перепишет поле
    /// (InitDustField/WriteAllDust задали реальное состояние, таймер = 0).</summary>
    private Transform3D _lastNodeXform = Transform3D.Identity;

    /// <summary>МИРОВАЯ позиция игрока в ПРОШЛОМ кадре UpdateDust — для измерения
    /// реальной скорости. КРИТИЧНО: во время полёта Player — CharacterBody3D,
    /// репейрентнутый ребёнком к креслу корабля (RigidBody3D). Godot НЕ обновляет
    /// свойство Velocity у такого прикреплённого тела (оно обнуляется при
    /// Attach/Detach и больше никогда не пишется). Поэтому vel = Velocity в полёте
    /// ≈ 0 и след никогда не появлялся (ровно жалоба «точки не вытянуты на
    /// скорости»). Правильный источник истины — производная МИРОВОЙ позиции по
    /// кадру: она верна при любом способе движения (тяги, репейрент, кинематика).</summary>
    private Vector3 _lastGlobalPos;

    /// <summary>Измеренная мировая скорость (м/с), сглаженная экспоненциально.
    /// Именно она кормит ComputeStreakFor / FadeInTimeFor — НЕ CharacterBody.Velocity.
    /// Инициализируется нулём: первый кадр замера даст сырую дельту (позиция ещё не
    /// менялась) → 0, дальше копится настоящее движение.</summary>
    private Vector3 _dustVel;

    /// <summary>Замер скорости уже выполнен (первый кадр задаёт базовую позицию без
    /// дельты, иначе на старте GlobalPosition−_lastGlobalPos = весь путь спавна).</summary>
    private bool _dustVelPrimed;

    /// <summary>Потолок «реалистичного» прироста позиции за кадр, отделяющий честное
    /// движение корабля (≤ MaxLinearSpeed·dt ≈ 20 м при 1200 м/с и 60 Гц) от рывков
    /// типа телепорта/респауна. Кадровая дельта больше этого порога — это НЕ поток
    /// воздуха сквозь поле: мгновенный перенос не создаёт ощущения полёта (точки
    /// вокруг телепортированного игрока надо просто перевыпустить, без следа).
    /// Такой кадр не обновляет _dustVel (сохраняем предыдущее сглаженное значение,
    /// обычно ~0 после простоя).</summary>
    private const float DustVelTeleportCap = 40f;

    /// <summary>Собрать поле пыли (вызывается из _Ready, только для _isLocal).</summary>
    private void BuildDustField()
    {
        _dustRng.Randomize();

        var quad = new QuadMesh { Size = new Vector2(1f, 1f) };
        // Квад-спрайт с билбордом: всегда повёрнут к камере (BillboardKeepScale —
        // иначе биллбординг съедает масштаб частицы), unshaded (не зависит от света
        // сцены — звёздная пыль сама по себе точка), аддитивный бленд (белая точка
        // поверх тёмного космоса = свечение, без чёрных рамок у текстуры).
        var tex = GD.Load<Texture2D>("res://assets/vfx/dust_dot.png");
        if (tex == null)
        {
            GD.PushWarning("Player: текстура res://assets/vfx/dust_dot.png не найдена — поле пыли не создано");
            return;
        }
        quad.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoTexture = tex,
            // Биллборд ВЫКЛЮЧЕН: ориентацию квада строим сами в SetDust — нормаль
            // на камеру + длинная сторона вдоль экранныго направления пролёта
            // (след). Авто-биллборд на это не способен: он всегда разворачивает
            // квад «плоско к камере» без возможности растянуть по нужной оси.
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        // MultiMesh с ПОЦВЕТОЧНЫМ цветом (UseColors) — альфа fade-in/edge-fade
        // пишется в цвет экземпляра; формат трансформ — Transform3D.
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = DustAmount,
        };
        mm.Mesh = quad; // ← mesh привязывается к MultiMesh-ресурсу, не к инстансу

        var field = new MultiMeshInstance3D
        {
            Name = "DustField",
            Multimesh = mm,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        // AABB видимости задаём вручную с запасом: частицы всегда в пределах
        // DustRadius вокруг узла (дальше — переиспользование), плюс запас на след,
        // растянутый вдоль скорости. Берём ±34 м.
            field.CustomAabb = new Aabb(new Vector3(-34, -34, -34), new Vector3(68, 68, 68));

            AddChild(field);
        _dustField = field;
        _dustMesh = mm;

        InitDustField();
    }

    /// <summary>Заполнить всё поле точками при старте: поле видно сразу (dust уже
    /// «висит» вокруг корабля), а не наполняется постепенно на глазах. Все слоты
    /// считаются прожившими fade-in (age = DustFadeInSec), поэтому при старте нет
    /// общего «проявления из ниоткуда». Позиции — равномерные в шаре вокруг игрока.</summary>
    private void InitDustField()
    {
        _dustFree.Clear();
        Vector3 p = GlobalPosition;
        for (int i = 0; i < DustAmount; i++)
        {
            _dustPos[i] = RandomInShell(p, Vector3.Zero, DustSpawnRadius, DustSpawnMinRadius);
            _dustScale[i] = _dustRng.RandfRange(0.02f, 0.035f);
            _dustAge[i] = DustFadeInSec; // уже полностью видима
            _dustFadeIn[i] = DustFadeInSec;
            _dustActive[i] = true;
        }
        WriteAllDust();
    }

    /// <summary>Тестовый доступ к CPU-источнику истины поля пыли: заполняет
    /// outWorld[i] МИРОВЫМИ позициями частиц и возвращает число активных слотов.
    /// Читает собственные массивы пула (_dustPos/_dustActive/_dustScale), а НЕ
    /// multiMesh.GetInstanceTransform: в headless-режиме dummy RenderingServer не
    /// хранит per-instance данные MultiMesh, поэтому GPU-readback там всегда
    /// возвращает identity. Позиции в _dustPos — абсолютные точки мира, это и
    /// есть источник истины, из которого каждый кадр пересчитываются трансформы.</summary>
    public int DebugGetDustWorld(Vector3[] outWorld)
    {
        int active = 0;
        for (int i = 0; i < DustAmount; i++)
        {
            outWorld[i] = _dustPos[i];
            if (_dustActive[i] && _dustScale[i] > 0.005f)
                active++;
        }
        return active;
    }

    /// <summary>Тестовый доступ к РАДИАЛЬНОМУ следу: возвращает для каждой активной
    /// частицы прирост длины следа (Extend, м) вдоль её личного экранныго пролёта,
    /// рассчитанный при ИЗМЕРЕННОЙ скорости игрока (_dustVel, та же переменная, что
    /// читает живой UpdateDust) и текущей камере — ровно той же функцией
    /// (ComputeStreakFor), что и живая отрисовка. outPerp[i] — величина компоненты
    /// скорости, перпендикулярной лучу этой точки (мировой screenVel-модуль): она
    /// РАСТЁТ от «впереди по курсу» (≈0) к «сбоку» (≈ вся скорость). Проба проверяет
    /// корреляцию Extend↔perp: боковые точки вытянуты, продольные — точки.</summary>
    public int DebugGetDustStreaks(float[] outExtend, float[] outPerp)
    {
        int active = 0;
        Vector3 vel = _dustVel;   // измеренная мировая скорость (см. _dustVel)
        Camera3D? cam = GetViewport()?.GetCamera3D();
        Vector3 camPos = cam != null ? cam.GlobalPosition : GlobalPosition;
        for (int i = 0; i < DustAmount; i++)
        {
            if (!_dustActive[i] || _dustScale[i] <= 0.005f)
            {
                outExtend[i] = 0f;
                outPerp[i] = 0f;
                continue;
            }
            active++;
            // perp — та же величина, что движет следом (компонента ⊥ личного луча).
            Vector3 r = _dustPos[i] - camPos;
            r = r.LengthSquared() > 1e-6f ? r.Normalized() : -Vector3.Forward;
            Vector3 sv = vel - r * vel.Dot(r);
            outPerp[i] = sv.Length();
            StreakFrame sk = ComputeStreakFor(vel, camPos, _dustPos[i]);
            outExtend[i] = sk.Extend;
        }
        return active;
    }

    /// <summary>Тестовый доступ: ИЗМЕРЕННАЯ мировая скорость (ту же переменную
    /// читает живой след). Проба двигает игрока маленькими шагами по кадрам и
    /// проверяет, что MeasureDustVelocity действительно восстанавливает скорость из
    /// разницы позиций — т.е. что в РЕАЛЬНОМ пути (не выставленная вручную
    /// Velocity) есть чем растягивать точки.</summary>
    public Vector3 DebugGetMeasuredVelocity() => _dustVel;

    /// <summary>Кадровый апдейт поля пыли (вызывается из _PhysicsProcess). Логика
    /// жизни: частица живёт, пока дистанция до игрока ≤ DustRadius; за границей —
    /// слот освобождается и в ЭТОМ ЖЕ кадре заполняется новой точкой впереди курса.
    /// Поэтому новые частицы появляются только при движении, а в покое поле стоит
    /// на месте и не исчезает.</summary>
    private void UpdateDust(float dt)
    {
        if (_dustMesh == null || _dustField == null)
            return;

        // Сперва — ИЗМЕРИТЬ реальную мировую скорость игрока (до гейта покоя и до
        // всего остального): именно она питает след и fade-in. См. MeasureDustVelocity.
        MeasureDustVelocity(dt);

        // ── ГЕЙТ ПОКОЯ (микро-опт №1) ────────────────────────────────────────
        // Пока корабль летит, поле перестраивается каждый кадр: точки миростатичны,
        // а узел едет/поворачивается за игроком → их локальные трансформы меняются,
        // их надо перетранслитить (~160 межъязыковых SetInstanceTransform/Color в
        // пустую, если ничего не поменялось). Гейт сравнивает МИРОВУЮ ТРАНСФОРМУ
        // узла (позиция + ориентация) с прошлым кадром записи: если узел и не
        // сдвинулся, и не повернулся (стоят на месте / доковались / вращаются? —
        // поворот МЕНЯЕТ трансформу, поэтому вращение на месте тоже попадает в
        // «движение» и поле честно разворачивается) и нет свободных слотов под
        // спавн, → world→node тот же, что в прошлый кадр записи → локальные
        // трансформы и альфы точек НЕ меняются → переписывать MultiMesh незачем.
        // Держим открытым ещё DustFadeInSec после последнего движения, чтобы добрать
        // fade-in свежезаспавленных точек (они появляются только при движении, а
        // таймер тогда = 0 → гейт открыт). В покое после добора стоимость кадра ≈ 0,
        // а это бо́льшая часть игрового времени.
        Transform3D nodeXf = _dustField.GlobalTransform;
        bool nodeStatic = nodeXf == _lastNodeXform && _dustFree.Count == 0;
        if (nodeStatic)
        {
            _dustRestTimer += dt;
            if (_dustRestTimer >= DustFadeInSec)
                return;   // узел и поле статичны относительно мира: ничего не изменилось
        }
        else
        {
            _dustRestTimer = 0f;
            _lastNodeXform = nodeXf;
        }

        MultiMesh mm = _dustMesh;
        Vector3 p = GlobalPosition;
        // Скорость — ИЗМЕРЕННАЯ мировая (Position delta), не CharacterBody.Velocity:
        // во время полёта Player прикреплён к кораблю и его Velocity не поддерживается
        // движком (см. поле _dustVel). Именно поэтому след раньше не появлялся.
        Vector3 vel = _dustVel;
        Vector3 forward = vel.LengthSquared() > 0.25f ? vel.Normalized() : Vector3.Zero;

        // Узел двигается вместе с игроком — пересчёт мир→узел один раз за кадр,
        // дальше каждая мировая точка перемножается им в локальную трансформу.
        Transform3D worldToNode = nodeXf.AffineInverse();

        // Камера — источник направления «взгляд-лучей». След от пылинки считается
        // ПО КАЖДОЙ точке (см. ComputeStreak): у неё свой луч от камеры до точки,
        // и «размазывает» её компонент скорости игрока, перпендикулярный ИМЕННО
        // этому лучу. Поэтому след РАДИАЛЬНЫЙ: точка впереди по курсу (луч ∥
        // скорости) почти не едет по экрану → остаётся точкой, точка сбоку (луч ⊥
        // скорости) летит через экран быстро → длинный штрих. Классическая
        // «гиперпространственная» картина. Единный мировой фрейм на всё поле это
        // физически НЕ может описать — поэтому теперь фрейм на точку.
        Camera3D? cam = GetViewport()?.GetCamera3D();
        Vector3 camPos = cam != null ? cam.GlobalPosition : p;

        float edgeBand = DustRadius * 0.2f; // край области, где начинается edge-fade
        float r2 = DustRadius * DustRadius;
        float edgeR = DustRadius - edgeBand;
        float edgeR2 = edgeR * edgeR;

        for (int i = 0; i < DustAmount; i++)
        {
            if (!_dustActive[i])
            {
                HideDust(mm, i);
                continue;
            }

            _dustAge[i] += dt;

            // ── МИКРО-ОПТ №2: квадрат дистанции вместо sqrt ────────────────
            // DistanceTo (sqrt) на 80 точек в кадре — лишняя работа: sqrt нужен
            // ТОЛЬКО у точек у самой границы (для edge-fade). У внутренних —
            // сравним квадрат дистанции с порогами напрямую.
            float d2 = _dustPos[i].DistanceSquaredTo(p);

            // Вылетела за область генерации → освобождаем слот (новый появится ниже).
            if (d2 > r2)
            {
                _dustActive[i] = false;
                _dustFree.Enqueue(i);
                HideDust(mm, i);
                continue;
            }

            float alpha = DustBaseAlpha * Mathf.Min(1f, _dustAge[i] / _dustFadeIn[i]);
            // Мягкое гаснение у внешнего края: точка «истончается» к границе области
            // и исчезает уже невидимой (dust не «хлопает» на границе экрана).
            // sqrt берём только здесь, у точек в краевой полосе (их меньшинство).
            if (d2 > edgeR2)
            {
                float d = Mathf.Sqrt(d2);
                alpha *= Mathf.Clamp((DustRadius - d) / edgeBand, 0f, 1f);
            }

            // Кадр следа СВОЙ у каждой точки: зависить от её луча от камеры.
            StreakFrame sk = ComputeStreakFor(vel, camPos, _dustPos[i]);
            SetDust(mm, worldToNode, i, _dustPos[i], _dustScale[i], alpha, sk);
        }

        // Новое рождение — только если были освобождённые слоты (движение). В покое
        // ни один слот не освобождён → цикл не выполняется → поле не меняется.
        while (_dustFree.Count > 0)
        {
            int i = _dustFree.Dequeue();
            _dustPos[i] = RandomInShell(p, forward, DustSpawnRadius, DustSpawnMinRadius);
            _dustScale[i] = _dustRng.RandfRange(0.02f, 0.035f);
            _dustAge[i] = 0f;
            _dustFadeIn[i] = FadeInTimeFor(_dustPos[i]);
            _dustActive[i] = true;
            StreakFrame sk = ComputeStreakFor(vel, camPos, _dustPos[i]);
            SetDust(mm, worldToNode, i, _dustPos[i], _dustScale[i], 0f, sk);
        }
    }

    /// <summary>СКОРОСТНОЙ FADE-IN (фикс «мигания на высокой скорости»). У точки
    /// здесь ЕСТЬ срок жизни: рождается в спавн-оболочке (≤22 м) и умирает на
    /// радиусе 28 м → живёт ≈ (28 − стартовая_дист) / скорость_игрока. На быстром
    /// ходу этот срок крохотный (6 м / 100 м/с = 0,06 с ≈ 3–4 кадра), а
    /// фиксированный fade-in 0,7 с за такую жизнь не дотягивает яркость и до 10% →
    /// точка появляется тусклым «мигающим» пятнышком и гаснет — ощущение движения
    /// теряется (ровно жалоба). РЕШЕНИЕ: время fade-in = min(DustFadeInSec,
    /// остаток жизни × 2). На медленном ходу остаток велик → fade = 0,7 с
    /// (поведение НЕ меняется); на быстром — ужимается, и точка добирает полную
    /// яркость за 1–2 кадра, сразу становясь ярким ориентированным следом.
    /// Считается ОДИН раз на спавн (скорость и стартовая дистанция известны).</summary>
    /// <summary>Измерить РЕАЛЬНУЮ мировую скорость игрока через разность мировой
    /// позиции между кадрами и загладить экспоненциально (EMA, константа
    /// DustVelSmoothTau). Заменяет собой CharacterBody.Velocity, который во время
    /// полёта не поддерживается движком (Player прикреплён к кораблю — см. _dustVel).
    /// Ключ к починке «точки не штрихуются на скорости»: без этого vel в
    /// ComputeStreakFor был ≈0, Extend всегда 0, все точки оставались квадратами.
    ///
    /// Защита от телепорта: дельта позиции за кадр, превышающая DustVelTeleportCap,
    /// — это мгновенный перенос (респаун/тестовый телепорт), а не поток воздуха
    /// сквозь поле; он не должен рождать гигантский след, поэтому в такой кадр
    /// _dustVel НЕ обновляем (остаётся прежнее сглаженное значение — обычно ~0).
    /// Сырое значение (raw = delta/dt) EMA не ограничивает сверху, чтобы быстрый,
    /// но настоящий разгон честно выходил на большую скорость за ~τ секунд.</summary>
    private void MeasureDustVelocity(float dt)
    {
        Vector3 now = GlobalPosition;

        // Первый замер: запоминаем базу без дельты (позиция спавна), иначе первая
        // же дельта «from zero/стартовый путь» дала бы ложную скорость.
        if (!_dustVelPrimed)
        {
            _dustVelPrimed = true;
            _lastGlobalPos = now;
            _dustVel = Vector3.Zero;
            return;
        }

        Vector3 delta = now - _lastGlobalPos;
        _lastGlobalPos = now;

        if (dt <= 0f)
            return;

        // Телепорт/рывок: огромная мгновенная дельта — след не рождаем, скорость
        // не пересчитываем (сохраняем прежнее сглаженное значение).
        if (delta.Length() > DustVelTeleportCap)
            return;

        Vector3 raw = delta / dt;

        // EMA-сглаживание: приращение за кадр = raw·(1−e^(−dt/τ)).τ ≈ dt. При
        // стабильной скорости за ~τ секунд выходим на неё; рывки отдельного кадра
        // (физический джиттер) сглаживаются. τ маленький (0.1 с) — след обязан
        // реагировать на ускорение быстро, иначе отстаёт от картинки.
        float a = 1f - Mathf.Exp(-dt / DustVelSmoothTau);
        _dustVel = _dustVel.Lerp(raw, a);
    }

    private float FadeInTimeFor(Vector3 worldPos)
    {
        float spd = _dustVel.Length();   // измеренная мировая скорость (см. _dustVel)
        if (spd < 0.5f)
            return DustFadeInSec;
        float lifeLeft = (DustRadius - GlobalPosition.DistanceTo(worldPos)) / spd;
        if (lifeLeft < 0f) lifeLeft = 0f;
        float fadeIn = lifeLeft * 2f;
        return fadeIn < DustFadeInSec ? fadeIn : DustFadeInSec;
    }

    /// <summary>Записать все активные слоты (первичная отрисовка при старте).</summary>
    private void WriteAllDust()
    {
        if (_dustMesh == null || _dustField == null)
            return;
        MultiMesh mm = _dustMesh;
        Vector3 p = GlobalPosition;
        Transform3D worldToNode = _dustField.GlobalTransform.AffineInverse();
        // Первичная отрисовка — игрок в покое (скорость ~0) → у всех Extend=0
        // (чистая точка-квад), но квад каждой точки повёрнут нормалью ПО ЕЁ лучу
        // от камеры (face-on). Скорость нулевая → ComputeStreakFor даёт чистую точку.
        Camera3D? cam = GetViewport()?.GetCamera3D();
        Vector3 camPos = cam != null ? cam.GlobalPosition : p;
        float edgeBand = DustRadius * 0.2f;
        for (int i = 0; i < DustAmount; i++)
        {
            if (!_dustActive[i]) { HideDust(mm, i); continue; }
            float d = _dustPos[i].DistanceTo(p);
            float alpha = DustBaseAlpha;
            if (d > DustRadius - edgeBand)
                alpha *= Mathf.Clamp((DustRadius - d) / edgeBand, 0f, 1f);
            StreakFrame sk = ComputeStreakFor(Vector3.Zero, camPos, _dustPos[i]);
            SetDust(mm, worldToNode, i, _dustPos[i], _dustScale[i], alpha, sk);
        }
    }

    /// <summary>Кадр ориентации следа (в МИРОВЫХ осях), посчитанный один раз за
    /// кадр для всех точек: U — длинная сторона (вдоль экранныго пролёта), V —
    /// короткая (толщина), N — нормаль квада (вдоль взгляда). Len — полная длина
    /// квада вдоль U в метрах (= размер точки, когда следа нет).</summary>
    private struct StreakFrame
    {
        public Vector3 U;   // длинная сторона (мир), единичная
        public Vector3 V;   // короткая сторона (мир), единичная
        public Vector3 N;   // нормаль (вдоль взгляда), единичная
        public float Extend; // прирост длины вдоль U сверх размера, м (0 = нет следа)
    }

    /// <summary>Кадр следа ОТДЕЛЬНОЙ точки (в МИРОВЫХ осях). Учтён ЛИЧНЫЙ луч
    /// каждой точки от камеры: экранное движение точки = компонент скорости
    /// игрока, ПЕРПЕНДИКУЛЯРНЫЙ её лучу взгляда. Точка впереди по курсу (луч ∥
    /// скорости) даёт почти нулевой перпендикуляр → остаётся точкой; точка сбоку
    /// (луч ⊥ скорости) даёт весь вектор → длинный штрих. Это и есть радиальный
    /// «гиперпространственный» веер: раньше общий фрейм (проекция на ⊥ взгляду)
    /// для летящей вперёд камеры давал screenVel≈0 ВСЕМ точкам → сплошные точки.</summary>
    private StreakFrame ComputeStreakFor(Vector3 vel, Vector3 camPos, Vector3 worldPos)
    {
        var f = new StreakFrame { Extend = 0f };

        // Личный луч камера→точка (единичный). Совпала с камерой — берём направление
        // назад по скорости (всё равно alpha у такой точки ~0).
        Vector3 r = worldPos - camPos;
        r = r.LengthSquared() > 1e-6f ? r.Normalized()
            : (vel.LengthSquared() > 1e-6f ? -vel.Normalized() : -Vector3.Forward);

        // Экранное движение = компонент скорости, перпендикулярный лучу этой точки.
        Vector3 sv = vel - r * vel.Dot(r);
        float sLen = sv.Length();

        if (sLen < 0.01f || sLen < DustStreakSpeedMin)
        {
            // След выключен: квад нормалью ПО ЛУЧУ (face-on к камере) = чистая точка.
            f.N = r;
            f.U = PerpOf(r);
            f.V = r.Cross(f.U);
            f.Extend = 0f;
            return f;
        }

        f.U = sv / sLen;    // длинная сторона — вдоль экранныго пролёта точки
        f.V = r.Cross(f.U); // толщина — в экранной плоскости, ⊥ пролёта и луча
        f.N = r;            // нормаль вдоль луча → квад повёрнут «лицом» к камере
        // Размытие движения: метры смаза за выдержку τ, с потолком MaxLen.
        f.Extend = Mathf.Min(sLen * DustStreakBlurSec, DustStreakMaxLen);
        return f;
    }

    /// <summary>Единичный вектор, перпендикулярный dir (ориентация стороны «точки»
    /// без следа; сам выбор безразличен, т.к. квад квадратный и повёрнут N=dir).</summary>
    private static Vector3 PerpOf(Vector3 dir)
    {
        Vector3 a = Mathf.Abs(dir.Dot(Vector3.Up)) < 0.9f ? Vector3.Up : Vector3.Right;
        return dir.Cross(a).Normalized();
    }

    /// <summary>Записать слот: трансформа (всегда — при движении узла локальная
    /// позиция/ориентация точки меняется) + цвет (МИКРО-ОПТ №3: межъязыковой
    /// SetInstanceColor стоит дороже самой математики альфы, поэтому пишем цвет
    /// ТОЛЬКО если альфа изменилась с прошлого кадра; при равномерном полёте
    /// альфа большинства точек постоянна → экономим ~большую часть вызовов).</summary>
    private void SetDust(MultiMesh mm, Transform3D worldToNode, int i, Vector3 pos, float scale, float alpha, StreakFrame sk)
    {
        // Квад (QuadMesh 1×1, центрирован) масштабируется столбцами базиса:
        // столбец длиной L → сторона длиной L метров. При Extend=0 обе стороны =
        // scale → квадрат, повёрнутый нормалью к камере = чистая точка. При
        // Extend>0 сторона вдоль U = scale+Extend → вытянутый «штрих» вдоль
        // экранныго пролёта, толщина (V, N) остаётся = scale.
        float len = scale + sk.Extend;
        var b = new Basis(sk.U * len, sk.V * scale, sk.N * scale);
        var worldXform = new Transform3D(b, pos);
        mm.SetInstanceTransform(i, worldToNode * worldXform);

        // Условная запись цвета: сравнение float дешевле межъязыкового вызова.
        if (_dustAlpha[i] != alpha)
        {
            _dustAlpha[i] = alpha;
            mm.SetInstanceColor(i, new Color(1f, 1f, 1f, alpha));
        }
    }

    private void HideDust(MultiMesh mm, int i)
    {
        // Скрытый слот: нулевой масштаб (квад схлопнут) и нулевая альфа — не рисуется.
        // Трансформу пишем всегда (она должна обнулиться), цвет — только если
        // слот раньше не был скрыт (иначе повторно гасим тот же нуль).
        mm.SetInstanceTransform(i, new Transform3D(Basis.FromScale(Vector3.Zero), Vector3.Zero));
        if (_dustAlpha[i] != 0f)
        {
            _dustAlpha[i] = 0f;
            mm.SetInstanceColor(i, new Color(1f, 1f, 1f, 0f));
        }
    }

    /// <summary>Равномерное направление на сфере (две равномерные величины).</summary>
    private Vector3 RandomDir()
    {
        float z = _dustRng.Randf() * 2f - 1f;      // cos(полярный) — равномерно по cos
        float phi = _dustRng.Randf() * Mathf.Tau;
        float s = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
        return new Vector3(s * Mathf.Cos(phi), s * Mathf.Sin(phi), z);
    }

    /// <summary>Равномерная точка в шаровой оболочке вокруг playerPos: радиус в
    /// [rMin, rMax] (равномерно по объёму — через корень из rand), направление —
    /// равномерное на сфере, но со смещением вдоль forward (вперёд курса) на
    /// DustSpawnForwardBias, чтобы при движении новые точки рождались туда, куда
    /// летит корабль. При forward==Zero (покой) смещения нет.</summary>
    private Vector3 RandomInShell(Vector3 playerPos, Vector3 forward, float rMax, float rMin)
    {
        Vector3 dir = RandomDir();
        if (forward != Vector3.Zero)
            dir = (dir + forward * DustSpawnForwardBias).Normalized();
        // Равномерность по объёму шара: r = rMax * cbrt(u). Смещаем минимум, чтобы
        // не ставить точку в камеру.
        float u = _dustRng.Randf();
        float r = rMin + (rMax - rMin) * Mathf.Pow(u, 1f / 3f);
        return playerPos + dir * r;
    }
}
