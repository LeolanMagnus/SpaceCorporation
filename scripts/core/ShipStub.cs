using Godot;

namespace SpaceCorporation;

/// <summary>
/// Заглушка корабля (шаг 1.5 этапа 1). Нужна, чтобы проверить невесомость и уже сейчас
/// видеть корабль в debug-info. На этапе 2 этот узел получит ShipController.cs
/// (6DoF-тяга, мышь), здесь — только параметры тела и отчёт в панель.
/// </summary>
public partial class ShipStub : RigidBody3D
{
    public override void _Ready()
    {
        // Параметры из плана: невесомость, отсутствие линейного трения, гашение вращения.
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
            ApplyImpulse(-GlobalTransform.Basis.Z * Mass * 5f);
            GD.Print($"ship nudged, |v| = {LinearVelocity.Length():F2} m/s");
        });
    }
}
