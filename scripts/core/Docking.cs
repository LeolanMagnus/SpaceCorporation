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
    /// Станция r = 60 → 30 м запаса снаружи корпуса.</summary>
    public const float DockRange = 90f;

    /// <summary>«Скорость ниже порога», м/с (|vn| к станции — та же величина,
    /// что <c>CollisionDamage.ApproachSpeed</c>). Заведомо НИЖЕ порога урона
    /// 12, чтобы валидный запрос не успел натаранить (В19).</summary>
    public const float DockSpeedLimit = 8f;

    /// <summary>Жизнь разрешения: Approved без начала захвата → авто-сброс в
    /// None (иначе «огни горят вечно», assert п.16).</summary>
    public const float DockWindowSec = 5f;

    /// <summary>Жёсткость позиционной пружины: a += k·Δx, 1/с².</summary>
    public const float SpringK = 6.0f;

    /// <summary>Демпфирование пружины: a -= c·v, 1/с (без него пружина =
    /// вечный осциллятор, картинка «качается»).</summary>
    public const float SpringC = 4.0f;

    /// <summary>Кламп ускорения пружины, м/с²: запрет «пружина рванула через
    /// станцию» (иначе одномоментный пролёт и повторный контакт).</summary>
    public const float MaxSpringAccel = 25f;

    /// <summary>Жёсткость угловой доводки оси носа к оси стыка, 1/с².</summary>
    public const float AlignK = 3.0f;

    /// <summary>Демпфирование угловой доводки, 1/с.</summary>
    public const float AlignDamp = 3.0f;

    /// <summary>Capturing → Latched («фиксация»), м.</summary>
    public const float LatchDistance = 3.0f;

    /// <summary>Угловое условие лatch'а: косинус угла нос↔точка стыка (≈26°).</summary>
    public const float LatchAlignCos = 0.9f;

    /// <summary>Latched → Docked: корабль «на замке» (м).</summary>
    public const float DockDistance = 0.5f;

    /// <summary>Latched → Docked: почти нулевая скорость (м/с).</summary>
    public const float DockSpeedEps = 0.3f;

    /// <summary>Нет заявителя (сброс/инициализация).</summary>
    public const int DockerPeerNone = 0;

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
        var nose = shipRot * Vector3.Back; // -Z = нос (конвенция Godot: модель смотрит вдоль -Z)
        var axis = dockAxisWorld;
        if (axis.LengthSquared() < 1e-9f)
            return 0f;
        axis = axis.Normalized();
        float d = nose.Dot(axis);
        // Num/float точность: кламп в [-1,1] перед acos.
        return Mathf.Acos(Mathf.Clamp(d, -1f, 1f));
    }

    /// <summary>Условие фиксации: расстояние и ориентация (нос к точке стыка).</summary>
    public static bool IsLatched(Vector3 shipPos, Vector3 dockPos, float alignErr)
        => shipPos.DistanceTo(dockPos) <= LatchDistance
           && alignErr <= LatchAlignAngleRad;

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
