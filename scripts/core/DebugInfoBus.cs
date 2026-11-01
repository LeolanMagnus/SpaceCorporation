using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Autoload-сингл (res://scripts/core/DebugInfoBus.cs). Шина между игровыми системами
/// и дебаг-интерфейсом: системы не знают про UI, они лишь объявляют
/// «у меня есть кнопка» и «у меня есть строка отладки».
///
/// Публичный API для систем (по одной строке на механику):
///   DebugInfoBus.RegisterDebugButton("Spawn Target", SpawnTarget);
///   DebugInfoBus.RegisterInfoSource("Ship", () => $"vel = {LinearVelocity.Length():F2}");
/// </summary>
public partial class DebugInfoBus : Node
{
    public static DebugInfoBus? Instance { get; private set; }

    private readonly Dictionary<string, Func<string>> _infoSources = new();
    private readonly List<DebugButtonHandle> _pendingButtons = new();
    // Все зарегистрированные (и ещё не снятые) кнопки, включая уже выгруженные в слой:
    // нужно для HasDebugButton — проверка «есть ли кнопка» не зависит от готовности UI
    // (§4 шаг 4.9 п.23: у клиента кнопка «Toggle God Mode» не регистрируется вовсе).
    private readonly List<DebugButtonHandle> _activeButtons = new();
    private DebugLayer? _layer;

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    // ------------------------------------------------------------------ кнопки

    /// <summary>
    /// Регистрирует кнопку в дебаг-панели. Работает и до появления UI — запросы копятся
    /// и выгружаются, когда DebugLayer готов. Возвращает ручку: снятие по ней убирает
    /// ровно ЭТУ кнопку (или её отложенную заявку), не задев чужую с той же меткой.
    /// </summary>
    public static DebugButtonHandle? RegisterDebugButton(string label, Action callback)
    {
        if (Instance == null)
        {
            GD.PushWarning($"DebugInfoBus: нет сингла, кнопка «{label}» не добавлена.");
            return null;
        }

        var handle = new DebugButtonHandle(label, callback);
        Instance._activeButtons.Add(handle); // учитываем для HasDebugButton независимо от UI
        if (Instance._layer != null)
            handle.Button = Instance._layer.RegisterDebugButton(label, callback);
        else
            Instance._pendingButtons.Add(handle); // слой ещё не готов — заявка до готовности
        return handle;
    }

    /// <summary>Зарегистрирована ли кнопка с такой подпиской (логически, без учёта
    /// готовности UI). Сервер-авторитет зарегистрировал → true; клиент-не-авторитет
    /// не регистрировал → false (шаг 4.9 п.23).</summary>
    public static bool HasDebugButton(string label)
    {
        if (Instance == null)
            return false;
        foreach (var handle in Instance._activeButtons)
            if (!handle.Removed && handle.Label == label)
                return true;
        return false;
    }

    /// <summary>Сколько ЖИВЫХ кнопок с такой подпиской. Шов п.23: мирные корабли
    /// сервера уже зарегистрировали «Toggle God Mode», поэтому «у клиента кнопки
    /// нет» проверяем НЕ отсутствием метки вообще, а тем, что добавление
    /// НЕ-авторитетного корабля НЕ увеличило счётчик этой кнопки (он регистрирует
    /// «Toggle Interpolation»). Счёт по метке, а не bool.</summary>
    public static int CountDebugButtons(string label)
    {
        if (Instance == null)
            return 0;
        int n = 0;
        foreach (var handle in Instance._activeButtons)
            if (!handle.Removed && handle.Label == label)
                n++;
        return n;
    }

    /// <summary>Подписки всех живых зарегистрированных кнопок через запятую — для
    /// assert'ов «в списке нет «Toggle God Mode» у клиента» (п.23).</summary>
    public static string ListDebugButtonLabels()
    {
        if (Instance == null)
            return string.Empty;
        var labels = new List<string>();
        foreach (var handle in Instance._activeButtons)
            if (!handle.Removed && !labels.Contains(handle.Label))
                labels.Add(handle.Label);
        return string.Join(", ", labels);
    }

    /// <summary>Вызывается DebugLayer при готовности: забрать накопленные кнопки.</summary>
    internal void AttachLayer(DebugLayer layer)
    {
        _layer = layer;
        foreach (var handle in _pendingButtons)
            if (!handle.Removed)
                handle.Button = layer.RegisterDebugButton(handle.Label, handle.Callback);
        _pendingButtons.Clear();
    }

    /// <summary>Снять кнопку по метке (например, при смене авторитета: не-авторитет не
    /// имеет права на «Nudge Ship», §3.1 п.4). Убирает первую кнопку с таким именем и
    /// копию в очереди. Копия в очереди тоже удаляется.</summary>
    public static void RemoveDebugButton(string label)
    {
        if (Instance == null)
            return;
        // Помечаем снятыми все живые ручки с этой меткой — иначе HasDebugButton
        // врал бы после снятия (шаг 4.9 п.23: «кнопки нет» должно быть видно).
        foreach (var handle in Instance._activeButtons)
            if (handle.Label == label)
                handle.Removed = true;
        Instance._pendingButtons.RemoveAll(b => b.Label == label);
        Instance._layer?.RemoveDebugButton(label);
    }

    /// <summary>Снять конкретную кнопку по ручке (полученной из RegisterDebugButton).
    /// Владелец снимает ровно свою кнопку, не задев чужую с той же меткой — надёжно при
    /// смене роли, когда старый и новый игрок живут в одном кадре, и когда кнопка была
    /// зарегистрирована до готовности слоя (§3.11 п.9).</summary>
    public static void RemoveDebugButton(DebugButtonHandle? handle)
    {
        if (Instance == null || handle == null)
            return;
        handle.Removed = true;
        Instance._pendingButtons.Remove(handle);          // ещё не создана — убрать заявку
        if (handle.Button != null && GodotObject.IsInstanceValid(handle.Button))
            handle.Button.Free();                          // уже создана — убрать кнопку
    }

    internal void DetachLayer(DebugLayer layer)
    {
        if (ReferenceEquals(_layer, layer))
            _layer = null;
    }

    // ------------------------------------------------------------- debug info

    /// <summary>
    /// Источник текста для панели. Повторная регистрация по тому же ключу заменяет блок,
    /// поэтому системы могут спокойно перерегистрироваться при рестарте.
    /// </summary>
    public static void RegisterInfoSource(string key, Func<string> provider)
    {
        if (Instance == null)
        {
            GD.PushWarning($"DebugInfoBus: нет сингла, источник «{key}» не добавлен.");
            return;
        }

        Instance._infoSources[key] = provider;
    }

    public static void RemoveInfoSource(string key)
    {
        if (Instance != null)
            Instance._infoSources.Remove(key);
    }

    /// <summary>Есть ли источник с таким ключом (для assert'ов и отладки: ключ —
    /// часть контракта, например «Ship#&lt;ShipNetId&gt;», §3.1 п.6).</summary>
    public static bool HasInfoSource(string key) =>
        Instance != null && Instance._infoSources.ContainsKey(key);

    // ------------------------------------------------------- журнал событий

    // Короткие событийные строки (spawn, смены ролей) — последние N строк живут в
    // панели как отдельный источник «Log»: без них события сети невидимы, а гонять
    // каждый ивент отдельным RegisterInfoSource — плодить ключи (§3.11 п.9).
    private readonly Queue<string> _logLines = new();
    private const int LogCapacity = 6;

    /// <summary>Одноразовая строка в панель (и в консоль). Не заменяет GD.PrintErr
    /// для ошибок — это именно видимая в UI лента событий.</summary>
    public static void Report(string line)
    {
        GD.Print($"[net] {line}");
        if (Instance == null)
            return;
        Instance._logLines.Enqueue(line);
        while (Instance._logLines.Count > LogCapacity)
            Instance._logLines.Dequeue();
        if (!Instance._infoSources.ContainsKey("Log"))
            Instance._infoSources["Log"] = Instance.BuildLog;
    }

    private string BuildLog() => string.Join("\n", _logLines);

    /// <summary>Собирает все блоки текста через перенос строки.</summary>
    public static string BuildInfoText()
    {
        if (Instance == null)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var (key, provider) in Instance._infoSources)
        {
            string text;
            try
            {
                text = provider.Invoke();
            }
            catch (Exception e)
            {
                text = $"{key}: ERROR {e.Message}";
                GD.PushError($"DebugInfoBus: источник «{key}» упал: {e}");
            }

            if (string.IsNullOrEmpty(text))
                continue;

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(text);
        }

        return sb.ToString();
    }
}

/// <summary>
/// Ручка на дебаг-кнопку. Владелец (Player/Ship) получает её из
/// DebugInfoBus.RegisterDebugButton и снимает кнопку по ней в _ExitTree. Работает и
/// когда кнопка ещё не создана (зарегистрирована до готовности DebugLayer): снятие
/// убирает отложенную заявку, а не только готовый Button — иначе при Offline→Host
/// кнопка старого игрока оставалась бы сиротой с колбэком на освобождённый узел
/// (§3.11 п.9).
/// </summary>
public sealed class DebugButtonHandle
{
    internal string Label { get; }
    internal Action Callback { get; }
    internal Button? Button;
    internal bool Removed;

    internal DebugButtonHandle(string label, Action callback)
    {
        Label = label;
        Callback = callback;
    }
}
