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
    private readonly List<(string Label, Action Callback)> _pendingButtons = new();
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
    /// и выгружаются, когда DebugLayer готов.
    /// </summary>
    public static void RegisterDebugButton(string label, Action callback)
    {
        if (Instance == null)
        {
            GD.PushWarning($"DebugInfoBus: нет сингла, кнопка «{label}» не добавлена.");
            return;
        }

        if (Instance._layer != null)
            Instance._layer.RegisterDebugButton(label, callback);
        else
            Instance._pendingButtons.Add((label, callback));
    }

    /// <summary>Вызывается DebugLayer при готовности: забрать накопленные кнопки.</summary>
    internal void AttachLayer(DebugLayer layer)
    {
        _layer = layer;
        foreach (var (label, callback) in _pendingButtons)
            layer.RegisterDebugButton(label, callback);
        _pendingButtons.Clear();
    }

    /// <summary>Снять кнопку (например, при смене авторитета: не-авторитет не имеет
    /// права на «Nudge Ship», §3.1 п.4). Копия в очереди тоже удаляется.</summary>
    public static void RemoveDebugButton(string label)
    {
        if (Instance == null)
            return;
        Instance._pendingButtons.RemoveAll(b => b.Label == label);
        Instance._layer?.RemoveDebugButton(label);
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
