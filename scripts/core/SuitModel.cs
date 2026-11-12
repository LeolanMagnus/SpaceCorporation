using Godot;

namespace SpaceCorporation;

/// <summary>
/// EVA-режим игрока (eva-plan, Б1). Значения фиксированы числами: их хранит
/// сеть (Блок D) и серверный `SuitState`, и перенумерация сломает протокол.
///  • Seat   — игрок пристёгнут к креслу (Ship/Station), управляет им;
///  • Jetpack — свободный полёт 6DoF (тот же InputFrame, что у корабля);
///  • Boots  — магнитные ботинки на поверхности (ходьба по плоскости нормали).
/// </summary>
public enum EvaMode { Seat = 0, Jetpack = 1, Boots = 2 }

/// <summary>
/// Состояние расходников скафандра (eva-plan Б1). Чистая структура — ни узлов,
/// ни времени: по ней же пишется автотест без сцены (как по `Docking`/`PilotAim`).
/// Единицы — проценты от полного запасa (`MassFuelTotal`/`BatteryTotal`), оба
/// клампятся в [0, 100] внутри функций `SuitModel`, наружу наружный код обязан
/// читать, а не менять руками.
///
/// `HeatOn` по ТЗ «всегда включён» — true создаёт `SuitModel.Fresh()`; поле
/// живёт ради двух событий: гашение при севшей батарее (ФЛАГ В22-пояснение в
/// плане: «HeatOn гаснет») и будущие модули/отключения. `LowFuelWarn`/
/// `LowBatteryWarn` — флаги HUD с гистерезисом (порог включения 10 %,
/// выключения 12 % — ФЛАГ: ширина полосы в ТЗ не названа, константа).
///
/// `ActiveSuitModules` — битовая маска активных модулей-задела (Б3, см.
/// `SuitModuleKind`): заведена уже сейчас, чтобы Блок C/D не меняли формат
/// структуры при переносе по сети.
/// </summary>
public struct SuitState
{
    public float Fuel;        // реактивная масса джетпака, % (0..MassFuelTotal)
    public float Battery;     // батареяLifeSupport, % (0..BatteryTotal)
    public bool HeatOn;       // обогрев (по ТЗ всегда; гаснет только при 0 % батареи)
    public bool LightOn;      // налобный фонарь (клавиша suit_light, Блок C/E)
    public bool BootsOn;      // магнитные ботинки активны (режим Boots)
    public bool LowFuelWarn;
    public bool LowBatteryWarn;
    public uint ActiveSuitModules; // битовая маска SuitModuleKind (задел Б3)

    /// <summary>Полный свежий скафандр: оба счётчика 100 %, обогрев включён
    /// (ТЗ: «постоянный»), свет и ботинки выключены.</summary>
    public static SuitState Fresh() => new SuitState
    {
        Fuel = SuitModel.MassFuelTotal,
        Battery = SuitModel.BatteryTotal,
        HeatOn = true,
    };
}

/// <summary>
/// Модель расходников EVA (eva-plan, Б1–Б2): чистые функции «вход — факт
/// движения, а не намерение»: нулевая тяга ⇒ нулевой расход, покое джетпака
/// массу не тратит. Тест: tests/SuitModelTest (п.1–3 ТЗ: 1 %/с линейная тяга,
/// 0.2 %/с вращение на пределе, 4 %/мин обогрев).
/// </summary>
public static class SuitModel
{
    // ── Скафандр: движение (eva-plan §2; числа для Блока C живут здесь) ──────
    public const float SuitMaxLinearSpeed = 10f;   // м/с   (ТЗ)
    public const float SuitAccel = 8f;             // м/с²  (подбор: 1.25 с до потолка)
    public const float SuitMaxAngularSpeed = 0.5f; // рад/с — slew-rate look-канала
    // (поднято с 0.1 по запросу: скафандр вращается заметно шустрее; пределы
    // вращения кораблей одновременно понижены — SuitModel ↔ ShipTypes.cs)
    public const float SuitTurnWarnDeg = 60f;      // ° — гейт ботинок (ТЗ, тот же угол, что у пола)

