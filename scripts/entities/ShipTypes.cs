using Godot;

namespace SpaceCorporation;

/// <summary>
/// Типы кораблей прототипа. Порядок фиксирован: byte-значение enum уходит в
/// реплику `NetState.ShipTypeValue`, поэтому перечислять существующие элементы
/// нельзя (старый клиент и новый сервер по-разному прочитали бы байт).
/// 0 = Courier — значение по умолчанию (требование задачи: спавнимся курьером).
/// </summary>
public enum ShipType : byte
{
    /// <summary>Курьер: маленький и юркий (жёлтый).</summary>
    Courier = 0,
    /// <summary>Грузовик: средний и неповоротливый (оранжевый).</summary>
    Hauler = 1,
    /// <summary>Контейнеровоз: чуть больше среднего, тоже неповоротливый (красный).</summary>
    ContainerFreighter = 2,
}

/// <summary>
/// Характеристики ОДНОГО типа корабля: база полётной модели + габариты и цвет
/// примитивного корпуса. Чистые данные, узлов не держат.
///
/// ВАЖНО про `Base`: это то же основание, из которого `Ship._baseFlight`
/// пересобирает `Flight` = `Loadout.Build(base)` (Этап 6: тяговые ускорения и
/// потолки скорости приходят из модулей, а корпус привносит массу, демпфирование,
/// потолок вращения корпуса и множитель форсажа). designer-дружественно: поля
/// подписаны и лежат в одном месте — <see cref="ShipStatCatalog"/>.
/// </summary>
public sealed class ShipTypeSpec
{
    public readonly ShipType Type;
    /// <summary>Человекочитаемое имя (дебаг-панель, RPC-отказ).</summary>
    public readonly string DisplayName;

    /// <summary>Масса корпуса, кг (идёт в `RigidBody3D.Mass` и в `Flight.Mass`:
    /// импульсы, отскоки, тараны).</summary>
    public readonly float Mass;

    /// <summary>База полётной модели корпуса (до модулей). Дизайнеры балансят
    /// именно её; модули поверх (см. ShipLoadout.Build).</summary>
    public readonly FlightParams Base;

    /// <summary>Габариты примитивного корпуса-параллелепипеда, м (ширина,
    /// высота, длина). Один и тот же размер даёт и collision BoxShape3D, и
    /// BoxMesh картинки — провалить корпус сквозь коллизию нельзя.</summary>
    public readonly Vector3 HullSize;

    /// <summary>Цвет корпуса (примитив-замена art-модели).</summary>
    public readonly Color HullColor;

    /// <summary>Навесные примитивы силуэта (только картинка, коллайдер — один
    /// корпус). Смещение относительно центра корпуса, размер и цвет — дизайнеры
    /// рисуют ими читаемый силуэт типа: контейнеры, груз, кили. Ноль коллизии:
    /// в эти детали корабль «входит» freely, это заглушка до art-моделей.</summary>
    public readonly (Vector3 Offset, Vector3 Size, Color Color)[] Accessories;

    public ShipTypeSpec(ShipType type, string displayName, float mass,
                        FlightParams @base, Vector3 hullSize, Color hullColor,
                        (Vector3 Offset, Vector3 Size, Color Color)[]? accessories = null)
    {
        Type = type;
        DisplayName = displayName;
        Mass = mass;
        Base = @base;
        HullSize = hullSize;
        HullColor = hullColor;
        Accessories = accessories ?? System.Array.Empty<(Vector3, Vector3, Color)>();
    }
}

/// <summary>
/// Общий список характеристик кораблей (статический каталог). Требование задачи:
/// «характеристики, используемые в полётной модели, хранятся отдельно» — сюда
/// приходит дизайнер за правкой чисел, и только сюда. `Ship` при спавне берёт
/// запись по своему типу (по умолчанию Courier) и строит из неё `_baseFlight`.
///
/// Текущие числа — первая расстановка, НЕ балансировка:
///  • Courier совпадает с прежними константами Ship (1200 кг, 90 кН, 40 кН·м,
///    демпф 0/1.5, форсаж ×2, потолок вращения 6 рад/с) — все существующие
///    прогоны и тесты считают ровно те же числа;
///  • Hauler и ContainerFreighter — тяжелее, с низким потолком вращения и
///    слабым форсажом: «неповоротливые» уже здесь, на уровне корпуса (модули
///    тяги у всех общие до той поры, пока каталог не начнут наполнять всерьёз).
/// </summary>
public static class ShipStatCatalog
{
    public const ShipType DefaultType = ShipType.Courier;

    /// <summary>Запись корабля по умолчанию (спавн без явно заданного типа).</summary>
    public static ShipTypeSpec DefaultShip => Find(DefaultType);

    /// <summary>Все записи каталога в порядке enum (дебаг-панель рисует по одной
    /// кнопке на запись). Порядок = байт реплики, переставлять элементы нельзя.</summary>
    public static ShipTypeSpec[] Types => Specs;

