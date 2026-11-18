using Godot;

namespace SpaceCorporation;

/// <summary>
/// Стадия 5 (Этап 5, В20): состояние стыковки, константы и чистые функции.
/// По образцу <see cref="CollisionDamage"/> (стадия 4): никаких узлов,
/// <c>GD.Print</c> и <c>NetworkManager</c> — всё проверяется без дерева сцены.
///
/// Фазы сделки (значения попадают в репликацию как <c>byte</c> — порядок
/// фиксирован, переставлять нельзя):
///   None → Requested → Approved → Capturing → Latched → Docked,
/// плюс авто-сброс Approved по окну <see cref="DockWindowSec"/> и отмена
/// незавершённой сделки при <c>OnPeerLeft</c> (шаг 5.11).
/// </summary>
public enum DockState : byte
{
    /// <summary>Стыковки нет.</summary>
    None = 0,
    /// <summary>Запрос принят, сервер ещё не разрешил (редкая, переходная).</summary>
    Requested = 1,
    /// <summary>«Огни» горят, ждём входа в док-окно по скорости.</summary>
    Approved = 2,
    /// <summary>Сервер ведёт корабль пружиной (шаг 5.8).</summary>
    Capturing = 3,
    /// <summary>Механическая фиксация, дотягивание/дожим.</summary>
    Latched = 4,
    /// <summary>Финал: Freeze у сервера, скорости обнулены (шаг 5.10).</summary>
    Docked = 5,
}

/// <summary>
/// Константы и чистые функции стыковки (stage5-plan §1). Имена фиксируются до
/// кода, чтобы тесты, панель и план говорили одними словами.
/// </summary>
public static class Docking
{
    /// <summary>«Корабль в зоне»: расстояние от DockingPoint до корабля, м.
    /// П14: 90→1000 м — разрешение получают издалека, затем летят к площадке;
    /// вне зоны запрос отклоняется с причиной в комм-лог HUD.</summary>
    public const float DockRange = 1000f;

    /// <summary>«Скорость ниже порога», м/с (|vn| к станции — та же величина,
    /// что <c>CollisionDamage.ApproachSpeed</c>). Заведомо НИЖЕ порога урона
    /// 12, чтобы валидный запрос не успел натаранить (В19).</summary>
    public const float DockSpeedLimit = 8f;

    /// <summary>Жизнь разрешения: Approved без начала захвата → авто-сброс в
    /// None (иначе «огни горят вечно», assert п.16). Механика посадки (П3-правки):
    /// 5 минут — игрок за это время успевает долететь до площадки; разрешение
    /// также гасится досрочно: отзыв на дистанции > <see cref="DockCancelRangeM"/>
    /// от станции, отмена через радиоменю, или отключение пира.</summary>
    public const float DockWindowSec = 300f;

    /// <summary>Предел «улета» с живым разрешением (механика посадки, П3-правки):
    /// Approved с кораблем дальше этой дистанции от станции → авто-отзыв.</summary>
    public const float DockCancelRangeM = 1200f;

    /// <summary>Жёсткость позиционной пружины: a += k·Δx, 1/с².
    /// Правка ТЗ: скорость захвата увеличена вдвое (K 6→12; кламп и
    /// терминальная скорость a/c — тоже ×2).</summary>
    public const float SpringK = 12.0f;

    /// <summary>Демпфирование пружины: a -= c·v, 1/с (без него пружина =
    /// вечный осциллятор, картинка «качается»).</summary>
    public const float SpringC = 4.0f;

    /// <summary>Кламп ускорения пружины, м/с²: запрет «пружина рванула через
    /// станцию» (иначе одномоментный пролёт и повторный контакт). ×2 вместе
    /// с SpringK (терминальная a/c ≈ 12.5 м/с — вдвое быстрее прежнее).</summary>
    public const float MaxSpringAccel = 50f;

    /// <summary>Жёсткость угловой доводки оси носа к оси стыка, 1/с² (×2 —
    /// та же удвоенная скорость захвата по вращению).</summary>
    public const float AlignK = 6.0f;

    /// <summary>Демпфирование угловой доводки, 1/с.</summary>
    public const float AlignDamp = 3.0f;

    /// <summary>Capturing → Latched («фиксация»), м.</summary>
    public const float LatchDistance = 3.0f;

