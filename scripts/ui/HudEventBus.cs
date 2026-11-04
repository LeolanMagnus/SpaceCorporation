using System;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Autoload-сингл (res://scripts/ui/HudEventBus.cs). Шина «событие → комм-лог
/// HUD» (hud-plan.md, Этап H6) по образцу DebugInfoBus: системы не знают про
/// UI, они лишь объявляют событие одним вызовом HudEventBus.Report(...).
///
/// Отличие от DebugInfoBus: здесь нет накопления кнопок — только последние
/// события (кольцевой буфер). HudLayer подписывается на OnEvent и сам решает,
/// сколько строк показывать и когда их затемнять (fade-out через HoldSeconds).
///
/// Публичный API для систем (одна строка на событие):
///   HudEventBus.Report("Docked at Station");
///   HudEventBus.Report($"Target lost: {name}");
/// </summary>
public partial class HudEventBus : Node
{
    public static HudEventBus? Instance { get; private set; }

    /// <summary>Сколько секунд строка живёт полной яркостью, затем гаснет
    /// (fade за FadeSeconds). Критерий H6: померкла через ~8 с.</summary>
    public const double HoldSeconds = 6.0;
    public const double FadeSeconds = 2.0;

    private const int RingSize = 16; //.кольцевой буфер: последние события — для позднего подписчика

    /// <summary>Вызывается на каждое событие (подписка HudLayer). Текст — готовая
    /// строка лога; время события не передаём — HudLayer сам считает возраст.</summary>
    public event Action<string>? OnEvent;

    private readonly string?[] _ring = new string?[RingSize];
    private int _ringCount;
    private int _ringHead;

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    /// <summary>Одно событие в лог (главная точка входа для систем).</summary>
    public static void Report(string message)
    {
        if (Instance == null)
        {
            // Шина не в дереве (headless-тест без Main) — молча пропускаем:
            // событие есть только украшение картинки.
            return;
        }
        Instance.ReportLocal(message);
    }

    /// <summary>Все сохранённые события от старых к новым (для HudLayer:
    /// подписчик, появившийся позже событий, не теряет последние строки).</summary>
    public string[] Snapshot()
    {
        var list = new System.Collections.Generic.List<string>(_ringCount);
        int start = (_ringHead - _ringCount + RingSize) % RingSize;
        for (int i = 0; i < _ringCount; i++)
            list.Add(_ring[(start + i) % RingSize]!);
        return list.ToArray();
    }

    private void ReportLocal(string message)
    {
        _ring[_ringHead] = message;
        _ringHead = (_ringHead + 1) % RingSize;
        if (_ringCount < RingSize)
            _ringCount++;
        OnEvent?.Invoke(message);
    }
}
