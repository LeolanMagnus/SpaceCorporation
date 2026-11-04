using Godot;
using System;
using System.Collections.Generic;
using SpaceCorporation.Net;
using SpaceCorporation.UI;

namespace SpaceCorporation.Tests;

/// <summary>
/// Вспомогательные проверки Stage6Test — отдельный partial того же драйвера,
/// чтобы основной файл остался картой фаз (образец Stage5Test.Helpers.cs).
/// </summary>
public partial class Stage6Test
{
    // Общая для проверок собранная пачка контактов и спавн-мишени.
    private readonly List<RadarContact> _contacts = new(32);
    private TargetState? _nearTarget;   // мишень ~1200 м (в радиусе)
    private TargetState? _farTarget;    // мишень ~2500 м (за 1.1R — не входит)
    private Ship? _own;
    private int _ownNetId;

    // ──────────────── п.1: свой корабль — центр радара ────────────────

    private void CheckOwnShipAndCenter()
    {
        _own = _source!.OwnShipForTest();
        Check(_own != null, "п.1: OwnShipForTest() — свой корабль определён");
        if (_own == null) return;
        Check(_own.Net != null && _own.Net.ShipNetId != 0,
              "п.1: у своего корабля есть ShipNetId");
        _ownNetId = _own.Net?.ShipNetId ?? 0;
        Check(_own.Net?.FactionValue == (byte)Faction.Player,
              $"п.1: фракция своего корабля Player (получено {_own.Net?.FactionValue})");
        Check(MathF.Abs(_source.Model.Range - RadarModel.DefaultRange) < 0.01f,
              $"п.1: радиус модели по умолчанию 2000 (получено {_source.Model.Range})");
        GD.Print($"INFO п.1: own=#{_ownNetId} at {_own.GlobalPosition}");
    }

    // ──────────────── п.2: горячая клавиша радарa ────────────────

    private void CheckHotkeyRegistered()
    {
        Check(InputMap.HasAction("hud_radar"), "п.2: действие hud_radar есть в InputMap");
    }

    // ──────────── п.3: спавн мишени — NetPosition в снимке ────────────

    private void SpawnNearbyTarget()
    {
        if (_own == null) { Check(false, "п.3: свой корабль для спавна мишени"); return; }

        // Мишень «впереди по +z» от своего корабля: 1200 м — внутри 0.9R=1800.
        Vector3 nearPos = _own.GlobalPosition + new Vector3(0f, 0f, 1200f);
        _nearTarget = _spawner!.SpawnTarget(nearPos);
        Check(_nearTarget != null, "п.3: SpawnTarget(1200 м) вернул TargetState");
        if (_nearTarget != null)
        {
            // Правка WorldSpawner: поза пишется в буфер состояния ДО AddChild —
            // иначе на пире (и в оффлайне) мишень для радара в (0,0,0).
            float err = (_nearTarget.NetPosition - nearPos).Length();
            Check(err < 1f, $"п.3: TargetState.NetPosition == позиция спавна (Δ={err:0.###})");
            Check(NetworkManager.Instance!.FindTarget(_nearTarget.TargetNetId) == _nearTarget,
                  $"п.3: мишень #{_nearTarget.TargetNetId} в реестре Targets");
            Check(_nearTarget.FactionValue == (byte)Faction.Hostile,
                  "п.3: фракция мишени Hostile");
        }

        // Далёкая мишень: 2500 м > 1.1R=2200 — на радаре её быть не должно.
        Vector3 farPos = _own.GlobalPosition + new Vector3(2500f, 0f, 0f);
        _farTarget = _spawner.SpawnTarget(farPos);
        Check(_farTarget != null, "п.3: SpawnTarget(2500 м) вернул TargetState");
    }

    // ───────────── п.4: CollectContacts — состав и RelPos ─────────────

