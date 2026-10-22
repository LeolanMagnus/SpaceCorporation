using Godot;

namespace SpaceCorporation;

/// <summary>
/// Корабль игрока (шаг 1.5 этапа 1). Сцена scenes/Ship.tscn.
///
/// Параметры из плана: невесомость, отсутствие линейного трения, гашение вращения.
/// На этапе 2 этот класс получит ShipController-логику (6DoF-тяга, мышь).
///
/// AttachPoints — узлы, к которым может быть пристёгнут игрок (см. Player.AttachTo).
/// Именно поэтому точка сиденья лежит внутри корабля: игрок, сидящий в кабине,
/// физически является частью иерархии корабля и летит вместе с ним.
/// </summary>
public partial class Ship : RigidBody3D
{
    public override void _Ready()
    {
        GravityScale = 0f;
        LinearDamp = 0f;
        AngularDamp = 1.5f;
        // CustomIntegrator = true включится на этапе 5 (пружина стыковки в _IntegrateForces).

        CollisionLayer = CollisionLayers.Ship;
        CollisionMask = CollisionLayers.ShipMask;

        DebugInfoBus.RegisterInfoSource("Ship", () =>
            $"ship: |v| = {LinearVelocity.Length():F2} m/s, " +
            $"|w| = {AngularVelocity.Length():F2} rad/s, " +
            $"mass = {Mass:F1}");

        // Кнопка для проверки инерции: короткий импульс вперёд, дальше тело летит само.
        DebugInfoBus.RegisterDebugButton("Nudge Ship", () =>
        {
            var impulse = -GlobalTransform.Basis.Z * Mass * 5f;
            ApplyImpulse(impulse);
            // Скорость обновится только на следующем шаге физики, поэтому печатаем импульс.
            GD.Print($"ship nudged, impulse = {impulse.Length():F1} Н*с (ожидается |v| ≈ 5 м/с)");
        });
    }
}
