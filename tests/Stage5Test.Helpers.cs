using Godot;
using System.Collections.Generic;
using System.Linq;

namespace SpaceCorporation.Tests;

/// <summary>
/// Вспомогательные проверки Stage5Test — отдельный partial того же драйвера,
/// чтобы основной файл остался картой фаз (образец Stage4Test.Helpers.cs).
/// </summary>
public partial class Stage5Test
{
    // ──────────────────── п.1: чистые функции Docking.cs ────────────────────

    /// <summary>п.1: чистые функции без дерева сцены — как CollisionDamage в
    /// Stage4Test п.13–п.16. Пороги читаются из констант, дублируются числами
    /// только явные границы.</summary>
    private void CheckPureDockingFn()
    {
        // InZone: границы из DockRange (90): 80 — внутри, 120 — снаружи.
        Check(Docking.InZone(new Vector3(0, 0, 80), Vector3.Zero),
              "п.1: InZone((0,0,80)) == true (внутри DockRange=90)");
        Check(!Docking.InZone(new Vector3(0, 0, 120), Vector3.Zero),
              "п.1: InZone((0,0,120)) == false (снаружи DockRange=90)");
        Check(Docking.InZone(new Vector3(0, 0, Docking.DockRange), Vector3.Zero),
              "п.1: InZone ровно на DockRange == true (<=)");
        Check(!Docking.InZone(new Vector3(0, 0, Docking.DockRange + 0.01f), Vector3.Zero),
              "п.1: InZone чуть за DockRange == false");

        // AllowDock: границы из DockSpeedLimit (8).
        Check(Docking.AllowDock(7.9f), "п.1: AllowDock(7.9) == true");
        Check(!Docking.AllowDock(8.1f), "п.1: AllowDock(8.1) == false");
        Check(Docking.AllowDock(Docking.DockSpeedLimit),
              "п.1: AllowDock ровно DockSpeedLimit == true (<=)");

        // П6: ComputeAlignError — чистая схема выравнивания.
        // Конвенция: nose = −Z, «над площадкой» = вдоль up (+Y).
        var id = Docking.ComputeAlignError(new Vector3(0, 20, 0), Quaternion.Identity,
                                           Vector3.Zero, Vector3.Up, Vector3.Forward);
        Check(Mathf.Abs(id.YawDeg) < 1e-3f && Mathf.Abs(id.PitchDeg) < 1e-3f
              && Mathf.Abs(id.RollDeg) < 1e-3f && id.OffsetM < 1e-3f && id.Within,
              $"п.1: AlignError(идеальное) все нули && Within (yaw={id.YawDeg} pitch={id.PitchDeg} roll={id.RollDeg} off={id.OffsetM})");
        // Рыскание: доворот +10° вокруг up — проекция носа уходит по −right → −10°.
        var yawQ = new Quaternion(Vector3.Up, Mathf.DegToRad(10f));
        var yawE = Docking.ComputeAlignError(new Vector3(0, 20, 0), yawQ, Vector3.Zero,
                                             Vector3.Up, Vector3.Forward);
        Check(Mathf.Abs(yawE.YawDeg - -10f) < 0.1f && !yawE.Within,
              $"п.1: AlignError(yaw -10°): YawDeg≈-10 && !Within (получено {yawE.YawDeg})");
        // Допуск: тангаж 4.99° — Within, 5.01° — нет (нос вверх = доворот +X).
        var p5 = Docking.ComputeAlignError(new Vector3(0, 20, 0),
                                           new Quaternion(Vector3.Right, Mathf.DegToRad(4.99f)),
                                           Vector3.Zero, Vector3.Up, Vector3.Forward);
        var p51 = Docking.ComputeAlignError(new Vector3(0, 20, 0),
                                            new Quaternion(Vector3.Right, Mathf.DegToRad(5.01f)),
                                            Vector3.Zero, Vector3.Up, Vector3.Forward);
        Check(p5.Within && !p51.Within && Mathf.Abs(Mathf.Abs(p5.PitchDeg) - 4.99f) < 0.1f
              && Mathf.Abs(Mathf.Abs(p51.PitchDeg) - 5.01f) < 0.1f,
              $"п.1: AlignError |тангаж| 4.99° Within, 5.01° нет (pitch={p5.PitchDeg:F3}/{p51.PitchDeg:F3}, in={p5.Within}/{p51.Within})");
        // Смещение: 2.4 м — Within, 2.6 м — нет (по правой оси площадки +X).
        var oA = Docking.ComputeAlignError(new Vector3(2.4f, 20, 0),
                                           Quaternion.Identity, Vector3.Zero, Vector3.Up, Vector3.Forward);
        var oB = Docking.ComputeAlignError(new Vector3(2.6f, 20, 0),
                                           Quaternion.Identity, Vector3.Zero, Vector3.Up, Vector3.Forward);
        Check(oA.Within && !oB.Within,
              $"п.1: AlignError смещение 2.4 м Within, 2.6 м нет (off={oA.OffsetM:F2}/{oB.OffsetM:F2}, in={oA.Within}/{oB.Within})");
        // Крен: доворот вокруг оси носа (-Z) на 10° → ошибка крена ≈10°.
        var rollE = Docking.ComputeAlignError(new Vector3(0, 20, 0),
                                              new Quaternion(Vector3.Back, Mathf.DegToRad(10f)),
                                              Vector3.Zero, Vector3.Up, Vector3.Forward);
        Check(Mathf.Abs(Mathf.Abs(rollE.RollDeg) - 10f) < 0.1f && !rollE.Within,
              $"п.1: AlignError крен |10°| && !Within (получено {rollE.RollDeg})");

        // BUG «стыкуется вверх ногами» (два корня): (1) `up.Cross(fwd)` давал
        // ЗЕРКАЛЬНЫЙ базис (det=-1) — кватернион переворачивал корабль килем
        // вверх; (2) Vector3.Back в Godot = (0,0,+1), а нос модели = −Z, т.е.
        // Vector3.Forward — прежний `rot * Vector3.Back` брал ХВОСТ и разворачивал
        // рысканием на 180°. UprightPose: верх = нормаль палубы, азимут носа
        // сохранён, базис правый (det=+1).
        var upQ = Docking.UprightPose(Quaternion.Identity, Vector3.Up);
        Check(Docking.UprightCos(upQ, Vector3.Up) > 0.999f,
              $"п.1: UprightPose(идеал) верх совпадает с палубой (cos={Docking.UprightCos(upQ, Vector3.Up):F3})");
        Check((upQ * Vector3.Forward).Dot(Vector3.Forward) > 0.999f,
              "п.1: UprightPose(идеал) = идеал (нос -Z не меняется)");
        // Перевёрнутый вход (килем вверх) -> выпрямленный выход тем же рысканием.
        var flip = new Quaternion(Vector3.Right, Mathf.Pi);
        var fixQ = Docking.UprightPose(flip, Vector3.Up);
        Vector3 noseIn = flip * Vector3.Forward;
        Check(Docking.UprightCos(fixQ, Vector3.Up) > 0.999f
              && (fixQ * Vector3.Forward).Dot(noseIn) > 0.99f,
              $"п.1: UprightPose(перевёрнутый) выпрямлен, рыскание сохранено (up={Docking.UprightCos(fixQ, Vector3.Up):F3} nose·noseIn={(fixQ * Vector3.Forward).Dot(noseIn):F3})");
        // Косой вход (нос вниз под 45°, тангаж) -> upright с тем же азимутом носа.
        var dive = new Quaternion(Vector3.Right, Mathf.DegToRad(45f));
        var diveOut = Docking.UprightPose(dive, Vector3.Up);
        var noseInFlat = dive * Vector3.Forward; noseInFlat.Y = 0f; noseInFlat = noseInFlat.Normalized();
        Check(Docking.UprightCos(diveOut, Vector3.Up) > 0.999f
              && (diveOut * Vector3.Forward).Dot(noseInFlat) > 0.99f,
              "п.1: UprightPose(тангаж 45°) -> upright, азимут носа сохранён");
        // Правый базис: X-ось выхода = Y×Z (det=+1) — проверка через ортогональность
        // и ориентацию: (outX × outY)·outZ > 0.
        var bo = new Basis(fixQ);
        Check(bo.X.Cross(bo.Y).Dot(bo.Z) > 0.99f,
              $"п.1: UprightPose базис правый (det={bo.X.Cross(bo.Y).Dot(bo.Z):F3})");
        // UprightLatchCos: боковой вход (верх к +X) не проходит латч.
        var sideQ = new Quaternion(Vector3.Back, Mathf.DegToRad(90f));
        Check(!Docking.IsLatchedUpright(Vector3.Zero, Vector3.Zero, Docking.UprightCos(sideQ, Vector3.Up)),
              "п.1: IsLatchedUpright(бок 90°) == false");
        Check(Docking.IsLatchedUpright(Vector3.Zero, Vector3.Zero, Docking.UprightCos(upQ, Vector3.Up)),
              "п.1: IsLatchedUpright(upright) == true");

        // CaptureAccel: dx = (0,0,10), v = 0 → ускорение +z и |a| <= MaxSpringAccel.
        var a = Docking.CaptureAccel(new Vector3(0, 0, 10), Vector3.Zero);
        Check(a.Z > 0, $"п.1: CaptureAccel(dx=+z, v=0) -> a.z > 0 (получено {a.Z})");
        Check(a.Length() <= Docking.MaxSpringAccel + 1e-3f,
              $"п.1: |CaptureAccel| <= MaxSpringAccel (получено {a.Length()})");
        // Кламп: большой dx не должен давать ускорение выше клампа.
        var aBig = Docking.CaptureAccel(new Vector3(0, 0, 1000), Vector3.Zero);
        Check(Mathf.Abs(aBig.Length() - Docking.MaxSpringAccel) < 1e-3f,
              $"п.1:CaptureAccel(dx=1000) закламлен в MaxSpringAccel (получено {aBig.Length()})");
        // Демпфирование: v вдоль dx уменьшает (незаклампленьгй) a. Берём малый
        // dx, чтобы не упираться в MaxSpringAccel (при dx=10 raw a=40 > кламп,
        // и с демпфированием тоже клампится — различие не видно).
        var aSmall = Docking.CaptureAccel(new Vector3(0, 0, 3), Vector3.Zero);          // raw 18
        var aDamped = Docking.CaptureAccel(new Vector3(0, 0, 3), new Vector3(0, 0, 2)); // raw 10
        Check(aDamped.Z < aSmall.Z && aSmall.Z < Docking.MaxSpringAccel,
              $"п.1: демпфирование: a(dx=3,v=2)={aDamped.Z} < a(dx=3,v=0)={aSmall.Z} (без клампа)");


        // IsLatched: границы расстояния и угла.
        float latchAngle = Mathf.Acos(Docking.LatchAlignCos);
        Check(Docking.IsLatched(new Vector3(0, 0, Docking.LatchDistance), Vector3.Zero, latchAngle),
              "п.1: IsLatched на границах (расстояние == LatchDistance, угол == acos(0.9)) == true");
        Check(!Docking.IsLatched(new Vector3(0, 0, Docking.LatchDistance + 0.01f), Vector3.Zero, latchAngle),
              "п.1: IsLatched чуть дальше LatchDistance == false");
        Check(!Docking.IsLatched(Vector3.Zero, Vector3.Zero, latchAngle + 0.01f),
              "п.1: IsLatched при угле чуть больше порога == false");

        // IsDocked: границы допуска по позиции (0.5) и скорости (0.3).
        Check(Docking.IsDocked(new Vector3(0, 0, Docking.DockDistance), new Vector3(0, 0, Docking.DockSpeedEps)),
              "п.1: IsDocked на границах (|dx|==DockDistance, |v|==DockSpeedEps) == true");
        Check(!Docking.IsDocked(new Vector3(Docking.DockDistance + 0.01f, 0, 0), Vector3.Zero),
              "п.1: IsDocked при |dx| > DockDistance == false");
        Check(!Docking.IsDocked(Vector3.Zero, new Vector3(0, 0, Docking.DockSpeedEps + 0.01f)),
              "п.1: IsDocked при |v| > DockSpeedEps == false");

        // AlignError: нос (-Z) смотрит в точку стыка (ось +z от корабля к доку —
        // т.е. axis = направление «нос к доку»). Кватернион identity: нос = -Z;
        // axis = -Z → ошибка 0; axis = +X → π/2.
        float err0 = Docking.AlignError(Quaternion.Identity, Vector3.Forward);
        Check(err0 < 1e-3f, $"п.1: AlignError(identity, -Z) ≈ 0 (получено {err0})");
        float err90 = Docking.AlignError(Quaternion.Identity, Vector3.Right);
        Check(Mathf.Abs(err90 - Mathf.Pi / 2f) < 1e-3f,
              $"п.1: AlignError(identity, +X) ≈ π/2 (получено {err90})");

        // Вспомогательные предикаты фаз (используются в 5.9/5.10/5.11).
        Check(Docking.IsCapturePhase(DockState.Capturing) && Docking.IsCapturePhase(DockState.Docked)
              && !Docking.IsCapturePhase(DockState.Approved),
              "п.1: IsCapturePhase true для Capturing/Latched/Docked, false для Approved");
        Check(Docking.IsPending(DockState.Approved) && Docking.IsPending(DockState.Capturing)
              && !Docking.IsPending(DockState.Docked) && !Docking.IsPending(DockState.None),
              "п.1: IsPending true для Requested..Latched, false для None/Docked");
    }