    private void CheckCollectContacts()
    {
        _source!.CollectContacts(_contacts);

        Check(_contacts.TrueForAll(c => c.NetId != _ownNetId),
              "п.4: свой корабль ИСКЛЮЧЁН из контактов");

        // Другой корабль (не свой — свой RadarSource исключает): адрес и
        // RelPos = разница репликованных поз. Хост-игрок автопристёгивается к
        // ближнему сиденью, им может оказаться и Ship, и Ship2, поэтому
        // выбираем корабль из реестра, чей узел != свой.
        Ship? other = null;
        foreach (var (id, s) in NetworkManager.Instance!.Ships)
            if (!ReferenceEquals(s, _own)) { other = s; break; }
        Check(other != null && other.Net != null, "п.4: в мире есть второй корабль (не свой)");
        var ship2 = other;
        if (ship2?.Net != null)
        {
            RadarContact cs = default;
            bool found = false;
            foreach (var c in _contacts)
                if (c.NetId == ship2.Net.ShipNetId) { cs = c; found = true; break; }
            Check(found, "п.4: Ship2 присутствует в контактах");
            if (found)
            {
                Vector3 want = ship2.Net.NetPosition - _own!.GlobalPosition;
                float err = new Vector3(cs.RelPos.X - want.X, cs.RelPos.Y - want.Y,
                                        cs.RelPos.Z - want.Z).Length();
                Check(err < 1f, $"п.4: RelPos Ship2 = NetPosition - центр (Δ={err:0.###})");
                Check(cs.Shape == RadarShape.Ship, "п.4: форма Ship2 — Ship (точка)");
                Check(cs.Faction == (byte)Faction.Player,
                      "п.4: фракция Ship2 — Player (своя, из NetState)");
            }
        }

        if (_nearTarget != null)
        {
            RadarContact ct = default;
            bool found = false;
            foreach (var c in _contacts)
                if (c.NetId == _nearTarget.TargetNetId) { ct = c; found = true; break; }
            Check(found, "п.4: мишень (1200 м) присутствует в контактах");
            if (found)
            {
                float d = MathF.Sqrt(ct.RelPos.LengthSquared);
                Check(MathF.Abs(d - 1200f) < 2f,
                      $"п.4: |RelPos| мишени ≈ 1200 м (получено {d:0.#})");
                Check(ct.Shape == RadarShape.Target, "п.4: форма мишени — Target (ромб)");
                Check(ct.Faction == (byte)Faction.Hostile, "п.4: фракция мишени — Hostile");
            }
        }
    }

    // ─────────── п.5: Sync + Advance — criterion «≥ 1 блип» ───────────

    private void CheckModelBlips()
    {
        var m = _source!.Model;
        m.Sync(_contacts);
        m.Advance(1f / 60f, 0f);

        Check(m.LiveCount >= 1, $"п.5: критерий R2 — LiveCount ≥ 1 (получено {m.LiveCount})");
        Check(m.LiveCount >= 2, $"п.5: Ship2 + мишень + станция ≥ 2 блипов (получено {m.LiveCount})");
        Check(m.FindBlip(_ownNetId) == null, "п.5: своего корабля среди блипов нет");
        if (_nearTarget != null)
        {
            var b = m.FindBlip(_nearTarget.TargetNetId);
            Check(b != null, "п.5: FindBlip(мишень) != null");
            if (b != null)
            {
                Check(MathF.Abs(b.Contact.Distance - 1200f) < 2f,
                      $"п.5: дистанция блипа мишени ≈ 1200 (получено {b.Contact.Distance:0.#})");
                Check(!b.Stale && b.Fade > 0.99f, "п.5: блип живой (не Stale, Fade≈1)");
            }
        }
        // Проекция при yaw=0: forward=(0,1) → ScreenY = RelPos.Z, ScreenX = RelPos.X.
        if (_nearTarget != null && m.FindBlip(_nearTarget.TargetNetId) is { } bb)
        {
            Check(MathF.Abs(bb.ScreenY - 1200f) < 2f && MathF.Abs(bb.ScreenX) < 2f,
                  $"п.5: проекция yaw=0: ScreenY≈+1200, ScreenX≈0 (получено {bb.ScreenX:0.#},{bb.ScreenY:0.#})");
        }
    }

    // ──────────────── п.6: станция — square/Neutral ────────────────

    private void CheckStationBlip()
    {
        var nm = NetworkManager.Instance!;
        int stationId = 0;
        foreach (var (id, st) in nm.Stations)
        { stationId = id; _ = st; break; }
        Check(stationId != 0, "п.6: станция TestStand в реестре Stations");
        if (stationId == 0) return;

        RadarContact cs = default;
        bool found = false;
        foreach (var c in _contacts)
            if (c.NetId == stationId) { cs = c; found = true; break; }
        Check(found, "п.6: станция в контактах");
        if (found)
        {
            Check(cs.Shape == RadarShape.Station, "п.6: форма станции — Station (квадрат)");
            Check(cs.Faction == (byte)Faction.Neutral,
                  "п.6: фракция станции — Neutral (поля фракции у станции нет)");
        }
        Check(_source!.Model.FindBlip(stationId) != null, "п.6: блип станции в модели");
    }

