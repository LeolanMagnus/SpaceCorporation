using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Комплектация скафандра (eva-plan, Б3): 4 слота по одному на вид
/// (<see cref="SuitModuleKind"/>) — копия структуры <see cref="ShipLoadout"/> в
/// миниатюре. Модули НЕ имеют цен и деревьев (eva-plan §6): это только структура
/// + мост в <see cref="SuitState"/> и <see cref="SuitModel"/>.
///
/// Единственная функция, реально влияющая на геймплей сейчас: потолок реактивной
/// массы (сумма MassTotal по слотам). Остальное (надбавки расхода) — задел,
/// применяемый той же сборкой формул, что и будущая стадия апгрейдов.
/// </summary>
public class SuitLoadout
{
    private readonly Dictionary<SuitModuleKind, string> _slots = new();

    public SuitLoadout()
    {
        foreach (SuitModuleKind k in System.Enum.GetValues<SuitModuleKind>())
            _slots[k] = SuitModuleCatalog.DefaultFor(k);
    }

    /// <summary>Id модуля в слоте ("" = пусто).</summary>
    public string Get(SuitModuleKind kind) => _slots.TryGetValue(kind, out string id) ? id : "";

    /// <summary>Поставить модуль в слот его вида ("" — вынуть). false, если id
    /// неизвестен или вид не совпадает со слотом.</summary>
    public bool EquipKind(SuitModuleKind kind, string? id)
    {
        if (string.IsNullOrEmpty(id)) { _slots[kind] = ""; return true; }
        if (!SuitModuleCatalog.TryFind(id, out SuitModuleSpec spec) || spec.Kind != kind) return false;
        _slots[kind] = id;
        return true;
    }

    public bool Equip(string? id)
    {
        if (!SuitModuleCatalog.TryFind(id, out SuitModuleSpec spec)) return false;
        _slots[spec.Kind] = id;
        return true;
    }

    public SuitModuleSpec Spec(SuitModuleKind kind) =>
        SuitModuleCatalog.TryFind(Get(kind), out SuitModuleSpec spec) ? spec : default;

    /// <summary>Потолок реактивной массы — сумма MassTotal по слотам, %
    /// (пустой слот Jetpack = 0: джетпак без бака молчит, см. SuitModel.BurnFuel).</summary>
    public float MassTotal()
    {
        float sum = 0f;
        foreach (string id in _slots.Values)
            if (SuitModuleCatalog.TryFind(id, out SuitModuleSpec s)) sum += s.MassTotal;
        return sum;
    }

    /// <summary>Σ надбавок расхода батареи, %/мин (относительно базового
    /// комплекта). Базовая комплектация даёт ровно 0 — сегодняшние числа
    /// SuitModel не меняются (инвариант теста п.2 Блока B).</summary>
    public float PowerDrawDeltaPerMin()
    {
        float sum = 0f;
        foreach (string id in _slots.Values)
            if (SuitModuleCatalog.TryFind(id, out SuitModuleSpec s)) sum += s.DrawPerMin;
        return sum;
    }

    /// <summary>Итоговый базовый расход батареи c учётом модулей, %/мин:
    /// константы SuitModel (обогрев 4 + свет 12, если включён) + надбавки
    /// слотов. Базовая комплектация = ровно сегодняшние числа SuitModel.</summary>
    public float PowerDrawPerMinWithBase() => PowerDrawPerMinWithBase(false);

    public float PowerDrawPerMinWithBase(bool lightOn)
    {
        float baseRate = SuitModel.HeatDrawPerMin + (lightOn ? SuitModel.LightDrawPerMin : 0f);
        return baseRate + PowerDrawDeltaPerMin();
    }

    /// <summary>Маска активных модулей для <see cref="SuitState.ActiveSuitModules"/>
    /// (её возит сеть, Блок D).</summary>
    public uint ActiveMask()
    {
        uint mask = 0u;
        foreach (KeyValuePair<SuitModuleKind, string> kv in _slots)
            if (!string.IsNullOrEmpty(kv.Value)) mask |= SuitModules.BitOf(kv.Key);
        return mask;
    }

    /// <summary>
    /// Собрать начальное состояние скафандра из комплекта: батарея полная,
    /// обогрев включён (ТЗ «постоянно»), реактивная масса = потолок комплекта
    /// (базовый Jetpack Mk I = 100 % — ровно сегодняшние константы).
    /// </summary>
    public SuitState Build()
    {
        SuitState s = SuitState.Fresh();
        float mass = MassTotal();
        s.Fuel = Mathf.Clamp(mass, 0f, SuitModel.MassFuelTotal);
        s.ActiveSuitModules = ActiveMask();
        SuitModel.UpdateWarnings(ref s);
        return s;
    }
}
