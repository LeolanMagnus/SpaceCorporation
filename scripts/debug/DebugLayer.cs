using System;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Оболочка дебаг-режима (этап 1). Живёт на CanvasLayer поверх игры.
///
/// F3            — показать/скрыть кнопку «Debug Mode» (весь дебаг-режим).
/// клик по кнопке — открыть/закрыть панель; открытая панель ставит дерево на паузу.
///
/// Системы добавляют сюда кнопки и строки отладки через DebugInfoBus
/// (одна строка кода на механику), сам DebugLayer про них ничего не знает.
/// </summary>
public partial class DebugLayer : CanvasLayer
{
    private const string ActionToggle = "debug_toggle";

    private Button _btnDebugMode = null!;
    private PanelContainer _panel = null!;
    private Label _lblDebugInfo = null!;
    private VBoxContainer _buttons = null!;

    /// <summary>Паузу дерева ставим сами — сами и снимаем (чужую паузу не трогаем).</summary>
    private bool _wePausedTheTree;

    /// <summary>Видима ли сама кнопка «Debug Mode» (то есть включён ли дебаг-режим).</summary>
    public bool DebugModeVisible { get; private set; }

    /// <summary>Открыта ли панель с кнопками.</summary>
    public bool PanelOpen => _panel.Visible;

    public override void _Ready()
    {
        Layer = 10;
        ProcessMode = ProcessModeEnum.Always;

        BuildUi();
        DebugInfoBus.Instance?.AttachLayer(this);

        // Тестовая кнопка — приёмка этапа: RegisterDebugButton работает, печатает в консоль.
        RegisterDebugButton("Test", () => GD.Print("debug button pressed"));
    }

    public override void _ExitTree()
    {
        DebugInfoBus.Instance?.DetachLayer(this);
    }

    // --------------------------------------------------------------------- UI

    private void BuildUi()
    {
        var root = new Control
        {
            Name = "DebugRoot",
            // Растягиваем на весь экран, но клики не перехватываем — только свои контролы.
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ProcessMode = ProcessModeEnum.Always,
        };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        // Кнопка «Debug Mode»: левый верхний угол, зелёный текст, изначально скрыта.
        _btnDebugMode = new Button
        {
            Name = "BtnDebugMode",
            Text = "Debug Mode",
            Visible = false,
            ProcessMode = ProcessModeEnum.Always,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _btnDebugMode.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _btnDebugMode.Position = new Vector2(8, 8);
        _btnDebugMode.AddThemeColorOverride("font_color", new Color(0.2f, 1.0f, 0.35f));
        _btnDebugMode.AddThemeColorOverride("font_hover_color", new Color(0.5f, 1.0f, 0.6f));
        _btnDebugMode.AddThemeColorOverride("font_pressed_color", new Color(0.1f, 0.8f, 0.25f));
        _btnDebugMode.Pressed += TogglePanel;
        root.AddChild(_btnDebugMode);

        // Панель: PanelContainer + VBoxContainer с Label отладки и контейнером кнопок.
        _panel = new PanelContainer
        {
            Name = "DebugPanel",
            Visible = false,
            ProcessMode = ProcessModeEnum.Always,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _panel.Position = new Vector2(8, 44);
        _panel.CustomMinimumSize = new Vector2(320, 0);
        root.AddChild(_panel);

        var vbox = new VBoxContainer { Name = "VBox" };
        _panel.AddChild(vbox);

        _lblDebugInfo = new Label
        {
            Name = "LblDebugInfo",
            Text = string.Empty,
            ProcessMode = ProcessModeEnum.Always,
            // Моноширинный шрифт — цифры не «прыгают» между кадрами.
        };
        _lblDebugInfo.AddThemeColorOverride("font_color", new Color(0.85f, 0.9f, 0.95f));
        vbox.AddChild(_lblDebugInfo);

        vbox.AddChild(new HSeparator { Name = "Separator" });

        _buttons = new VBoxContainer { Name = "Buttons" };
        vbox.AddChild(_buttons);
    }

    /// <summary>
    /// Публичный API: добавить кнопку в дебаг-панель одной строкой.
    /// Возвращает созданную кнопку — можно подписаться дополнительно или переименовать.
    /// </summary>
    public Button RegisterDebugButton(string label, Action callback)
    {
        var button = new Button
        {
            Name = label,
            Text = label,
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
                GD.PushError($"Debug button «{label}» упал: {e}");
            }
        };
        _buttons.AddChild(button);
        return button;
    }

    // ------------------------------------------------------------------ ввод

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!@event.IsActionPressed(ActionToggle))
            return;

        ToggleDebugMode();
        GetViewport().SetInputAsHandled();
    }

    private void ToggleDebugMode()
    {
        DebugModeVisible = !DebugModeVisible;
        _btnDebugMode.Visible = DebugModeVisible;

        // Выключили дебаг-режим — заодно закрываем панель и снимаем паузу.
        if (!DebugModeVisible && PanelOpen)
            ClosePanel();
    }

    private void TogglePanel()
    {
        if (PanelOpen)
            ClosePanel();
        else
            OpenPanel();
    }

    private void OpenPanel()
    {
        _panel.Visible = true;
        // Пока меню открыто — мир стоит, но UI (ProcessMode = Always) реагирует на клики.
        // Паузу снимаем только если сами её поставили: позже появится меню паузы игры,
        // и закрывать чужую паузу дебаг-панель не имеет права.
        if (!GetTree().Paused)
        {
            GetTree().Paused = true;
            _wePausedTheTree = true;
        }
    }

    private void ClosePanel()
    {
        _panel.Visible = false;
        if (_wePausedTheTree)
        {
            GetTree().Paused = false;
            _wePausedTheTree = false;
        }
    }

    // ------------------------------------------------------------ debug info

    public override void _Process(double delta)
    {
        UpdateDebugInfo();
    }

    /// <summary>
    /// Обновляет строку «Debug Info». Текст собирает DebugInfoBus из блоков,
    /// которые объявили системы; сам DebugLayer их содержимого не знает.
    /// </summary>
    private void UpdateDebugInfo()
    {
        if (!PanelOpen)
            return; // на закрытой панели Label невидим — не тратим кадры

        var engine = Engine.GetFramesPerSecond();
        var camera = GetViewport().GetCamera3D();
        var cameraPos = camera != null ? camera.GlobalPosition : Vector3.Zero;

        _lblDebugInfo.Text =
            $"FPS: {engine}\n" +
            $"gravity: 0.0 (невесомость)\n" +
            $"camera: {Fmt(cameraPos)}\n" +
            $"paused: {GetTree().Paused}\n" +
            DebugInfoBus.BuildInfoText();
    }

    private static string Fmt(Vector3 v) =>
        $"({v.X:F1}, {v.Y:F1}, {v.Z:F1})";
}