    // ─────────────── п.7: флаг цели (TargetingService) ───────────────

    private void CheckTargetFlag()
    {
        if (_nearTarget == null) { Check(false, "п.7: мишень для флага цели"); return; }
        var targeting = _main!.GetNodeOrNull<TargetingService>("HudLayer/TargetingService");
        Check(targeting != null, "п.7: TargetingService смонтирован в HudLayer");
        if (targeting == null) return;

        bool sel = targeting.Select(_nearTarget.TargetNetId);
        Check(sel, $"п.7: Select(мишень #{_nearTarget.TargetNetId}) -> true");

        _source!.CollectContacts(_contacts); // перезабор с флагом
        bool flagged = false;
        foreach (var c in _contacts)
            if (c.NetId == _nearTarget.TargetNetId) flagged = c.IsTarget;
        Check(flagged, "п.7: контакт цели отмечен IsTarget");

        targeting.Clear();
        _source.CollectContacts(_contacts);
        bool stillFlagged = false;
        foreach (var c in _contacts)
            if (c.NetId == _nearTarget.TargetNetId) stillFlagged = c.IsTarget;
        Check(!stillFlagged, "п.7: после Clear() флаг цели снят");
    }

    // ─────────────── п.8: далёкая мишень за порогом входа ───────────────

    private void CheckFarTargetExcluded()
    {
        if (_farTarget == null) { Check(false, "п.8: далёкая мишень"); return; }
        var m = _source!.Model; // Range всё ещё 2000: вход нового — 0.9R = 1800
        m.Sync(_contacts);
        m.Advance(1f / 60f, 0f);
        Check(m.FindBlip(_farTarget.TargetNetId) == null,
              $"п.8: мишень 2500 м > 0.9R не входит (#{_farTarget.TargetNetId})");
        Check(m.FindBlip(_nearTarget?.TargetNetId ?? 0) != null,
              "п.8: ближняя мишень осталась в блипах");
    }

    // ──── п.9: сужение радиуса → Stale+fade; экстраполяция RelVel ────

    private void CheckHysteresisAndExtrapolation()
    {
        var m = _source!.Model;

        // Сужаем радиус до 500: ближняя мишень (1200) > 1.1R=550 → вне сборки.
        m.Range = 500f;
        m.Sync(_contacts);
        m.Advance(0.1f, 0f); // fade 0.1/0.5 = 0.2 → блип ещё виден, но Stale
        if (_nearTarget != null)
        {
            var b = m.FindBlip(_nearTarget.TargetNetId);
            Check(b != null && b.Stale && b.Fade < 0.9f && b.Fade > 0f,
                  $"п.9: при сужении R блип стал Stale и гаснет (Fade={b?.Fade:0.##})");
            m.Advance(1f, 0f); // ещё 1 с > FadeTime → погас и удалён
            Check(m.FindBlip(_nearTarget.TargetNetId) == null,
                  "п.9: погасший блип удалён из кэша");
        }

        // Ship2 (~16 м) и станция (~408 м): при R=500 (выход 1.1R=550) держатся.
        m.Sync(_contacts);
        m.Advance(0.016f, 0f);
        var ship2 = WorldSpawner.Current!.FindShipByName("Ship2");
        Check(ship2?.Net != null && m.FindBlip(ship2.Net.ShipNetId) != null,
              "п.9: гистерезис: Ship2 (16 м) держится в R=500");

        // Экстраполяция: синтетический контакт с RelVel=(10,0,0), за 1 с
        // RelPos.X: 100 → 110; при yaw=0 ScreenX = RelPos.X.
        var m2 = new RadarModel { Range = 1000f };
        var synthetic = new List<RadarContact>
        {
            new() { NetId = 999, RelPos = new Vec3(100f, 0f, 0f), RelVel = new Vec3(10f, 0f, 0f) },
        };
        m2.Sync(synthetic);
        m2.Advance(1f, 0f);
        var sb = m2.FindBlip(999);
        Check(sb != null && MathF.Abs(sb.Contact.RelPos.X - 110f) < 0.5f,
              $"п.9: экстраполяция RelPos += RelVel*dt: X 100→110 (получено {sb?.Contact.RelPos.X:0.#})");
        Check(sb != null && MathF.Abs(sb.ScreenX - 110f) < 0.5f && MathF.Abs(sb.ScreenY) < 0.5f,
              $"п.9: ScreenX=RelPos.X при yaw=0 (получено {sb?.ScreenX:0.#},{sb?.ScreenY:0.#})");

        m.Range = RadarModel.DefaultRange;
    }

