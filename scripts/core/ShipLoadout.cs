using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Комплектация корабля модулями (Этап 6): 5 слотов по одному на вид модуля +
/// флаг поддержки пилотирования. Чистые данные (без узлов) — `Build()` превращает
/// их в `FlightParams`. Единственный источник скоростных характеристик корабля:
/// замена модуля = новая строка в слоте + пересчёт `Flight` (Ship.EquipModule).
///
/// Единицы (Р2 плана): модули описывают ЭФФЕКТИВНЫЕ ускорения (м/с², рад/с²)
/// для типового корпуса; `Flight.ThrustAccel`/`AngularAccel` > 0 включают в
/// FlightModel прямой путь «ускорение напрямую», минуя LinearThrust/Mass.
/// </summary>
public class ShipLoadout
{
    /// <summary>Слот по виду модуля; "" = слот пуст (структурный ноль).</summary>
    private readonly Dictionary<ModuleKind, string> _slots = new();

    /// <summary>
    /// Поддержка пилотирования ВКЛ по умолчанию (требование задачи). false =
    /// «космос жидкий-свободный»: инерция сохраняется вечно (текущее поведение).
    /// </summary>
    public bool PilotAssist = true;

    public ShipLoadout()
    {
        foreach (ModuleKind k in System.Enum.GetValues<ModuleKind>())
            _slots[k] = ModuleCatalog.DefaultFor(k);
    }

    /// <summary>Id модуля в слоте ("" = пусто).</summary>
    public string Get(ModuleKind kind) => _slots.TryGetValue(kind, out string id) ? id : "";

    /// <summary>
    /// Поставить модуль в слот своего вида (null/"" — вынуть). Возвращает false,
    /// если id неизвестен каталогу или вид не совпадает со слотом.
    /// </summary>
    public bool Equip(string? id)
    {
        if (!ModuleCatalog.TryFind(id, out ModuleSpec spec))
            return false; // "" и неизвестный id — отказ; слот чистят через EquipKind(kind, null)
        _slots[spec.Kind] = id;
        return true;
    }

    /// <summary>Поставить/вынуть модуль в слот конкретного вида (null — вынуть).</summary>
    public bool EquipKind(ModuleKind kind, string? id)
    {
        if (string.IsNullOrEmpty(id)) { _slots[kind] = ""; return true; }
        if (!ModuleCatalog.TryFind(id, out ModuleSpec spec) || spec.Kind != kind) return false;
        _slots[kind] = id;
        return true;
    }

    /// <summary>Спецификация слота (default, если пусто/неизвестен).</summary>
    public ModuleSpec Spec(ModuleKind kind) =>
        ModuleCatalog.TryFind(Get(kind), out ModuleSpec spec) ? spec : default;

    // ── Собранные характеристики (для дебага, UI и теста п.11) ────────────────

    /// <summary>Суммарное тяговое ускорение до деградации по питанию, м/с².</summary>
    public float RawThrustAccel()
    {
        float sum = 0f;
        foreach (string id in _slots.Values)
            if (ModuleCatalog.TryFind(id, out ModuleSpec s)) sum += s.ThrustAccel;
        return sum;
    }

    /// <summary>Суммарное угловое ускорение РСУ до деградации, рад/с².</summary>
    public float RawAngularAccel()
    {
        float sum = 0f;
        foreach (string id in _slots.Values)
            if (ModuleCatalog.TryFind(id, out ModuleSpec s)) sum += s.AngularAccel;
        return sum;
    }

    /// <summary>Потолок линейной скорости — максимум по слотам (у нас один двигатель).</summary>
    public float RawMaxLinearSpeed()
    {
        float max = 0f;
        foreach (string id in _slots.Values)
            if (ModuleCatalog.TryFind(id, out ModuleSpec s)) max = Mathf.Max(max, s.MaxLinearSpeed);
        return max;
    }

    /// <summary>Потолок вращения — максимум по слотам; 0 = без лимита (как структурный ноль).</summary>
    public float RawMaxAngularSpeed()
    {
        float max = 0f;
        foreach (string id in _slots.Values)
            if (ModuleCatalog.TryFind(id, out ModuleSpec s)) max = Mathf.Max(max, s.MaxAngularSpeed);
        return max;
    }

    /// <summary>Σ PowerDraw по слотам, усл. ед.</summary>
    public float TotalPowerDraw()
    {
        float sum = 0f;
        foreach (string id in _slots.Values)
            if (ModuleCatalog.TryFind(id, out ModuleSpec s)) sum += s.PowerDraw;
        return sum;
    }