    // ── Скафандр: расходники (ТЗ + подбор, ФЛАГИ В25) ────────────────────────
    public const float MassFuelTotal = 100f;   // %, условный полный бак
    public const float MassFuelBurnLinear = 1.0f; // %/с при линейной тяге (ТЗ)
    public const float MassFuelBurnRot = 0.2f;    // %/с при довороте на пределе (ТЗ)
    public const float BatteryTotal = 100f;    // %
    public const float HeatDrawPerMin = 4.0f;  // %/мин, постоянно (ТЗ)
    public const float LightDrawPerMin = 12.0f; // %/мин при включённом свете (подбор)
    public const float RechargePerMin = 20f;   // %/мин батареи при креплении к кораблю
    public const bool RefuelOnAttach = true;   // мгновенная заправка массы при креплении к КОРАБЛЮ

    // ── Ботинки (eva-plan §2) ────────────────────────────────────────────────
    // Правка 7-й части: дистанцию магнитного «захвата» палубы подняли с
    // ТЗ-ных 1.0 м до 2.5 м — раньше ботинки включались только вплотную
    // (≈1 м) к палубе, игрок не дотягивался до неё с нормальной высоты
    // шага/прыжка. Константа задаёт и радиус поиска опоры (ActivateBoots:
    // reach = +0.1), и дальность пробы «есть ли палуба за краем» (+0.3),
    // и потолок серво-притяжения по нормали (±BootsMaxStickDist м/с).
    public const float BootsMaxStickDist = 2.5f; // м
    public const float BootsMaxSlopeDeg = 60f;   // ° (ТЗ) — иначе переход невозможен
    public const float BootsWalkSpeed = 3f;      // м/с (дефолт MoveInAttachedPlane)
    public const float BootsStepProbeLen = 1.2f; // м — луч «следующей ступеньки»
    public const float BootsStickOffset = 0.05f; // м зазор капсулы от поверхности

    // ── Гистерезис HUD-предупреждений (порог ТЗ-подобный 10 %; полоса — подбор)
    public const float WarnOnPct = 10f;
    public const float WarnOffPct = 12f;

    /// <summary>
    /// Сколько реактивной массы сгорит за dt (в %). Расход — по ФАКТУ движения
    /// (ТЗ-формулировка про 0.2 %/с из плана §1.1):
    ///  • linThrust01 — доля включённых линейных осей (0/1, «идёт манёвр» ⇒ 1);
    ///  • rotUsedRad  — угол (рад), реально приложенный за dt (кинематический
    ///    look-канал, см. §1.1: расход честный — довод до центра = 0);
    ///  • доля предела = rotUsedRad / (maxAng·dt), клампится [0,1] (защита от
    ///    численного перелёта), при maxAng &lt;= 0 или dt &lt;= 0 — ноль.
    /// Формула: (1 %/с·lin + 0.2 %/с·rotFraction)·dt. Массы уже нет ⇒ 0
    /// (джетпак «сухой» — молчит, пока не заправят: Б2/Seat).
    /// </summary>
    public static float BurnFuel(in SuitState s, float linThrust01, float rotUsedRad,
                                 float maxAng, float dt)
    {
        if (s.Fuel <= 0f || dt <= 0f || maxAng <= 0f) return 0f;

        float lin = Mathf.Clamp(linThrust01, 0f, 1f);
        float rot = Mathf.Clamp(rotUsedRad / (maxAng * dt), 0f, 1f);
        float rate = MassFuelBurnLinear * lin + MassFuelBurnRot * rot; // %/с
        float burned = rate * dt;
        // Не даём сжечь больше остатка: краевой случай — последние миллисекунды бака.
        return Mathf.Min(burned, s.Fuel);
    }

    /// <summary>
    /// Сколько батарея потеряет за dt (в %). Обогрев — 4 %/мин «постоянно»,
    /// т.е. всегда при HeatOn (в Seat тоже — ФЛАГ В22), свет — +12 %/мин при
    /// LightOn. Отрицательного значения не бывает (зарядка — отдельная функция
    /// `StepRecharge`).
    /// </summary>
    public static float BurnBattery(in SuitState s, float dt)
    {
        if (dt <= 0f) return 0f;
        float rate = 0f; // %/мин
        if (s.HeatOn) rate += HeatDrawPerMin;
        if (s.LightOn) rate += LightDrawPerMin;
        return Mathf.Min(rate / 60f * dt, s.Battery);
    }