    // ──────── п.11 (R3): цвет по фракции, hit-test и клик-выбор ────────

    private void CheckR3Interaction()
    {
        // 11.1: палитра фракций различима (RadarPanel красит блип по
        // HudTheme.FactionColor — в headless картинку не проверить, проверяем
        // саму палитру и то, что контакт несёт фракцию для неё).
        var cPlayer = HudTheme.FactionColor((byte)Faction.Player);
        var cHostile = HudTheme.FactionColor((byte)Faction.Hostile);
        var cNeutral = HudTheme.FactionColor((byte)Faction.Neutral);
        Check(cPlayer != cHostile && cHostile != cNeutral && cPlayer != cNeutral,
              "п.11: цвета фракций Player/Hostile/Neutral попарно различны");

        // 11.2: hit-test панели — пиксель блипа кликом даёт именно его NetId.
        var panel = _main!.GetNodeOrNull<RadarPanel>(
            "HudLayer/HudRoot/BottomRight/RadarPanel/RadarBox/RadarView");
        Check(panel != null, "п.11: RadarView (RadarPanel) смонтирован в HudLayer");
        if (panel == null) return;

        var m = _source!.Model; // Range уже возвращён к 2000
        m.Sync(_contacts);
        m.Advance(0.016f, m.ForwardYaw);

        // Мишень (1200 м) — её пиксель кликается именно её же NetId'ом.
        int nearId = _nearTarget?.TargetNetId ?? 0;
        var px = panel.BlipPixel(nearId);
        Check(px != null, "п.11: BlipPixel(мишень) — блип на панели");
        if (px != null)
            Check(panel.HitTestBlipId(px.Value) == nearId,
                  "п.11: HitTestBlipId(пиксель мишени) == NetId мишени");

        // Пиксель Ship2 — кликается Ship2 (не соседняя мишень).
        var ship2 = WorldSpawner.Current!.FindShipByName("Ship2");
        int ship2Id = ship2?.Net?.ShipNetId ?? 0;
        var px2 = panel.BlipPixel(ship2Id);
        Check(px2 != null && panel.HitTestBlipId(px2.Value) == ship2Id,
              "п.11: HitTestBlipId(пиксель Ship2) == ShipNetId Ship2");

        // Точка вне всех блипов (> ClickRadius=10 px) — выбора нет.
        Vector2 panelCenter = panel.GetRect().Size * 0.5f;
        int missId = panel.HitTestBlipId(
            panelCenter + new Vector2(0f, -(panel.GetRect().Size.Y * 0.5f - 1f)));
        Check(missId == 0, "п.11: клик у края кольца (мимо блипов) — NetId 0");

        // 11.3: клик-выбор — Select по hit-test'у ставит флаг цели в модели
        // (pinned-маркер рисуется именно по IsTarget).
        var targeting = _main.GetNodeOrNull<TargetingService>("HudLayer/TargetingService");
        Check(targeting != null, "п.11: TargetingService доступен");
        if (targeting == null || px == null) return;

        Check(targeting.Select(panel.HitTestBlipId(px.Value)),
              "п.11: Select(NetId из hit-test) -> true");
        _source.CollectContacts(_contacts); // перезабор: RadarSource проставит IsTarget
        m.Sync(_contacts);
        m.Advance(0.016f, m.ForwardYaw);
        var tb = m.FindBlip(nearId);
        Check(tb != null && tb.Contact.IsTarget,
              "п.11: блип цели помечен IsTarget (pinned-маркер)");

        targeting.Clear();
        _source.CollectContacts(_contacts);
        m.Sync(_contacts);
        m.Advance(0.016f, m.ForwardYaw);
        tb = m.FindBlip(nearId);
        Check(tb != null && !tb.Contact.IsTarget,
              "п.11: после Clear() флаг цели снят");

        // 11.4: колесо-зум: Range уже проходил в п.9; проверяем ступени панели.
        panel.SetRangeStep(0);
        Check(Mathf.Abs(m.Range - RadarPanel.RangeSteps[0]) < 0.01f,
              $"п.11: SetRangeStep(0) -> R={m.Range} (ступень {RadarPanel.RangeSteps[0]})");
        panel.SetRangeStep(2);
        Check(Mathf.Abs(m.Range - RadarModel.DefaultRange) < 0.01f,
              "п.11: возврат ступени 2 -> R=2000");
    }
}
