using System;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Оболочка дебаг-режима (этап 1). Живёт на CanvasLayer поверх игры.
/// Всё дерево UI описано в scenes/DebugLayer.tscn — код только управляет им.
///
/// F3             — показать/скрыть кнопку «Debug Mode» (весь дебаг-режим).
/// клик по кнопке — открыть/закрыть панель (сигнал pressed подключён в сцене
///                  к TogglePanel).
///
/// Открытая панель по умолчанию ставит мир на паузу, чтобы отладку было удобно
/// читать, а кнопки применялись к неподвижному состоянию. Поведение — опция:
/// чекбокс «Pause world» в панели (узел ChkPauseWorld, сигнал toggled → SetPauseWorld).
/// Выключено — мир живёт при открытой панели (нужно для теста тяги и инерции на этапе 2).
///
/// Системы добавляют сюда кнопки и строки отладки через DebugInfoBus
/// (одна строка кода на механику), сам DebugLayer про них ничего не знает.
/// </summary>
public partial class DebugLayer : CanvasLayer
{
    // Узлы берём из сцены (DebugRoot/... в scenes/DebugLayer.tscn).
    private Button _btnDebugMode = null!;
    private PanelContainer _panel = null!;
    private Label _lblDebugInfo = null!;
    private VBoxContainer _buttons = null!;
    private CheckButton _chkPauseWorld = null!;

    /// <summary>Паузу дерева ставим сами — сами и снимаем (чужую паузу не трогаем).</summary>
    private bool _wePausedTheTree;

    /// <summary>Морозить ли мир при открытой панели (чекбокс «Pause world», по умолчанию да).</summary>
    private bool _pauseWorldEnabled = true;

    /// <summary>Видима ли сама кнопка «Debug Mode» (то есть включён ли дебаг-режим).</summary>
    public bool DebugModeVisible { get; private set; }

    /// <summary>Открыта ли панель с кнопками.</summary>
    public bool PanelOpen => _panel.Visible;

    /// <summary>Включена ли опция «паузить мир при открытой панели».</summary>
    public bool PauseWorldEnabled => _pauseWorldEnabled;

    public override void _Ready()
    {
        _btnDebugMode = GetNode<Button>("DebugRoot/BtnDebugMode");
        _panel = GetNode<PanelContainer>("DebugRoot/DebugPanel");
        _lblDebugInfo = GetNode<Label>("DebugRoot/DebugPanel/VBox/LblDebugInfo");
        _chkPauseWorld = GetNode<CheckButton>("DebugRoot/DebugPanel/VBox/ChkPauseWorld");
        _buttons = GetNode<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons");

        // Состояние чекбокса из сцены (button_pressed = true) — источник правды для опции.
        _pauseWorldEnabled = _chkPauseWorld.ButtonPressed;

        DebugInfoBus.Instance?.AttachLayer(this);

        // Тестовая кнопка — приёмка этапа: RegisterDebugButton работает, печатает в консоль.
        RegisterDebugButton("Test", () => GD.Print("debug button pressed"));
    }

    public override void _ExitTree()
    {
        ReleasePause();
        DebugInfoBus.Instance?.DetachLayer(this);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("debug_toggle"))
        {
            ToggleDebugMode();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// F3: показать/скрыть кнопку «Debug Mode». При выключении заодно закрывает панель.
    /// </summary>
    public void ToggleDebugMode()
    {
        DebugModeVisible = !DebugModeVisible;
        _btnDebugMode.Visible = DebugModeVisible;

        if (!DebugModeVisible && _panel.Visible)
            ClosePanel();
    }

    /// <summary>
    /// Открыть/закрыть панель. Метод публичный и подключён к сигналу pressed кнопки
    /// «Debug Mode» связью в сцене (не подпиской в коде).
    /// </summary>
    public void TogglePanel()
    {
        if (_panel.Visible)
            ClosePanel();
        else
            OpenPanel();
    }

    /// <summary>
    /// Включить/выключить опцию «морозить мир при открытой панели». Вызывается
    /// чекбоксом ChkPauseWorld (сигнал toggled из сцены) и из кода. Пауза меняется
    /// сразу, не дожидаясь открытия/закрытия панели.
    /// </summary>
    public void SetPauseWorld(bool enabled)
    {
        _pauseWorldEnabled = enabled;

        // Синхронизируем галочку, если опцию включили/выключили из кода.
        // Присваивание эмитирует toggled → повторный вызов безопасен (idempotent).
        if (_chkPauseWorld.ButtonPressed != enabled)
            _chkPauseWorld.ButtonPressed = enabled;

        if (_panel.Visible)
        {
            if (enabled) ApplyPause();
            else ReleasePause();
        }
    }

    /// <summary>Переключить опцию паузы (для дебаг-кнопки из кода).</summary>
    public void TogglePauseWorld() => SetPauseWorld(!_pauseWorldEnabled);

    private void OpenPanel()
    {
        _panel.Visible = true;
        if (_pauseWorldEnabled)
            ApplyPause();
    }

    private void ClosePanel()
    {
        _panel.Visible = false;
        ReleasePause();
    }

    /// <summary>Паузу ставим только если её никто другой не установил.</summary>
    private void ApplyPause()
    {
        if (!GetTree().Paused)
        {
            GetTree().Paused = true;
            _wePausedTheTree = true;
        }
    }

    /// <summary>Снимаем только собственную паузу — чужую (меню паузы игры) не трогаем.</summary>
    private void ReleasePause()
    {
        if (_wePausedTheTree)
        {
            GetTree().Paused = false;
            _wePausedTheTree = false;
        }
    }

    /// <summary>
    /// Добавить кнопку в панель. Единственный узел, который создаёт код: список кнопок
    /// известен только в рантайме. Работает через DebugInfoBus.RegisterDebugButton.
    /// </summary>
    public Button RegisterDebugButton(string label, Action callback)
    {
        var button = new Button
        {
            Name = $"Btn_{label.Replace(" ", "")}",
            Text = label,
            // Кнопки должны нажиматься и на паузе — как и весь дебаг-слой.
            ProcessMode = ProcessModeEnum.Always,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        button.Pressed += () =>
        {
            try
            {
                callback?.Invoke();
            }
            catch (Exception e)
            {
                GD.PushError($"DebugButton «{label}»: {e}");
            }
        };
        _buttons.AddChild(button);
        return button;
    }

    public override void _Process(double delta)
    {
        UpdateDebugInfo();
    }

    /// <summary>
    /// Строка отладки: собирает блоки всех зарегистрированных источников
    /// (DebugInfoBus.RegisterInfoSource) через перенос строки.
    /// </summary>
    private void UpdateDebugInfo()
    {
        if (!_panel.Visible)
            return;

        var camera = GetViewport().GetCamera3D();
        _lblDebugInfo.Text =
            $"FPS: {Engine.GetFramesPerSecond():F0} | " +
            $"physics ticks: {Engine.GetPhysicsTicksPerSecond()} | " +
            $"gravity: {ProjectSettings.GetSetting("physics/3d/default_gravity").AsDouble()}\n" +
            $"camera: {Format(camera?.GlobalPosition ?? Vector3.Zero)} | " +
            $"world: {(_wePausedTheTree ? "PAUSED" : "running")}\n" +
            DebugInfoBus.BuildInfoText();
    }

    private static string Format(Vector3 v) =>
        $"({v.X:F1}, {v.Y:F1}, {v.Z:F1})";
}
