using System;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Меню радиосвязи (docs/landing-mechanic-plan.md, П2). Появляется В ОКНЕ
/// СТАТУСА (BottomLeft статус-панель HUD) после успешного ответа на радиовызов
/// (сектор «Связь с целью» кругового меню). На время связи статус-панель
/// скрывается, её место занимает панель меню из четырёх строк-опций:
///   • «Опция 1..3» — заглушки будущих команд;
///   • «Запросить посадку» — контекстная: видна только если у цели есть
///     посадочная площадка (проверка типоразмера и запуск процедуры — П3).
/// Закрытие: выбор опции, Esc или ПКМ (отмена).
///
/// Задел на НЕуспешные-отклонённые вызовы: колбэк вызова даёт два исхода
/// (успех → это меню; отказ → только событие в комм-лог, меню не открывается).
/// Контрол строится кодом в _Ready, сцена не нужна — хостится под BottomLeft
/// в HudLayer.tscn.
/// </summary>
public partial class CommlinkMenu : Control
{
    /// <summary>Выбрана строка «Запросить посадку» (П3: хост запускает
    /// процедуру запроса посадки с учётом типоразмера площадки).</summary>
    public Action? OnDockRequested;

    /// <summary>Выбрана строка «Отменить посадку» (П3-правки: хост посылает
    /// серверу отзыв живого разрешения на посадку).</summary>
    public Action? OnDockCancelRequested;

    private const string OptionStub1 = "Опция 1";
    private const string OptionStub2 = "Опция 2";
    private const string OptionStub3 = "Опция 3";
    private const string OptionDock = "Запросить посадку";
    private const string OptionDockCancel = "Отменить посадку";
    private PanelContainer _panel = null!;
    private Label _lblTarget = null!;
    private readonly Label[] _options = new Label[5];
    private int _hovered = -1;

    /// <summary>Открыто ли меню (гейт для HudLayer: Esc/ПКМ, скрытие статуса).</summary>
    public bool IsCommlinkOpen { get; private set; }

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;

        _panel = new PanelContainer { Name = "CommlinkMenuPanel" };
        _panel.AddThemeStyleboxOverride("panel", HudTheme.PanelStyle());
        // Та же геометрическая ниша, что у статус-панели (BottomLeft): нижняя
        // кромка прижата к низу слота, ширина — как у статуса.
        _panel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _panel.OffsetLeft = 0;
        _panel.OffsetRight = 400;
        _panel.OffsetTop = -140;
        _panel.OffsetBottom = 0;
        _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.GrowVertical = Control.GrowDirection.Begin;
        AddChild(_panel);

        var box = new VBoxContainer { Name = "Box" };
        box.AddThemeConstantOverride("separation", 4);
        _panel.AddChild(box);

        _lblTarget = new Label { Name = "Target", Text = "—" };
        _lblTarget.AddThemeColorOverride("font_color", HudTheme.TextAccent);
        box.AddChild(_lblTarget);

        string[] captions = { OptionStub1, OptionStub2, OptionStub3, OptionDock, OptionDockCancel };
        for (int i = 0; i < captions.Length; i++)
        {
            var lbl = new Label { Name = $"Option{i + 1}", Text = captions[i] };
            lbl.AddThemeColorOverride("font_color", HudTheme.TextPrimary);
            box.AddChild(lbl);
            _options[i] = lbl;
        }
    }

    /// <summary>Открыть меню связи с целью (после успешного ответа).
    /// showDockCancelOption (П3-правки) — строка отзыва живого разрешения
    /// на посадку (клавиша 5).</summary>
    public void Open(string targetName, bool showDockOption, bool showDockCancelOption = false)
    {
        _lblTarget.Text = $"Радиосвязь: {targetName}";
        _options[0].Visible = true;
        _options[1].Visible = true;
        _options[2].Visible = true;
        // «Запросить посадку» — только когда у цели есть посадочная площадка.
        _options[3].Visible = showDockOption;
        // «Отменить посадку» — только когда у нашего корабля живое Approved.
        _options[4].Visible = showDockCancelOption;
        _hovered = -1;
        Visible = true;
        IsCommlinkOpen = true;
        MouseFilter = MouseFilterEnum.Stop;
        QueueRedraw();
    }

    /// <summary>Закрыть меню (отмена/выбор опции). Возврат статуса — на хосте
    /// (HudLayer.CloseCommlinkMenu), сам хост закрывает через OnCloseRequested.</summary>
    public void Close()
    {
        Visible = false;
        IsCommlinkOpen = false;
        _hovered = -1;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Запрос закрытия изнутри (ПКМ): хост закрывает общий путь
    /// (меню + возврат статус-панели).</summary>
    public Action? OnCloseRequested;

    private void Choose(int i)
    {
        switch (i)
        {
            case 0:
            case 1:
            case 2:
                HudEventBus.Report($"{_options[i].Text} (радиосвязь): не реализовано");
                break;
            case 3:
                OnDockRequested?.Invoke();
                break;
            case 4:
                OnDockCancelRequested?.Invoke();
                break;
        }
        OnCloseRequested?.Invoke();
    }

    /// <summary>Выбор строки клавишей 1..4 (из HudLayer._UnhandledInput):
    /// только открытое меню и видимая строка.</summary>
    public void ChooseByKey(int index)
    {
        if (!IsCommlinkOpen || index < 0 || index >= _options.Length || !_options[index].Visible)
            return;
        Choose(index);
    }

    /// <summary>Обработчик мыши: наведение строк, ЛКМ — выбор строки,
    /// ПКМ — закрытие.</summary>
    public override void _GuiInput(InputEvent @event)
    {
        if (!IsCommlinkOpen)
            return;
        if (@event is InputEventMouseMotion m)
        {
            int row = RowAtGlobal(m.GlobalPosition);
            if (row != _hovered)
            {
                _hovered = row;
                UpdateHighlight();
            }
        }
        else if (@event is InputEventMouseButton b && b.Pressed)
        {
            if (b.ButtonIndex == MouseButton.Right)
            {
                OnCloseRequested?.Invoke();
                GetViewport().SetInputAsHandled();
            }
            else if (b.ButtonIndex == MouseButton.Left)
            {
                int row = RowAtGlobal(b.GlobalPosition);
                if (row >= 0)
                    Choose(row);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private int RowAtGlobal(Vector2 gp)
    {
        for (int i = 0; i < _options.Length; i++)
        {
            var o = _options[i];
            if (!o.Visible)
                continue;
            var g = o.GetGlobalRect();
            var r = new Rect2(new Vector2(_panel.GlobalPosition.X, g.Position.Y - 2),
                              new Vector2(_panel.GetGlobalRect().Size.X, g.Size.Y + 4));
            if (r.HasPoint(gp))
                return i;
        }
        return -1;
    }

    private void UpdateHighlight()
    {
        for (int i = 0; i < _options.Length; i++)
            _options[i].AddThemeColorOverride("font_color",
                i == _hovered ? HudTheme.TextAccent : HudTheme.TextPrimary);
    }
}