    /// <summary>
    /// Один шаг расходников: применяет BurnFuel/BurnBattery к состоянию,
    /// обновляет гистерезис предупреждений и гасит энергопотребителей при 0 %
    /// батареи (LightOn/HeatOn — false; джетпак остаётся: реактивная масса —
    /// отдельный счётчик, ТЗ их не связывает). Единственная точка, где SuitState
    /// меняется от времени (Player и серверный путь Блока D зовут её).
    /// </summary>
    public static void StepDrain(ref SuitState s, float linThrust01, float rotUsedRad,
                                 float maxAng, float dt)
    {
        s.Fuel = Mathf.Clamp(s.Fuel - BurnFuel(s, linThrust01, rotUsedRad, maxAng, dt),
                             0f, MassFuelTotal);
        float burned = BurnBattery(s, dt);
        s.Battery = Mathf.Clamp(s.Battery - burned, 0f, BatteryTotal);

        if (s.Battery <= 0f)
        {
            s.LightOn = false;
            s.HeatOn = false; // ФЛАГ В22-оговорка: севшая батарея глушит оба
        }
        UpdateWarnings(ref s);
    }

    /// <summary>
    /// Крепление к КОРАБЛЮ (путь ApplyAttach, только при AttachPoint.OwnerNetId
    /// &gt; 0 и предке-Ship — станция не заправляет, ФЛАГ В25): мгновенная
    /// заправка реактивной массы (RefuelOnAttach, ТЗ) и старт зарядки батареи
    /// (идёт дальше через `StepRecharge`).
    /// </summary>
    public static void ApplyShipAttach(ref SuitState s)
    {
        if (RefuelOnAttach) s.Fuel = MassFuelTotal;
        UpdateWarnings(ref s);
    }

    /// <summary>Зарядка батареи при креплении к кораблю: +RechargePerMin %/мин.
    /// `attachedToShip = false` (станция/свобода) — ничего не делает (ТЗ).</summary>
    public static void StepRecharge(ref SuitState s, bool attachedToShip, float dt)
    {
        if (!attachedToShip || dt <= 0f) return;
        s.Battery = Mathf.Clamp(s.Battery + RechargePerMin / 60f * dt, 0f, BatteryTotal);
        // Восстановление батареи возвращает обогрев в «постоянно включён» (ТЗ),
        // но НЕ включает свет: фонарь — решение игрока (клавиша).
        if (s.Battery > 0f && !s.HeatOn) s.HeatOn = true;
        UpdateWarnings(ref s);
    }

    /// <summary>Гистерезис HUD: включились при &lt;= WarnOnPct, выключились при
    /// &gt;= WarnOffPct (между — держат прежнее значение, иначе строка HUD
    /// мигала бы на каждом кадре у границы 10 %).</summary>
    public static void UpdateWarnings(ref SuitState s)
    {
        if (s.LowFuelWarn) s.LowFuelWarn = s.Fuel <= WarnOffPct;
        else s.LowFuelWarn = s.Fuel <= WarnOnPct;

        if (s.LowBatteryWarn) s.LowBatteryWarn = s.Battery <= WarnOffPct;
        else s.LowBatteryWarn = s.Battery <= WarnOnPct;
    }

    /// <summary>
    /// Гейт «можно ли наступить» (В13=B, ФЛАГ В13): магнитные ботинки липнут
    /// только к поверхностям, наклонённым не круче `BootsMaxSlopeDeg` (60°) к
    /// вертикали. Чистая функция нормали — её же дёргает и `ActivateBoots`
    /// (лучевая проверка), и автотест п.24 (59° липнет, 61° отказ): держать
    /// угол в одном месте, чтобы физика и тест не разъезжались (урок `Docking`).
    /// Нулевая/вырожденная норма (LengthSquared&lt;≈0) — «нет поверхности»,
    /// отказ; знак нормали не важен — считаем острый угол к вертикали (|dot|).
    /// </summary>
    public static bool CanStepOn(Vector3 surfaceNormal)
    {
        if (surfaceNormal.LengthSquared() < 0.25f) // длина < 0.5 → не норма
            return false;
        float dot = Mathf.Abs(Vector3.Up.Dot(surfaceNormal.Normalized()));
        float ang = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(dot, 0f, 1f)));
        // Граница включительно с микропуском: `Acos(0.5)` в float даёт
        // 60.000004°, и строгое `<=` отвергало бы ровно-60° поверхность
        // (граница должна липнуть — тест п.24 проверяет 60° PASS, 61° FAIL).
        return ang <= BootsMaxSlopeDeg + 1e-3f;
    }
}