    // ──────────────────── п.2–п.4: свойства станции ────────────────────

    private Station _station;

    /// <summary>п.2: станция из мира — док-свойства в исходном нуле.</summary>
    private void CheckStationDefaults()
    {
        _station = _spawner!.GetChildren().OfType<Station>().FirstOrDefault();
        Check(_station != null, "п.2: станция найдена в GameRoot");
        if (_station == null) return;
        Check(!_station.DockApproved, "п.2: DockApproved == false в старте");
        Check(_station.DockingState == DockState.None,
              $"п.2: DockingState == None (получено {_station.DockingState})");
        Check(_station.DockerNetId == 0,
              $"п.2: DockShipNetId == 0 (получено {_station.DockerNetId})");
        var st = _station.State!;
        Check(st.DockStateValue == 0 && st.DockShipNetId == 0 && !st.DockApproved,
              "п.2: byte-поля StationState нулевые (быт-значение = None)");
    }

    /// <summary>п.3: серверная запись ставит Approved/shipNetId.</summary>
    private void CheckSetOnServer()
    {
        if (_station == null) return;
        _station.SetDockStateServerOnly(DockState.Approved, 7);
        Check(_station.DockApproved, "п.3: сервер: DockApproved == true");
        Check(_station.State!.DockStateValue == (byte)DockState.Approved,
              $"п.3: DockStateValue == (byte)Approved == 2 (получено {_station.State!.DockStateValue})");
        Check(_station.DockingState == DockState.Approved,
              $"п.3: обёртка DockingState == Approved (получено {_station.DockingState})");
        Check(_station.DockerNetId == 7,
              $"п.3: DockShipNetId == 7 (получено {_station.DockerNetId})");
    }