    /// <summary>Σ PowerOutput (реакторы), усл. ед.</summary>
    public float TotalPowerOutput()
    {
        float sum = 0f;
        foreach (string id in _slots.Values)
            if (ModuleCatalog.TryFind(id, out ModuleSpec s)) sum += s.PowerOutput;
        return sum;
    }

    /// <summary>
    /// Множитель деградации от нехватки мощности: Output/ΣDraw, кламп [0,1].
    /// Mk1-комплектация (Output 6 ≥ Draw 5) даёт 1 — характеристик не касается.
    /// </summary>
    public float Powered()
    {
        float draw = TotalPowerDraw();
        if (draw <= 0f) return 1f;
        return Mathf.Clamp(TotalPowerOutput() / draw, 0f, 1f);
    }

    /// <summary>Дальность радара (сумма по слотам) — вне FlightParams, для UI/задела.</summary>
    public float RadarRange()
    {
        float sum = 0f;
        foreach (string id in _slots.Values)
            if (ModuleCatalog.TryFind(id, out ModuleSpec s)) sum += s.RadarRange;
        return sum;
    }

    /// <summary>
    /// Форсаж: BoostMultiplier = 1 + Σ BoostGain. Mk1-сборка (движок 1 +
    /// конденсатор 0.5) даёт ровно 2.5; «пустые слоты» возвращают базовый вид
    /// из baseParams (см. ниже) — тест п.11 сверяет старую сборку с числом 2.
    /// </summary>
    public float BoostMultiplier()
    {
        float sum = 0f;
        foreach (string id in _slots.Values)
            if (ModuleCatalog.TryFind(id, out ModuleSpec s)) sum += s.BoostGain;
        return sum > 0f ? 1f + sum : 0f; // 0 = «нет мнения», Build возьмёт base
    }

    // ── Главный мост: loadout → FlightParams ──────────────────────────────────

    /// <summary>
    /// Собрать `FlightParams` поверх базовых параметров корпуса (base задаёт
    /// Mass/демпфирование/лимиты из [Export]-свойств Ship; пустой loadout обязан
    /// дать ПОБИТОВО base — тест п.11, структурный ноль всех новых полей).
    /// Модули задают: ThrustAccel/AngularAccel (прямые ускорения), MaxLinearSpeed,
    /// MaxAngularSpeed (максимум по слотам; 0 = остаётся базовый), BoostMultiplier,
    /// PilotAssist и AssistDragAccel = ThrustAccel («тормозит так же, как разгоняется»).
    /// Нехватка мощности (Powered &lt; 1) множит ускорения и линейный потолок.
    /// </summary>
    public FlightParams Build(FlightParams baseParams)
    {
        float powered = Powered();
        FlightParams p = baseParams; // копия структуры: всё базовое целое

        float thrust = RawThrustAccel();
        if (thrust > 0f) p.ThrustAccel = thrust * powered;

        float angAccel = RawAngularAccel();
        if (angAccel > 0f) p.AngularAccel = angAccel * powered;

        float maxLin = RawMaxLinearSpeed();
        if (maxLin > 0f) p.MaxLinearSpeed = maxLin * powered;

        float maxAng = RawMaxAngularSpeed();
        // Потолок вращения: если мнение есть и у корпуса (MaxAngularSpeed из
        // сцены, > 0), и у РСУ — берём МЕЛЬШЕЕ: корпус задаёт «сколько выдержит
        // рама», модуль — «сколько умеет РСУ» (тест п.12 Stage2: корабль с
        // MaxAngularSpeed = 1.5 не крутится быстрее, даже с Mk1-РСУ на 6 рад/с).
        if (maxAng > 0f)
            p.MaxAngularSpeed = p.MaxAngularSpeed > 0f
                ? Mathf.Min(p.MaxAngularSpeed, maxAng) : maxAng;

        float boost = BoostMultiplier();
        if (boost > 0f) p.BoostMultiplier = boost;

        p.PilotAssist = PilotAssist;
        // Правило задачи: «падает с таким же ускорением, как набирается».
        // Берём то же число, что и в тяге (после деградации по питанию!) — иначе
        // при нехватке мощности торможение стало бы сильнее разгона и корабль
        // ползал бы к потолку со странным перелётом.
        p.AssistDragAccel = PilotAssist ? thrust * powered : 0f;
        return p;
    }
}