    /// <summary>Угловое условие лatch'а: косинус угла нос↔точка стыка (≈26°).
    /// Применяется только для стыковок без площадки (внешние станции-посадочные
    /// узлы); посадка на площадку использует <see cref="UprightLatchCos"/>.</summary>
    public const float LatchAlignCos = 0.9f;

    /// <summary>Фикс бага «разворачивает вбок при посадке»: угловое условие
    /// лatch'а для ПОСАДКИ НА ПЛОЩАДКУ — косинус угла «верх корабля ↔ нормаль
    /// палубы» (≈0.9 → ~26°, та же строгость, что LatchAlignCos). Нос к точке
    /// контакта больше не тянется: у площадки точка находится ПОД кораблём,
    /// и ориентация «нос вдоль Δx» задирала нос/бок вниз вместо «брюхом вниз».</summary>
    public const float UprightLatchCos = 0.9f;

    /// <summary>Latched → Docked: корабль «на замке» (м).</summary>
    public const float DockDistance = 0.5f;

    /// <summary>Latched → Docked: почти нулевая скорость (м/с).</summary>
    public const float DockSpeedEps = 0.3f;

    /// <summary>Нет заявителя (сброс/инициализация).</summary>
    public const int DockerPeerNone = 0;

    // ==================================================================
    // Механика посадки, П6 (docs/landing-mechanic-plan.md): голографическая
    // схема выравнивания. Допуск углов — 5°, допуск смещения — константа.
    // ==================================================================

    /// <summary>Допуск по каждой из трёх угловых ошибок (крен/тангаж/рыскание),
    /// градусы (ТЗ: «погрешность выравнивания не более 5 градусов»).</summary>
    public const float AlignTolDeg = 5f;

    /// <summary>То же в радианах (для сравнений с AlignError в радианах).</summary>
    public static readonly float AlignTolRad = Mathf.DegToRad(AlignTolDeg);

    /// <summary>Допуск горизонтального смещения корабля от оси площадки, м.</summary>
    public const float AlignOffsetTol = 2.5f;

    /// <summary>П13: допуск вертикального выравнивания — отстояние корабля
    /// вдоль нормали палубы от точки стыка, м. Без него «фиксация»
    /// происходила на любой высоте в сфере CaptureRadius.</summary>
    public const float AlignAltTol = 3f;

    /// <summary>Радиус «зоны захвата» площадки вокруг точки зависания, м:
    /// вне его выравнивание не проверяется и захват не запускается
    /// (внутри LandingZone 12×10×12 — площадка физически рядом).</summary>
    public const float CaptureRadius = 15f;

    /// <summary>Набор ошибок выравнивания для голографической схемы (П6).
    /// Все углы — градусы; знак показывает, куда доворачивать. Within —
    /// все три угла в допуске AlignTolDeg и смещение в AlignOffsetTol.</summary>
    public struct AlignErrorSet
    {
        public float YawDeg;    // рыскание: нос не совпал с осью площадки (в плоскости)
        public float PitchDeg;  // тангаж: нос задран/опущен относительно плоскости
        public float RollDeg;   // крен: вокруг оси носа
        public float OffsetM;   // горизонтальное смещение от оси точки зависания, м
        public Vector2 OffsetXY; // то же в осях плоскости площадки (X=fwd, Y=right), м
        public float AltM;      // П13: отстояние вдоль нормали палубы от точки стыка, м (знак: выше +)
        public bool Within;     // всё в допуске → «фиксация», запуск захвата
    }

