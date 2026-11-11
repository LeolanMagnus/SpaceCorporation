using Godot;

namespace SpaceCorporation;

/// <summary>
/// eva-plan Блок E (Е1): панель управления у каждого корабля — физический
/// «щит»-мишень для hover-подсказки «{клавиша} сесть за штурвал».
///
/// Это НЕ интерактивный узел: клик по ней не ловится (в headless мыши нет,
/// а 3D-UI-области у панели нет до проекции). Луч из камеры находит форму
/// панели, игрок видит подсказку, а активация идёт по сетевому маршруту
/// `Player.RequestAttach` — тому же, что клавиша F.
///
/// Геометрия: вертикаль = `Player.CapsuleHeight / 4 = 0.45` м (ТЗ: «четверть
/// роста игрока»), ширина/глубина — такой же квадрат (ФЛАГ В27). Потомок
/// hull ⇒ едет вместе с кораблём. Ставится ПЕРЕД креслом `AttachPoints/Seat`
/// и слегка наружу по носу: луч сexterna сначала цепляет панель, потом
/// обшивку — панель всегда передняя по лучу, если игрок на неё смотрит.
///
/// Слой `ControlPanel` (бит 10), маска 0 — ни с чем не сталкивается, только
/// облучается (`EvaRayMask`). `Monitorable` — участвует в query-лучах.
/// Видимости не имеет вовсе (узла-меша нет): в Godot 4 `PhysicsBody3D`
/// участвует в `IntersectRay` независимо от визуала — проверяем пробою
/// Stage7Test п.4 (луч находит панель без единого меша).
///
/// Адрес кресла НЕ дублируем полями: держим ссылку на `AttachPoint` кресла
/// (предпочтение eva-plan — «меньше мест, где адрес можно рассинхронить»);
/// `OwnerNetId`/`NetId` читаем из неё.
/// </summary>
public partial class ControlPanel : Area3D
{
    /// <summary>Вертикаль панели — четверть капсулы игрока (ТЗ, ФЛАГ В27).</summary>
    public const float PanelSizeM = Player.CapsuleHeight / 4f; // 0.45

    /// <summary>Путь к креслу этого корабля (в сцене — сосед `AttachPoints/Seat`).
    /// Через него панель отдаёт сетевой адрес кресла.</summary>
    [Export] public NodePath SeatPath { get; set; } = new NodePath();

    private AttachPoint? _seat;

    /// <summary>Кресло, к «пристёгивает» эта панель (после _Ready).</summary>
    public AttachPoint? Seat => _seat;

    /// <summary>Сетевой адрес владельца (ShipNetId кресла). 0 — кресло ещё
    /// не получило адрес (или ссылка не настроена).</summary>
    public int OwnerNetId => _seat?.OwnerNetId ?? 0;

    /// <summary>Номер точки-кресла внутри владельца (AttachPoint.NetId).</summary>
    public int SeatPointNetId => _seat?.NetId ?? 0;

    public override void _Ready()
    {
        CollisionLayer = CollisionLayers.ControlPanel;
        CollisionMask = 0u;
        // Monitorable=true по умолчанию у PhysicsBody3D: панель участвует в
        // query-лучах (IntersectRay) даже без единого меша и без маски.

        if (string.IsNullOrEmpty(SeatPath.ToString()))
        {
            GD.PrintErr("ControlPanel: SeatPath не задан — панель без кресла, " +
                        "hover будет отказывать");
            return;
        }
        _seat = GetNodeOrNull<AttachPoint>(SeatPath);
        if (_seat == null)
            GD.PrintErr($"ControlPanel: кресло по пути «{SeatPath}» не найдено");
    }
}