    /// <summary>п.4: та же запись с роли клиента — PushError и отказ (значение
    /// не изменилось). Шов TestSetRoleForTest изображает роль клиента.</summary>
    private void CheckNonAuthReject()
    {
        if (_station == null) return;
        var nm = NetworkManager.Instance!;
        var prev = nm.TestSetRoleForTest(NetworkManager.NetRole.Client);
        _station.SetDockStateServerOnly(DockState.Capturing, 8); // ожидаем отказ
        Check(_station.DockingState == DockState.Approved && _station.DockerNetId == 7,
              $"п.4: запись с не-авторитета отклонена (осталось {_station.DockingState}/{_station.DockerNetId})");
        nm.TestSetRoleForTest(prev);
        _station.SetDockStateServerOnly(DockState.None, 0);
        Check(_station.DockingState == DockState.None && _station.DockerNetId == 0
              && !_station.DockApproved,
              "п.4: сервер снова пишет (None/0 снят корректно)");
    }

    // ──────────────── п.5: оба .tres содержат dock-пути ────────────────

    /// <summary>п.5: тест читает .tres и утверждает наличие dock-путей со
    /// spawn = true и replication_mode = 2 (OnChange). Образец —
    /// CheckStationSpawnConfig/CheckHealthReplicationConfig из Stage4Test.
    /// Это ловушка «пустой .tres = огни не горят» (§3.1).</summary>
    private void CheckDockReplicationConfigs()
    {
        CheckDockPath("res://scenes/StationReplication.tres", "StationState:DockApproved");
        CheckDockPath("res://scenes/StationReplication.tres", "StationState:DockShipNetId");
        CheckDockPath("res://scenes/StationReplication.tres", "StationState:DockStateValue");
        CheckDockPath("res://scenes/ShipReplication.tres", "NetState:DockStateValue");
        CheckDockPath("res://scenes/ShipReplication.tres", "NetState:DockStationNetId");
    }

