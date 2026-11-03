using Godot;

namespace SpaceCorporation;

/// <summary>Вид модуля = слот в <see cref="ShipLoadout"/> (Этап 6).</summary>
public enum ModuleKind { Rcs, Engine, Radar, Capacitor, Reactor }

/// <summary>
/// Спецификация одного модуля из каталога (Этап 6). Чистые данные, без узлов:
/// все скоростные характеристики — ЭФФЕКТИВНЫЕ ускорения в м/с² и рад/с²
/// (уже «для корпуса 1200 кг»), поэтому loadout их просто складывает/берёт
/// максимумом и не умножает на массу (Р2 плана: единицы — ускорения).
///
/// PowerDraw/PowerOutput — условные единицы мощности (задел под питание:
/// Powered = Output / Σ Draw, клампится в [0,1] и множит ускорения).
/// RadarRange — метры; в FlightParams не участвует, но живёт в том же
/// loadout'е (один источник характеристик корабля).
/// </summary>
public readonly struct ModuleSpec
{
    public readonly string Id;
    public readonly string Name;
    public readonly ModuleKind Kind;
    public readonly int Tier;             // 1..3 — «улучшение путём замены модуля»

    // Маршевый двигатель (Engine):
    public readonly float ThrustAccel;    // м/с² — тяговое ускорение
    public readonly float MaxLinearSpeed; // м/с  — потолок стрейфа/линейной скорости
    public readonly float BoostGain;      // +к форсажу (BoostMultiplier = 1 + Σ Gain… нет: см. ShipLoadout)

    // РСУ (Rcs):
    public readonly float AngularAccel;    // рад/с²
    public readonly float MaxAngularSpeed; // рад/с

    // Реактор / батарея конденсаторов:
    public readonly float PowerOutput;     // усл. ед.
    public readonly float PowerDraw;       // усл. ед.

    // Радар:
    public readonly float RadarRange;      // м

    public ModuleSpec(string id, string name, ModuleKind kind, int tier,
                      float thrustAccel = 0f, float maxLinearSpeed = 0f, float boostGain = 0f,
                      float angularAccel = 0f, float maxAngularSpeed = 0f,
                      float powerOutput = 0f, float powerDraw = 0f, float radarRange = 0f)
    {
        Id = id; Name = name; Kind = kind; Tier = tier;
        ThrustAccel = thrustAccel; MaxLinearSpeed = maxLinearSpeed; BoostGain = boostGain;
        AngularAccel = angularAccel; MaxAngularSpeed = maxAngularSpeed;
        PowerOutput = powerOutput; PowerDraw = powerDraw; RadarRange = radarRange;
    }
}

/// <summary>
/// Каталог модулей Этапа 6: 5 слотов × 2–3 уровня. Старые Mk I подобраны так,
/// чтобы в сборе дать ровно те числа, что сегодня хардкодит Ship._Ready
/// (75 м/с², 33.33 рад/с², лимит вращения 6 рад/с) — тест п.11 сверяет это
/// побитово. Уровни выше — умножение характеристик: «задел под улучшение».
/// </summary>
public static class ModuleCatalog
{
    private static readonly System.Collections.Generic.Dictionary<string, ModuleSpec> Specs = new()
    {
        // ── Маршевый двигатель: разгон / потолок линейной скорости ────────────
        ["engine_ics_mk1"] = new("engine_ics_mk1", "Engine ICS Mk I", ModuleKind.Engine, 1,
            thrustAccel: 75f, maxLinearSpeed: 260f, boostGain: 0.5f, powerDraw: 2f),
        ["engine_ics_mk2"] = new("engine_ics_mk2", "Engine ICS Mk II", ModuleKind.Engine, 2,
            thrustAccel: 110f, maxLinearSpeed: 340f, boostGain: 0.7f, powerDraw: 3f),
        ["engine_ics_mk3"] = new("engine_ics_mk3", "Engine ICS Mk III", ModuleKind.Engine, 3,
            thrustAccel: 150f, maxLinearSpeed: 420f, boostGain: 1.0f, powerDraw: 4f),

        // ── РСУ: угловое ускорение / потолок вращения ────────────────────────
        // 40000/1200 = 33.333 рад/с² — сегодняшнее число Ship (Р2: делим массу
        // на этом месте НЕ будем, берём готовое ускорение того же размера).
        ["rcs_ics_mk1"] = new("rcs_ics_mk1", "RCS ICS Mk I", ModuleKind.Rcs, 1,
            angularAccel: 40000f / 1200f, maxAngularSpeed: 6f, powerDraw: 1f),
        ["rcs_ics_mk2"] = new("rcs_ics_mk2", "RCS ICS Mk II", ModuleKind.Rcs, 2,
            angularAccel: 60f, maxAngularSpeed: 9f, powerDraw: 1.5f),
        ["rcs_ics_mk3"] = new("rcs_ics_mk3", "RCS ICS Mk III", ModuleKind.Rcs, 3,
            angularAccel: 90f, maxAngularSpeed: 12f, powerDraw: 2f),

        // ── Радар: только Range (на Этап 6 — задел) ──────────────────────────
        ["radar_rn1_mk1"] = new("radar_rn1_mk1", "Radar RN-1", ModuleKind.Radar, 1,
            radarRange: 800f, powerDraw: 1f),
        ["radar_rn2_mk2"] = new("radar_rn2_mk2", "Radar RN-2", ModuleKind.Radar, 2,
            radarRange: 2000f, powerDraw: 1.5f),

        // ── Батарея конденсаторов: прирост форсажа (Boost) ───────────────────
        ["cap_c1_mk1"] = new("cap_c1_mk1", "Capacitor C-1", ModuleKind.Capacitor, 1,
            boostGain: 0.5f, powerDraw: 1f),
        ["cap_c2_mk2"] = new("cap_c2_mk2", "Capacitor C-2", ModuleKind.Capacitor, 2,
            boostGain: 1.0f, powerDraw: 1.5f),

        // ── Реактор: мощность (Powered = Output/Σ Draw) ──────────────────────
        // Mk1 покрывает все Mk1-модули (2+1+1+1 = 5) с запасом.
        ["reactor_rt1_mk1"] = new("reactor_rt1_mk1", "Reactor RT-1", ModuleKind.Reactor, 1,
            powerOutput: 6f),
        ["reactor_rt2_mk2"] = new("reactor_rt2_mk2", "Reactor RT-2", ModuleKind.Reactor, 2,
            powerOutput: 12f),
    };

    /// <summary>Спецификация по id; false + дефолтный spec, если id неизвестен.</summary>
    public static bool TryFind(string id, out ModuleSpec spec) => Specs.TryGetValue(id ?? "", out spec);

    public static ModuleSpec Find(string id)
    {
        if (TryFind(id, out ModuleSpec spec)) return spec;
        GD.PrintErr($"ModuleCatalog: неизвестный модуль '{id}'");
        return default;
    }

    /// <summary>Базовая комплектация = сегодняшние числа Ship (тест п.11).</summary>
    public static string DefaultFor(ModuleKind kind) => kind switch
    {
        ModuleKind.Engine => "engine_ics_mk1",
        ModuleKind.Rcs => "rcs_ics_mk1",
        ModuleKind.Radar => "radar_rn1_mk1",
        ModuleKind.Capacitor => "cap_c1_mk1",
        ModuleKind.Reactor => "reactor_rt1_mk1",
        _ => "",
    };
}