    /// <summary>
    /// Чистая функция схемы выравнивания: три угла + смещение корабля
    /// относительно локальной системы площадки (padUp — нормаль палубы,
    /// padFwd — «направление захода» в плоскости площадки, nose = −Z корабля).
    /// </summary>
    public static AlignErrorSet ComputeAlignError(Vector3 shipPos, Quaternion shipRot,
                                                  Vector3 padPos, Vector3 padUp, Vector3 padFwd)
    {
        var up = padUp.Normalized();
        var fwd = (padFwd - up * padFwd.Dot(up)).Normalized();
        var right = fwd.Cross(up); // лежачая система площадки
        var nose = (shipRot * Vector3.Forward).Normalized(); // нос = −Z (Godot Forward!)

        // Тангаж: угол носа над/под плоскостью площадки.
        float pitchRad = Mathf.Asin(Mathf.Clamp(nose.Dot(up), -1f, 1f));

        // Рыскание: подписанный угол в плоскости площадки между осью захода
        // и проекцией носа (при вертикальном nose вырождается в 0 — допуск
        // по тангажу всё равно это ловит).
        var noseFlat = nose - up * nose.Dot(up);
        float yawRad = noseFlat.LengthSquared() > 1e-6f
            ? Mathf.Atan2(noseFlat.Dot(right), noseFlat.Dot(fwd))
            : 0f;

        // Крен: подписанный угол вокруг оси носа между «верхом» площадки и
        // «верхом» корабля (проекция обоих на перпендикуляр к nose).
        var shipUp = (shipRot * Vector3.Up).Normalized();
        var a = up - nose * up.Dot(nose);
        var b = shipUp - nose * shipUp.Dot(nose);
        float rollRad = a.LengthSquared() > 1e-6f && b.LengthSquared() > 1e-6f
            ? Mathf.Atan2(a.Cross(b).Dot(nose), a.Dot(b))
            : 0f;

        // Смещение: отстояние проекции корабля на плоскость площадки от её оси.
        Vector3 d = shipPos - padPos;
        Vector3 dFlat = d - up * d.Dot(up);
        float offsetM = dFlat.Length();
        float altM = d.Dot(up); // П13: вертикальное выравнивание по нормали палубы

        var e = new AlignErrorSet
        {
            YawDeg = Mathf.RadToDeg(yawRad),
            PitchDeg = Mathf.RadToDeg(pitchRad),
            RollDeg = Mathf.RadToDeg(rollRad),
            OffsetM = offsetM,
            OffsetXY = new Vector2(dFlat.Dot(fwd), dFlat.Dot(right)),
            AltM = altM,
        };
        e.Within = Mathf.Abs(e.YawDeg) <= AlignTolDeg
                   && Mathf.Abs(e.PitchDeg) <= AlignTolDeg
                   && Mathf.Abs(e.RollDeg) <= AlignTolDeg
                   && offsetM <= AlignOffsetTol
                   && Mathf.Abs(altM) <= AlignAltTol;
        return e;
    }

    /// <summary>Угол лatch'а в радианах: acos(LatchAlignCos).</summary>
    public static readonly float LatchAlignAngleRad = Mathf.Acos(LatchAlignCos);

    /// <summary>«Корабль в зоне» — расстояние от точки стыка, м.</summary>
    public static bool InZone(Vector3 shipPos, Vector3 dockPos)
        => shipPos.DistanceTo(dockPos) <= DockRange;

    /// <summary>Док-окно по скорости: |vn| ниже порога.</summary>
    public static bool AllowDock(float vnAbs)
        => vnAbs <= DockSpeedLimit;

    /// <summary>
    /// Серверная позиционная пружина (шаг 5.8): a = clamp(k·Δx − c·v,
    /// MaxSpringAccel). dx — вектор от корабля к точке стыка.
    /// </summary>
    public static Vector3 CaptureAccel(Vector3 dx, Vector3 v)
    {
        var a = dx * SpringK - v * SpringC;
        if (a.LengthSquared() > MaxSpringAccel * MaxSpringAccel)
            a = a.Normalized() * MaxSpringAccel;
        return a;
    }

    /// <summary>
    /// Ошибка доворота: угол (рад) между осью носа корабля (−Z локальной системы)
    /// и осью стыка в мире (нормаль «в станцию», т.е. из точки корабля в точку
    /// стыка — нормализованная). 0 = нос смотрит точно в точку стыка.
    /// </summary>
    public static float AlignError(Quaternion shipRot, Vector3 dockAxisWorld)
    {
        var nose = shipRot * Vector3.Forward; // -Z = нос (Godot 4: Forward=(0,0,-1))
        var axis = dockAxisWorld;
        if (axis.LengthSquared() < 1e-9f)
            return 0f;
        axis = axis.Normalized();
        float d = nose.Dot(axis);
        // Num/float точность: кламп в [-1,1] перед acos.
        return Mathf.Acos(Mathf.Clamp(d, -1f, 1f));
    }

    /// <summary>Фикс бага «при посадке разворачивает вбок»: ось «верх» корабля
    /// в мире (низ корпуса = -Y локальной рамки, брюхо смотрит вниз).</summary>
    public static Vector3 ShipUp(Quaternion shipRot) => shipRot * Vector3.Up;