    private void CheckDockPath(string resPath, string propName)
    {
        var cfg = GD.Load<SceneReplicationConfig>(resPath);
        Check(cfg != null, $"п.5: {resPath} загружается");
        if (cfg == null) return;
        var p = new NodePath(propName);
        Check(cfg.HasProperty(p), $"п.5: {resPath} содержит {propName}");
        if (!cfg.HasProperty(p)) return;
        Check(cfg.PropertyGetSpawn(p), $"п.5: spawn == true для {propName}");
        Check((int)cfg.PropertyGetReplicationMode(p) == 2,
              $"п.5: replication_mode == OnChange(2) для {propName} " +
              $"(получено {(int)cfg.PropertyGetReplicationMode(p)})");
    }

    // ──────────── п.7–п.9: реестр DockRequests (серверная карта) ────────────

    private Ship? _shipA;
    private Ship? _shipB;

    /// <summary>п.7: BeginDock согласовывает обе стороны (инвариант
    /// NetState.DockStationNetId ↔ StationState.DockShipNetId).</summary>
    private void CheckBeginDockInvariant()
    {
        var nm = NetworkManager.Instance!;
        _shipA = _spawner!.FindShipByName("Ship");
        _shipB = _spawner.SpawnShipFromScene(_station!.GlobalPosition + new Vector3(500, 0, 0), "ShipDockB");
        Check(_shipA != null && _shipB != null, "п.7: второй корабль заспавнен (ShipDockB)");
        if (_shipA == null || _shipB == null) return;
        // Этап 6: тесты дока пишут скорость напрямую (TestSetVelocity: пружина
        // захвата 20 м/с, таран дока 120 м/с). Пилот-ассист гасил бы эти скорости
        // между шагами — тестам дока нужен прежний режим чистой инерции.
        _shipA.SetPilotAssist(false);
        _shipB.SetPilotAssist(false);
        Check(_shipB.Net!.ShipNetId != _shipA.Net!.ShipNetId,
              "п.7: у второго корабля адрес из общего счётчика (не столкнулись)");

        int shipA = _shipA.Net!.ShipNetId, stationId = _station.State!.StationNetId;
        int rejBefore = nm.RejectedDockCount;
        bool ok = nm.BeginDock(shipA, 1, stationId, out string reason);
        Check(ok, $"п.7: BeginDock принят (reason='{reason}')");
        var entry = nm.FindDockRequest(shipA);
        Check(entry != null && entry.Value.PeerId == 1 && entry.Value.StationNetId == stationId
              && entry.Value.State == DockState.Requested,
              $"п.7: запись реестра peer=1 st=#{stationId} state=Requested (получено {entry})");
        // SetDockPhase пишет обе стороны — инвариант после смены фазы:
        Check(nm.SetDockPhase(shipA, DockState.Approved), "п.7: SetDockPhase(Approved) == true");
        Check(_shipA.Net!.DockStationNetId == stationId &&
              (DockState)_shipA.Net!.DockStateValue == DockState.Approved,
              "п.7: NetState корабля: ст# и фаза Approved");
        Check(_station.DockerNetId == shipA && _station.DockingState == DockState.Approved
              && _station.DockApproved,
              "п.7: StationState согласован (инвариант двух сторон держится)");
        Check(nm.RejectedDockCount == rejBefore, "п.7: приём не поднял счётчик отказов");
    }