    private static readonly ShipTypeSpec[] Specs =
    {
        new ShipTypeSpec(
            ShipType.Courier, "Courier (курьер)",
            mass: 1200f,
            // Ровно прежние константы Ship._Ready — иначе поедут тесты стадии 1/2.
            @base: new FlightParams
            {
                Mass = 1200f,
                LinearThrust = 90000f,
                AngularThrust = 40000f,
                LinearDamping = 0f,
                // Затухание СВОБОДного вращения (после удара, доводка стыковки)
                // при заданном `AngularRiseTime` НЕ участвует: разгон и остановку
                // на руле ведёт одна τ режима следования (п.5a) — симметрично в
                // обе стороны. Демпфер остаётся на случай кораблей без τ.
                AngularDamping = 1.5f,
                BoostMultiplier = 2f,
                MaxAngularSpeed = 6f,
                // Юркий: практически до своего предела вращения (6 рад/с) — за
                // полсекунды; дальше скорость держится на плато, пока зажато
                // Q/E. Столько же (0.6 с) занимает и остановка после отпускания:
                // разгон и торможение идут одной τ. Ниже 0.54 с опустить нельзя
                // без замены РСУ: сопла Mk1 (a = 33.3 рад/с²) физически не
                // разгоняют раму быстрее.
                AngularRiseTime = 0.6f,
            },
            hullSize: new Vector3(2f, 1f, 4f),   // маленький
            hullColor: new Color(0.95f, 0.8f, 0.1f), // жёлтый
            // Силуэт юркого курьера: кабина-«капля» спереди + два маленьких
            // киля-стабилизатора сзади (только картинка, см. Accessories).
            accessories: new[]
            {
                (new Vector3(0f, 0.25f, -1.4f), new Vector3(1f, 0.6f, 1f),
                 new Color(0.95f, 0.8f, 0.1f)),
                (new Vector3(-0.8f, 0f, 1.5f), new Vector3(0.2f, 0.8f, 0.9f),
                 new Color(0.75f, 0.62f, 0.08f)),
                (new Vector3(0.8f, 0f, 1.5f), new Vector3(0.2f, 0.8f, 0.9f),
                 new Color(0.75f, 0.62f, 0.08f)),
            }),

        new ShipTypeSpec(
            ShipType.Hauler, "Hauler (грузовик)",
            mass: 5200f,
            @base: new FlightParams
            {
                Mass = 5200f,
                LinearThrust = 150000f,   // ~29 м/с²: разгон вдвое вялее курьера
                AngularThrust = 75000f,
                LinearDamping = 0f,
                AngularDamping = 1.0f,
                BoostMultiplier = 1.5f,
                MaxAngularSpeed = 2.2f,   // неповоротливый: потолок вращения ниже
                MaxLinearSpeed = 1200f,
                // Неповоротливый: практически до своего предела вращения — за
                // секунду; заметный перенос ответа на нажатие Q/E. Остановка после
                // отпускания — ровно за те же 0.9 с (одна τ на обе стороны).
                AngularRiseTime = 0.9f,
            },
            hullSize: new Vector3(3.5f, 2.5f, 8f), // средний
            hullColor: new Color(0.95f, 0.5f, 0.1f), // оранжевый
            // Силуэт неповоротливого грузовика: блоковый грузовой отсек сверху
            // и «кабина-коробка» у носа.
            accessories: new[]
            {
                (new Vector3(0f, 1.8f, 1.0f), new Vector3(3f, 1.6f, 4.5f),
                 new Color(0.8f, 0.4f, 0.07f)),
                (new Vector3(0f, 1.7f, -2.8f), new Vector3(2.4f, 1.4f, 1.4f),
                 new Color(0.95f, 0.5f, 0.1f)),
            }),

        new ShipTypeSpec(
            ShipType.ContainerFreighter, "Container Freighter (контейнеровоз)",
            mass: 10000f,
            @base: new FlightParams
            {
                Mass = 10000f,
                LinearThrust = 160000f,   // ~16 м/с²: разгон самый вязкий из трёх
                AngularThrust = 110000f,
                LinearDamping = 0f,
                AngularDamping = 2.2f,    // «чуть больше среднего и тоже неповоротливый»
                BoostMultiplier = 1.5f,
                MaxAngularSpeed = 1.2f,
                MaxLinearSpeed = 1000f,
                // Самая инерционная рама из трёх: раскрутка «до упора» — за две
                // секунды, на руль отвечает отчётливым опозданием. И останавливает
                // вращение контейнеровоз ровно за те же 2 с (одна τ на обе
                // стороны), поэтому «неповоротливость» симметрична.
                AngularRiseTime = 2f,
            },
            hullSize: new Vector3(5f, 3.5f, 14f), // чуть больше среднего
            hullColor: new Color(0.85f, 0.15f, 0.12f), // красный
            // Силуэт контейнеровоза: стопки контейнеров вдоль корпуса (разные
            // оттенки красного) + рубка управления у кормы — читается издалека.
            accessories: new[]
            {
                (new Vector3(0f, 2.5f, -3.0f), new Vector3(4.4f, 1.8f, 4f),
                 new Color(0.75f, 0.25f, 0.2f)),
                (new Vector3(0f, 2.5f, 1.0f), new Vector3(4.4f, 1.8f, 4f),
                 new Color(0.6f, 0.2f, 0.18f)),
                (new Vector3(0f, 4.3f, -3.0f), new Vector3(4.4f, 1.6f, 4f),
                 new Color(0.7f, 0.3f, 0.25f)),
                (new Vector3(0f, 2.7f, 5.5f), new Vector3(3.2f, 2.2f, 1.8f),
                 new Color(0.85f, 0.15f, 0.12f)),
            }),
    };

    /// <summary>Запись каталога по типу. Неизвестный байт (чужой билд) → молча
    /// возвращаем дефолт: тип — не критичные данные, ронять из-за него корабль
    /// нельзя; громко сообщает сам читатель (Ship.OnShipTypeReplica).</summary>
    public static ShipTypeSpec Find(ShipType type)
    {
        foreach (var s in Specs)
            if (s.Type == type) return s;
        return Specs[(int)DefaultType];
    }

    /// <summary>Запись по byte-значению реплики (неизвестный байт → дефолт).</summary>
    public static ShipTypeSpec FindByByte(byte value) =>
        System.Enum.IsDefined(typeof(ShipType), value)
            ? Find((ShipType)value)
            : Specs[(int)DefaultType];
}