    /// <summary>Косинус угла «верх корабля ↔ нормаль палубы»: 1, когда брюхо
    /// смотрит точно на палубу (правильная посадка), -1 — корабль вверх килем.
    /// Это правильная угловая метрика для ПОСАДКИ на площадку: AlignError
    /// сравнивает НОС с направлением на точку стыка, а у площадки точка стыка
    /// находится ПОД кораблём — «нос на точку» задирал нос/бок вниз.</summary>
    public static float UprightCos(Quaternion shipRot, Vector3 deckUp)
    {
        var up = deckUp.LengthSquared() < 1e-9f ? Vector3.Up : deckUp.Normalized();
        return Mathf.Clamp(ShipUp(shipRot).Dot(up), -1f, 1f);
    }

    /// <summary>Целевая ориентация «брюхом к палубе» с сохранением текущего
    /// рыскания: верх корабля доводится к нормали палубы, нос остаётся там же
    /// по азимуту (проекция носа в плоскость палубы). Крен/тангаж относительно
    /// палубы обнуляются — корабль встаёт на шасси ровно, а не боком.</summary>
    public static Quaternion UprightPose(Quaternion shipRot, Vector3 deckUp)
    {
        var up = deckUp.LengthSquared() < 1e-9f ? Vector3.Up : deckUp.Normalized();
        // Нос модели = локальное −Z. В Godot 4 Vector3.Back = (0,0,+1), а
        // Forward = (0,0,−1), поэтому −Z — это Vector3.Forward! Прежний
        // `rot * Vector3.Back` брал ХВОСТ: выход разворачивался рысканием на
        // 180°, а с зеркальным базисом (см. ниже) — ещё и вверх килем.
        var nose = shipRot * Vector3.Forward;
        var fwdFlat = nose - up * nose.Dot(up); // азимут носа в плоскости палубы
        if (fwdFlat.LengthSquared() < 1e-6f)
            fwdFlat = up == Vector3.Up ? Vector3.Forward : up.Cross(Vector3.Forward).Normalized();
        else
            fwdFlat = fwdFlat.Normalized();
        // Правый базис Godot: Basis(X=right, Y=up, Z=−nose). X = Y×Z = up×(−noseFlat)
        // = noseFlat×up (det=+1; прежний up.Cross(fwd) давал det=−1 — отражение,
        // кватернион из которого переворачивал корабль вверх килем).
        var right = fwdFlat.Cross(up).Normalized();
        return new Basis(right, up, -fwdFlat).GetRotationQuaternion();
    }

    /// <summary>Условие фиксации: расстояние и ориентация (нос к точке стыка).</summary>
    public static bool IsLatched(Vector3 shipPos, Vector3 dockPos, float alignErr)
        => shipPos.DistanceTo(dockPos) <= LatchDistance
           && alignErr <= LatchAlignAngleRad;

    /// <summary>Фикс бага «разворачивает вбок при посадке»: условие фиксации
    /// для ПОСАДКИ НА ПЛОЩАДКУ — расстояние + «брюхо к палубе» (uprightCos =
    /// <see cref="UprightCos"/>, порог UprightLatchCos ≈ 26°). Та же строгость
    /// по углу, что и LatchAlignCos для стыковки к внешнему узлу.</summary>
    public static bool IsLatchedUpright(Vector3 shipPos, Vector3 dockPos, float uprightCos)
        => shipPos.DistanceTo(dockPos) <= LatchDistance && uprightCos >= UprightLatchCos;

    /// <summary>Условие финала: корабль «на замке» — в допуске по позиции и
    /// почти без скорости (dx — вектор от корабля к точке стыка).</summary>
    public static bool IsDocked(Vector3 dx, Vector3 v)
        => dx.Length() <= DockDistance && v.Length() <= DockSpeedEps;

    /// <summary>Фаза «идёт захват» (dock-фазы, где пилот не предсказывает и
    /// контакт с «нашей» станцией не наносит урон): Capturing/Latched/Docked.</summary>
    public static bool IsCapturePhase(DockState s)
        => s >= DockState.Capturing;

    /// <summary>Незавершённая сделка (отменяется при OnPeerLeft, шаг 5.11).</summary>
    public static bool IsPending(DockState s)
        => s != DockState.None && s < DockState.Docked;
}