    /// <summary>п.8: AbortDock обнуляет обе стороны и ставит None; повтор — no-op.</summary>
    private void CheckAbortDock()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null) return;
        int shipA = _shipA.Net!.ShipNetId;
        nm.AbortDock(shipA, "test-p8");
        Check(!nm.HasDockRequest(shipA), "п.8: запись снята из реестра");
        Check(_shipA.Net!.DockStateValue == 0 && _shipA.Net!.DockStationNetId == 0,
              "п.8: NetState обнулён (None / 0)");
        Check(_station!.DockingState == DockState.None && _station.DockerNetId == 0
              && !_station.DockApproved,
              "п.8: StationState обнулён (None / 0 / lights off)");
        nm.AbortDock(shipA, "repeat"); // идемпотентность: без PushError-шторма
        Check(!nm.HasDockRequest(shipA), "п.8: повторный AbortDock — no-op");
    }

    /// <summary>п.9: станция, занятая другим кораблём, отказывает второму;
    /// счётчик отказов растёт.</summary>
    private void CheckStationBusyReject()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null || _shipB == null) return;
        int shipA = _shipA.Net!.ShipNetId, shipB = _shipB.Net!.ShipNetId;
        int stationId = _station!.State!.StationNetId;
        int rejBefore = nm.RejectedDockCount;
        Check(nm.BeginDock(shipA, 1, stationId, out _) &&
              nm.SetDockPhase(shipA, DockState.Capturing),
              "п.9: ship#A захватил станцию (Capturing)");
        bool ok2 = nm.BeginDock(shipB, 1, stationId, out string reason2);
        Check(!ok2 && reason2 == "station-busy",
              $"п.9: ship#B получил отказ station-busy (получено ok={ok2}, '{reason2}')");
        Check(nm.RejectedDockCount == rejBefore + 1,
              $"п.9: RejectedDockCount вырос на 1 (было {rejBefore}, стало {nm.RejectedDockCount})");
        // За захватчиком осталась его сделка, у отказавшего — чистота:
        Check(nm.HasDockRequest(shipA) && !nm.HasDockRequest(shipB),
              "п.9: реестр не тронул отказанный корабль");
        nm.AbortDock(shipA, "test-cleanup");
    }

    // ──────────── п.10–п.13: серверный гейт RPC RequestDocking ────────────

    /// <summary>п.10-п.12: серверный гейт AcceptDockFrom — приём, не-пилот,
    /// чужой shipNetId. Швы зовут боевую функцию (не её копию).</summary>
    private void CheckDockAcceptFlow()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null) return;
        int shipA = _shipA.Net!.ShipNetId;
        int stationId = _station!.State!.StationNetId;
        // Пилот ship#A — пир 1 (спавн-конвенция Stage5Test); телепорт в зону.
        _shipA.TestSetPilotForTest(1);
        _shipA.GlobalPosition = _station.DockPointGlobal + new Vector3(40, 0, 0);
        _shipA.TestSetVelocity(Vector3.Zero);
        int rejBefore = nm.RejectedDockCount, accBefore = _shipA.AcceptedDockCount;
        bool ok = _shipA.TestRequestDock(1, stationId);
        Check(ok, "п.10: запрос пилота принят серверным гейтом (== true)");
        var e = nm.FindDockRequest(shipA);
        // Requested в реестре живёт нанoseconds (BeginDock -> SetDockPhase(Approved)
        // в одном вызове): наблюдаемая фаза сделки — Approved (п.7 блока A).
        Check(e != null && e.Value.State == DockState.Approved,
              $"п.10: в реестре Approved (получено {e})");
        Check(_station.DockApproved && (DockState)_shipA.Net!.DockStateValue == DockState.Approved
              && _shipA.Net!.DockStationNetId == stationId,
              "п.10: огни горят (Approved), корабль в Approved");
        Check(_shipA.AcceptedDockCount == accBefore + 1 && nm.RejectedDockCount == rejBefore,
              "п.10: счётчики: принята 1, отказов 0");
        nm.AbortDock(shipA, "test-p10-done");

        // п.11: не-пилот (peer 1 шлёт за корабль с пилотом 999) — отказ.
        _shipA.TestSetPilotForTest(999);
        bool okN = _shipA.TestRequestDock(1, stationId);
        Check(!okN, "п.11: запрос не-пилота отвергнут (== false)");
        Check(!nm.HasDockRequest(shipA) && !_station.DockApproved,
              "п.11: после отказа — чистая сделка (без записи в реестре)");
        _shipA.TestSetPilotForTest(1);

        // п.12: чужой shipNetId в RPC (peer 1 дёргает ship#B, пилот которого 1?
        // ставим пилоту B чужого и шлём от 1 за B — peer != Pilot(B)) — отказ.
        _shipB!.TestSetPilotForTest(999);
        bool okF = _shipB.TestAcceptDockFromForTest(1, _shipB.Net!.ShipNetId, stationId);
        Check(!okF, "п.12: чужой/не-пилотный адрес в RPC отвергнут");
        _shipB.TestSetPilotForTest(1);

        // п.12b (регресс бага «запрос стыковки не работает из режима
        // взаимодействия»): радиоменю открывается ТОЛЬКО в Interaction-режиме,
        // а гейт клавиши F (RequestDocking без fromCommlink) глушил запрос там
        // целиком. Проверка клиентских гейтов (не серверного шва): в
        // Interaction обычный вызов (путь F) — мёртв, вызов из комм-меню
        // (fromCommlink) — проходит; отмена из радиоменю — тоже проходит.
        _shipA.TestSetPilotForTest(1);
        _shipA.SetInteractionMode(true);
        _shipA.RequestDocking(stationId);
        Check(!nm.HasDockRequest(shipA),
              "п.12b: в Interaction путь F (без fromCommlink) по-прежнему закрыт");
        _shipA.RequestDocking(stationId, fromCommlink: true);
        Check(nm.HasDockRequest(shipA) && (DockState)_shipA.Net!.DockStateValue == DockState.Approved,
              "п.12b: запрос из комм-меню в Interaction принят (Approved)");
        _shipA.RequestDockCancel();
        Check(nm.HasDockRequest(shipA),
              "п.12b: отмена без fromCommlink в Interaction по-прежнему закрыта");
        _shipA.RequestDockCancel(fromCommlink: true);
        Check(!nm.HasDockRequest(shipA) && !_station.DockApproved,
              "п.12b: отмена из радиоменю в Interaction сработала (чисто, огни гаснут)");
        _shipA.SetInteractionMode(false);
    }

    /// <summary>п.13: вызов на не-авторитете (роль клиента) — отказ без записи.</summary>
    private void CheckDockRejectGates()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null) return;
        var prev = nm.TestSetRoleForTest(NetworkManager.NetRole.Client);
        bool ok = _shipA.TestRequestDock(1, _station!.State!.StationNetId);
        Check(!ok, "п.13: вызов RPC-гейта на не-авторитете -> false");
        Check(!nm.HasDockRequest(_shipA.Net!.ShipNetId) && !_station.DockApproved,
              "п.13: не-авторитет не создал ни записи, ни огней");
        nm.TestSetRoleForTest(prev);
    }

    // ──────────── п.14–п.17: гейты зоны/скорости + окно Approved ────────────

    private void CheckDockZoneSpeed()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null) return;
        int shipA = _shipA.Net!.ShipNetId, stationId = _station!.State!.StationNetId;
        Vector3 dp = _station.DockPointGlobal;

        // п.14: 120 м от точки стыка -> отказ «вне зоны», состояние None.
        _shipA.GlobalPosition = dp + new Vector3(120, 0, 0);
        _shipA.TestSetVelocity(Vector3.Zero);
        int rej = nm.RejectedDockCount;
        Check(!_shipA.TestRequestDock(1, stationId),
              "п.14: вне зоны (120 м) — отказ сервера");
        Check(nm.RejectedDockCount == rej + 1 && !nm.HasDockRequest(shipA)
              && _shipA.Net!.DockStateValue == 0,
              "п.14: DockState остаётся None; счётчик отказа вырос");

        // п.15: в зоне (60 м), но |v| = 20 по линии к точке -> отказ «скорость».
        _shipA.GlobalPosition = dp + new Vector3(60, 0, 0);
        _shipA.TestSetVelocity(new Vector3(-20, 0, 0));
        Check(!_shipA.TestRequestDock(1, stationId),
              "п.15: в зоне, скорость 20 > 8 — отказ «скорость»");
        Check(!nm.HasDockRequest(shipA) && (DockState)_shipA.Net!.DockStateValue == DockState.None,
              "п.15: после отказа по скорости — None, записи нет");
        _shipA.TestSetVelocity(Vector3.Zero);

        // п.17: в зоне, |v| <= 8 (телепорт = позиция сцены, не RPC) — принято,
        // через секунду (физкадры) фаза корабля становится Approved.
        _shipA.GlobalPosition = dp + new Vector3(40, 0, 0);
        bool ok = _shipA.TestRequestDock(1, stationId);
        Check(ok && nm.HasDockRequest(shipA) &&
              (DockState)nm.FindDockRequest(shipA)!.Value.State == DockState.Approved,
              "п.17: после телепорта в зону (|v|=0) запросApproved в записи сервера");
    }

    /// <summary>п.16: окно Approved гаснет само без перехвата: сдвигаем метку
    /// в прошлое (шов пишет те же поля, что читает боевая TickDockWindow) и
    /// зовём боевую функцию — AbortDock, None, огни погашены.</summary>
    private void CheckDockWindowExpiry()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null) return;
        _shipA.TestAgeDockWindowForTest(Docking.DockWindowSec + 1);
        _shipA.TestTickDockWindowForTest();
        int shipA = _shipA.Net!.ShipNetId;
        Check(!nm.HasDockRequest(shipA), "п.16: запись удалена из реестра (AbortDock)");
        Check(_shipA.Net!.DockStateValue == 0 && _shipA.Net!.DockStationNetId == 0,
              "п.16: NetState корабля обнулён (None)");
        Check(_station!.DockingState == DockState.None && !_station.DockApproved,
              "п.16: огни станции погашены");
    }

    /// <summary>п.18: «огни горят» на сервере: после одобрения DockBeacon.Visible
    /// == true; после отмены — false. Огни — производное от реплики DockApproved
    /// (_Process станции), не второй транспорт.</summary>
    private void CheckDockBeaconLights()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null) return;
        var beacon = _station!.GetNodeOrNull<MeshInstance3D>("LandingPad/DockingPoint/DockBeacon");
        Check(beacon != null, "п.18: DockBeacon есть в сцене станции");
        if (beacon == null) return;
        // П.16 снял состояние, но Visible — производное, его гасит _Process
        // станции; дёргаем его вручную (headless-тест сам не крутит кадры).
        _station._Process(0.0);
        Check(!beacon.Visible, "п.18: в покое огни выключены");
        _shipA.GlobalPosition = _station.DockPointGlobal + new Vector3(40, 0, 0);
        _shipA.TestRequestDock(1, _station.State!.StationNetId);
        _station._Process(0.0);
        Check(beacon.Visible, "п.18: после одобрения огни горят (Visible == true)");
        nm.AbortDock(_shipA.Net!.ShipNetId, "test-p18-cleanup");
        _station._Process(0.0);
        Check(!beacon.Visible, "п.18: после отмены огни снова погашены");

        // Модуль «посадочная площадка»: точка стыковки читается из LandingPad,
        // и флаг «посадка» ставится швом зоны (детектор над площадкой).
        var pad = _station.GetNodeOrNull<LandingPad>("LandingPad");
        Check(pad != null, "п.18b: LandingPad инстансирован в сцене станции");
        if (pad != null)
        {
            Check(pad.LandingPointGlobal == _station.DockPointGlobal,
                  "п.18b: DockPointGlobal станции = точка зависания площадки");
            Check(!_shipA.IsInLandingZone, "п.18b: вне зоны флаг «посадка» выключен");
            _shipA.TestEnterLandingZoneForTest(pad);
            Check(_shipA.IsInLandingZone && ReferenceEquals(_shipA.CurrentLandingPad, pad),
                  "п.18b: вход в зону площадки -> флаг «посадка» ON");
            _shipA.TestExitLandingZoneForTest(pad);
            Check(!_shipA.IsInLandingZone && _shipA.CurrentLandingPad == null,
                  "п.18b: выход из зоны -> флаг «посадка» OFF");
        }
    }

    // ──────────────────────────── страховки §3.1 ────────────────────────────

    /// <summary>Печатает пути и режимы конфига репликации при старте теста —
    /// страховка от «пустой .tres = огни не горят» (ловушка стадий 2–4).</summary>
    private void DumpReplicationConfig(string path)
    {
        var cfg = GD.Load<SceneReplicationConfig>(path);
        if (cfg == null)
        {
            GD.PrintErr($"CONFIG DUMP: {path} НЕ ЗАГРУЖЕН");
            return;
        }
        var lines = new List<string>();
        // cfg.GetPropertyList() — метаданные класса, а НЕ список репликационных
        // путей; Get("properties") пуст, потому что в .tres они лежат индексными
        // sub-свойствами "properties/N/path". Перебираем N до первой пустоты.
        for (int i = 0; i < 64; i++)
        {
            var p = cfg.Get($"properties/{i}/path");
            if (p.VariantType == Variant.Type.Nil) break;
            NodePath propPath = (NodePath)p;
            int mode = (int)(long)cfg.Get($"properties/{i}/replication_mode");
            bool spawn = (bool)cfg.Get($"properties/{i}/spawn");
            lines.Add($"{propPath} mode={mode} spawn={(spawn ? 1 : 0)}");

        }
        GD.Print($"CONFIG DUMP {path} ({lines.Count}): " + string.Join(" | ", lines));
    }

    // ------------------------------------------------------------- утилиты

    private void Check(bool ok, string what)
    {
        _checks++;
        if (ok)
            GD.Print($"PASS: {what}");
        else
        {
            _failures++;
            GD.PrintErr($"FAIL: {what}");
        }
    }

    private void Finish()
    {
        GD.Print($"==== Stage5Test: проверок={_checks}, провалов={_failures} ====");
        GD.Print(_failures == 0 ? "==== STAGE5TEST OK ====" : "==== STAGE5TEST FAILED ====");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
