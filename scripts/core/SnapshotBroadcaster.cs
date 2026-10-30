using Godot;
using System;
using System.Collections.Generic;
using SpaceCorporation.Net;

namespace SpaceCorporation;

/// <summary>
/// Серверный рассылщик батчей снапшотов (шаг R4 плана net-refactor-plan.md,
/// §2.2/§2.3). Godot-обёртка над чистым ядром `BroadcasterCore` + `SpatialIndex`
/// + `ShipRecordCodec`: грид интереса, календарь тиров, упаковка записей в
/// переиспользуемые буферы, один RPC на батч, канал 2, Unreliable.
///
/// Схема пакетов (§2.2): один батч = один тир одного клиента (заголовок 8 Б +
/// якорь 12 Б + конкатенация записей фиксированной длины). Якорь — позиция
/// СВОЕГО корабля клиента: смещения остальных в int16 при шаге тира
/// (near 1 см → ±327 м, mid 4 см → ±1310 м, far 10 см → ±3276 м). Если запись
/// не влезает (корабль дальше запаса шага), сервер расширяет шаг пакета и
/// повторяет запись — клиент читает шаг из заголовка, а не угадывает.
///
/// Свой корабль пилота — отдельная запись 25 Б с AppliedTick/AppliedMask,
/// всегда в near-батче, независимо от дистанции (§2.3).
///
/// Приём на клиенте: `RpcSnapshotBatch` → распаковка → `Ship.ApplyBatchRecord`
/// пишет поля `NetState`; существующий страж `SimTick != _lastNetTickSeen` в
/// `Ship._Process` кладёт снимок в буфер приёма — транспорт заменён, pipeline
/// реконсиляции/интерполяции НЕ менялся (§2.6, шаг R4).
///
/// Узел создаёт сам NetworkManager (autoload) в `_Ready`, чтобы путь узла
/// `/NetworkManager/SnapshotBroadcaster` совпадал на сервере и клиенте —
/// иначе RPC по пути не найдёт узел на другой стороне (§4 В4 п.2).
/// </summary>
public partial class SnapshotBroadcaster : Node
{
    public static SnapshotBroadcaster? Instance { get; private set; }

    // ------------------------------------------------------ конфигурация тиров (§2.3)

    /// <summary>Радиусы тиров интереса, м (стартовые из §2.3, правятся по замерам).</summary>
    public float NearRadius = 400f;
    public float MidRadius = 1200f;
    public float FarRadius = 4000f;

    /// <summary>Частоты тиров, Гц. Верх календаря = NearHz.</summary>
    public int NearHz = 20;
    public int MidHz = 10;
    public int FarHz = 3;

    /// <summary>Шаг квантования позиции по тирам, м. mid/far берут шаг больше,
    /// чтобы int16-смещение от якоря покрывало радиус тира (400/1200/4000 м):
    /// near 1 см → ±327 м, mid 4 см → ±1310 м, far 10 см → ±3276 м.</summary>
    public float NearStep = Quant.PosStepNear;    // 1 см
    public float MidStep = 0.04f;                 // 4 см
    public float FarStep = Quant.PosStepFar;      // 10 см

    /// <summary>Бюджет CPU на упаковку, мс на репликационный тик (риск R3:
    /// ≤ 3 мс). Сверх бюджета тик пропускает far/mid-пакеты (near и пилот —
    /// всегда: без своей реплики пилот теряет эталон реконсиляции).</summary>
    public float CpuBudgetMs = 3.0f;

    /// <summary>Порог пропадания пилотской записи, с (шаг R5): если за это время
    /// клиент не принял ни одной пилотской записи своего корабля, он шлёт
    /// `RpcRequestFullSnapshot`, а сервер отвечает одним пилотским батчем.</summary>
    public float PilotGapTimeout = 0.5f;

    // -------------------------------------------------------------- состояние

    private readonly SpatialIndex _grid = new();
    private readonly List<Ship> _bodies = new(512);   // слот-нумерация = индекс в списке
    private Vec3[] _positions = new Vec3[512];
    private float _gridTimer;

    // netId → слот в _grid (шаг R7, гейтинг событий попаданий). Строится в
    // RebuildGrid; по netId стрелявшего/цели находит слот для PeerWantsEvent.
    private readonly Dictionary<int, int> _netToSlot = new(512);
    private float _repAccum;

    /// <summary>Пер-клиентское состояние рассылки: поток интереса + буферы.</summary>
    private sealed class ClientStream
    {
        public readonly InterestState Interest = new();
        public readonly List<int> Near = new(128), Mid = new(128), Far = new(128);
        /// <summary>Свой корабль пира (ShipNetId) или 0, если ещё не назначен.</summary>
        public int OwnShipNetId;
        /// <summary>Счётчик репликационных тиков этого клиента (календарь тиров).</summary>
        public int Tick;
        /// <summary>Переиспользуемый буфер упаковки (растёт до пика и стоит).</summary>
        public byte[] Buf = new byte[4096];
    }
    private readonly Dictionary<int, ClientStream> _clients = new();

    /// <summary>Клиентский сторожевой счётчик пропадания пилотской записи своего
    /// корабля (шаг R5). Живёт только на клиенте; сервер его не трогает.</summary>
    private BroadcasterCore.PilotGapWatch _gapWatch =
        new(BroadcasterCore.PilotGapWatch.DefaultTimeout,
            BroadcasterCore.PilotGapWatch.DefaultRequestEvery);
    /// <summary>Свой корабль этого клиента (для запроса полного снимка).</summary>
    private int _clientOwnShipNetId;

    // Кэш снимков на один репликационный тик: slot → снимок (переиспользуется).
    private ShipSnapshotQ[] _snapCache = new ShipSnapshotQ[512];
    private bool[] _snapValid = new bool[512];

    // Метрики (в панели NetworkManager через счётчики батча; здесь — локальные).
    private int _sentPackets, _sentRecords;

    public override void _Ready()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    // ----------------------------------------------------------------- цикл

    public override void _Process(double delta)
    {
        var nm = NetworkManager.Instance;
        if (nm == null) return;

        if (nm.IsServer && nm.HasRealPeer && _clients.Count > 0)
            ServerTick(nm, (float)delta);
        else if (!nm.IsServer)
            ClientGapWatch(nm, (float)delta);
        // Клиентская ветка приёма — в RpcSnapshotBatch (RPC-колбэк), здесь не нужна.
    }

    /// <summary>Клиентская ветка (шаг R5): сторожим пропадание пилотской записи
    /// своего корабля. Если эталон не обновлялся дольше порога — просим у сервера
    /// полный снимок (надежный канал 1), не чаще кулдауна.</summary>
    private void ClientGapWatch(NetworkManager nm, float dt)
    {
        EnsureClientOwnShip(nm);
        if (_clientOwnShipNetId == 0) return; // своего корабля ещё нет — нечего сторожить
        if (_gapWatch.Tick(dt))
            // Просим полный снимок только у сервера (peer 1), надёжно, канал 1.
            RpcId(1, nameof(RpcRequestFullSnapshot), _clientOwnShipNetId);
    }

    /// <summary>Найти своего корабля на клиенте (тот, чей пилот — это мы). Кэш
    /// сбрасываем, если корабль перестал быть локальным (смена пилота/респавн).</summary>
    private void EnsureClientOwnShip(NetworkManager nm)
    {
        if (_clientOwnShipNetId != 0)
        {
            var s = nm.FindShip(_clientOwnShipNetId);
            if (s != null && s.IsLocalPilot) return;
        }
        _clientOwnShipNetId = 0;
        foreach (var kv in nm.Ships)
            if (kv.Value.IsLocalPilot) { _clientOwnShipNetId = kv.Key; break; }
    }

    private void ServerTick(NetworkManager nm, float dt)
    {
        // 1) Грид интереса перестраиваем редко (4 Гц, R2): позиции берём из
        //    опубликованного NetState (сервер — авторитет, он же их и пишет).
        _gridTimer += dt;
        if (_gridTimer >= 0.25f)
        {
            _gridTimer = 0f;
            RebuildGrid(nm);
        }

        // 2) Календарь: один шаг near-тира = 1/NearHz по ВРЕМЕНИ (частота кадров
        //    гуляет, по кадрам считать нельзя).
        _repAccum += dt;
        float repDt = 1f / NearHz;
        if (_repAccum < repDt) return;
        _repAccum -= repDt;
        if (_repAccum > repDt * 3f) _repAccum = repDt * 3f; // догон без лавины

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 3) Каждый клиент: тиры по календарю → упаковка → RpcId на канал 2.
        foreach (var (peerId, st) in _clients)
        {
            if (!nm.PeerExists(peerId)) continue;
            st.Tick++;
            SendForClient(nm, peerId, st, sw.Elapsed.TotalMilliseconds);
        }
    }

    private void RebuildGrid(NetworkManager nm)
    {
        _bodies.Clear();
        foreach (var ship in nm.Ships.Values)
        {
            if (ship == null || !GodotObject.IsInstanceValid(ship)) continue;
            if (ship.Net == null) continue;
            _bodies.Add(ship);
        }
        if (_positions.Length < _bodies.Count)
        {
            _positions = new Vec3[_bodies.Count];
            _snapCache = new ShipSnapshotQ[_bodies.Count];
            _snapValid = new bool[_bodies.Count];
        }
        for (int i = 0; i < _bodies.Count; i++)
        {
            var p = _bodies[i].Net!.NetPosition;
            _positions[i] = new Vec3(p.X, p.Y, p.Z);
        }
        _grid.Rebuild(_positions.AsSpan(0, _bodies.Count));
        _snapValid.AsSpan(0, _bodies.Count).Clear();

        // netId → слот (шаг R7): чтобы по netId стрелявшего/цели найти слот в _grid
        // и спросить интерес пира. Строится вместе с _grid.
        _netToSlot.Clear();
        for (int i = 0; i < _bodies.Count; i++)
        {
            int id = _bodies[i].Net!.ShipNetId;
            if (id != 0) _netToSlot[id] = i;
        }
    }

    /// <summary>
    /// Гейтинг события попаданий по интересу (шаг R7). Возвращает peerId тех
    /// клиентов, которым СТОИТ послать событие между кораблями shooterNetId и
    /// targetNetId: только тем, у кого хоть одна сторона сейчас в поле зрения
    /// (свежий тир тела != None по последнему репликационному тику). Stage 3
    /// (hit-scan) вызовет это перед рассылкой RpcOnHitConfirmed, чтобы не слать
    /// эффекты про корабли, которых клиент не видит.
    ///
    /// interest-состояние каждого пира обновляется в ServerTick (QueryTiers на
    /// каждом репликационном тике), поэтому CurTier свежий на последний тик.
    /// </summary>
    public void PeersInterestedIn(int shooterNetId, int targetNetId, List<int> outPeers)
    {
        outPeers.Clear();
        int slotA = _netToSlot.TryGetValue(shooterNetId, out int a) ? a : -1;
        int slotB = _netToSlot.TryGetValue(targetNetId, out int b) ? b : -1;
        // Ни одна сторона не в индексе (корабль вне мира) — событие некому слать.
        if (slotA < 0 && slotB < 0) return;
        foreach (var (peerId, st) in _clients)
            if (SpatialIndex.PeerWantsEvent(st.Interest, slotA, slotB))
                outPeers.Add(peerId);
    }


    // ------------------------------------------------------- отправка клиенту

    private void SendForClient(NetworkManager nm, int peerId, ClientStream st, double cpuSoFar)
    {
        // Свой корабль пира: всегда, отдельной записью (25 Б) в near-пакете.
        int ownSlot = -1;
        var own = nm.FindShip(st.OwnShipNetId);
        if (own != null)
            ownSlot = _bodies.IndexOf(own); // O(n), n ≤ 400 и 20 Гц — терпимо

        // Якорь = позиция своего корабля (или начало мира, если корабля ещё нет).
        Vec3 anchor = own != null
            ? new Vec3(own.Net!.NetPosition.X, own.Net!.NetPosition.Y, own.Net!.NetPosition.Z)
            : Vec3.Zero;

        _grid.QueryTiers(anchor, NearRadius, MidRadius, FarRadius, st.Interest,
                         st.Near, st.Mid, st.Far);

        int top = NearHz;
        int t = st.Tick;
        bool midFires = BroadcasterCore.TierFires(
            new BroadcasterCore.TierStream(st.Mid.Count, MidHz, ShipRecordCodec.FullRecordSize), top, t);
        bool farFires = BroadcasterCore.TierFires(
            new BroadcasterCore.TierStream(st.Far.Count, FarHz, ShipRecordCodec.FarRecordSize), top, t);

        // near — верх календаря, горит всегда; mid/far режем по бюджету CPU.
        SendTierBatch(nm, peerId, st, st.Near, ownSlot, NearStep, anchor, false);
        if (midFires && cpuSoFar <= CpuBudgetMs)
            SendTierBatch(nm, peerId, st, st.Mid, -1, MidStep, anchor, false);
        if (farFires && cpuSoFar <= CpuBudgetMs)
            SendTierBatch(nm, peerId, st, st.Far, -1, FarStep, anchor, true);
    }

    /// <summary>Упаковать тиры `slots` (+ опциональная пилотская запись `ownSlot`)
    /// в один или несколько батчей и разослать RpcId на канал 2. Нарезка по
    /// BroadcasterCore.BatchPayloadCap (один wire-пакет ≈ один MTU).</summary>
    private void SendTierBatch(NetworkManager nm, int peerId, ClientStream st,
                               List<int> slots, int ownSlot, float posStep,
                               Vec3 anchor, bool farTier)
    {
        int recSize = farTier ? ShipRecordCodec.FarRecordSize
                              : ShipRecordCodec.FullRecordSize;
        int headerMax = ShipRecordCodec.HeaderSize + ShipRecordCodec.AnchorBytes;

        // Буфер: один MTU-пакет + запас на запись, пишемую «через край» перед
        // проверкой cap (см. цикл). Растёт до пика и стоит.
        int need = BroadcasterCore.BatchPayloadCap + headerMax
                 + ShipRecordCodec.PilotRecordSize;
        if (st.Buf.Length < need)
            Array.Resize(ref st.Buf, need);

        // ШАГ ОБЩИЙ НА ПАКЕТ: заголовок объявляет один posStep, и записи,
        // закодированные с МЕНЬШИМ шагом, были бы прочитаны неверно. Поэтому
        // шаг подбирается ПРЕДПРОСМОТРОМ по всем записям тиры (наименьший из
        // posStep·2^k, при котором все смещения влезают в int16), а не
        // «растёт по ходу записи» — иначе первые записи пакета уже испорчены.
        float step = PickTierStep(slots, ownSlot, anchor, posStep);

        int baseTick = (int)Engine.GetPhysicsFrames();
        byte flags = farTier ? ShipRecordCodec.FlagAllFarTier : (byte)0;

        int w = headerMax;      // записи пишем после зарезервированного заголовка
        int records = 0;
        bool hasPilot = false;

        for (int k = 0; k < slots.Count; k++)
        {
            int slot = slots[k];
            if (slot == ownSlot) continue; // пилотская запись — отдельно, не дубль
            // Следующая запись не влезает в MTU — закрываем пакет ДО записи.
            if (w + recSize > BroadcasterCore.BatchPayloadCap)
                Flush();
            ref var snap = ref GetSnapshot(slot);
            snap.FarTier = farTier;
            if (WriteRecord(st.Buf, ref w, in snap, anchor, step, baseTick, farTier))
                records++;
            // Не влезла в int16 даже на выбранном шаге (корабль за дальним
            // краем тира из-за гистерезиса) — пропускаем, придёт в след. тике.
        }

        // Пилотская запись своего корабля — в конец near/mid-батча (не в far).
        if (ownSlot >= 0 && !farTier)
        {
            if (w + ShipRecordCodec.PilotRecordSize > BroadcasterCore.BatchPayloadCap)
                Flush();
            ref var snap = ref GetSnapshot(ownSlot);
            snap.ForPilot = true;
            snap.FarTier = false;
            if (WriteRecord(st.Buf, ref w, in snap, anchor, step, baseTick, false))
            {
                hasPilot = true;
                records++;
            }
            snap.ForPilot = false;
        }

        Flush();

        void Flush()
        {
            if (records == 0) { w = headerMax; return; }
            byte f = flags;
            if (hasPilot) f |= ShipRecordCodec.FlagHasPilotRecord;
            // Якорь пишем в каждый пакет (FlagHasAnchor): 12 Б на пакет ≈ 1 %,
            // оптимизация «якорь только при смене» — позже (экономия не критична,
            // а рассинхрон кэша якоря tier-запаздывает сходимость).
            ShipRecordCodec.WriteHeaderWithAnchor(
                st.Buf.AsSpan(0, headerMax), baseTick, records, f, step, anchor);
            var payload = new byte[w];
            Array.Copy(st.Buf, payload, w);
            // Godot 4.7: Variant принимает byte[] напрямую (PackedByteArray
            // в C#-привязке 4.7 заменён на System.Byte[]).
            // ВАЖНО: RpcId вызываем на СЕБЕ (SnapshotBroadcaster), а не на
            // NetworkManager: путь узла в RPC-пакете берётся от того узла, на
            // котором вызван RpcId, а RpcSnapshotBatch определён здесь. Путь
            // `/NetworkManager/SnapshotBroadcaster` одинаков на сервере и
            // клиенте (узел создаётся в NetworkManager._Ready на обеих сторонах).
            RpcId(peerId, nameof(RpcSnapshotBatch), payload);
            nm.CountBatchSent(records);
            _sentPackets++; _sentRecords += records;
            w = headerMax; records = 0; hasPilot = false;
        }
    }

    /// <summary>Аварийный полный снимок одного корабля пилоту (шаг R5). Отвечает
    /// на `RpcRequestFullSnapshot`: один батч с единственной пилотской записью,
    /// якорь = позиция корабля (свежий, не тирный), шаг near. Реконструкция
    /// эталона после потери near-батчей по ненадёжному каналу. Снимок строим
    /// ЗАНОВО из NetState (кэш слотов живёт только внутри ServerTick).</summary>
    private void SendFullSnapshotTo(NetworkManager nm, int peerId, ClientStream st)
    {
        var own = nm.FindShip(st.OwnShipNetId);
        if (own?.Net == null) return;

        var n = own.Net;
        var s = new ShipSnapshotQ
        {
            NetId = (ushort)n.ShipNetId,
            SimTick = n.SimTick,
            Position = new Vec3(n.NetPosition.X, n.NetPosition.Y, n.NetPosition.Z),
            Rotation = new Quat(n.NetRotation.X, n.NetRotation.Y, n.NetRotation.Z, n.NetRotation.W),
            LinearVelocity = new Vec3(n.NetLinearVelocity.X, n.NetLinearVelocity.Y, n.NetLinearVelocity.Z),
            AngularVelocity = new Vec3(n.NetAngularVelocity.X, n.NetAngularVelocity.Y, n.NetAngularVelocity.Z),
            AppliedTick = n.AppliedTick,
            AppliedMask = n.AppliedMask,
            ForPilot = true,
            FarTier = false,
        };

        var anchor = s.Position; // якорь = сама позиция: смещение ~0, квант near-шага
        int headerMax = ShipRecordCodec.HeaderSize + ShipRecordCodec.AnchorBytes;
        if (st.Buf.Length < headerMax + ShipRecordCodec.PilotRecordSize)
            Array.Resize(ref st.Buf, headerMax + ShipRecordCodec.PilotRecordSize);

        int p = ShipRecordCodec.WriteHeaderWithAnchor(
            st.Buf.AsSpan(0, headerMax), s.SimTick, 1, ShipRecordCodec.FlagHasPilotRecord,
            NearStep, anchor);
        ShipRecordCodec.Write(st.Buf.AsSpan(p), in s, anchor, s.SimTick, NearStep);
        p += ShipRecordCodec.PilotRecordSize;

        var payload = new byte[p];
        Array.Copy(st.Buf, payload, p);
        // Тот же канал 2 (Unreliable), что и обычные батчи: запрос надёжный, а
        // ответ — ещё одна попытка доставить эталон; при повторной потере клиент
        // через кулдаун (_gapWatch) запросит снова.
        RpcId(peerId, nameof(RpcSnapshotBatch), payload);
        nm.CountBatchSent(1);
        _sentPackets++; _sentRecords++;
    }

    /// <summary>Наименьший шаг posStep·2^k (до 16×), при котором ВСЕ смещения
    /// тиры влезают в int16 от якоря. Если и на 16× не влезают — возвращаем
    /// максимум: непоместившиеся записи пропустятся при записи (WriteRecord).</summary>
    private float PickTierStep(List<int> slots, int ownSlot, Vec3 anchor, float posStep)
    {
        float step = posStep;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            bool allFit = true;
            foreach (int slot in slots)
            {
                if (slot == ownSlot) continue; // пилотская запись: смещение ~0
                ref var snap = ref GetSnapshot(slot);
                var off = new Vec3(snap.Position.X - anchor.X,
                                   snap.Position.Y - anchor.Y,
                                   snap.Position.Z - anchor.Z);
                if (!Quant.FitsInt16(off, step)) { allFit = false; break; }
            }
            if (allFit) return step;
            step *= 2f;
        }
        return step;
    }

    /// <summary>Записать одну запись при фиксированном шаге пакета. Возвращает
    /// false, если смещение не влезает в int16 (запись пропускается).</summary>
    private static bool WriteRecord(byte[] buf, ref int w, in ShipSnapshotQ snap,
                                    Vec3 anchor, float step, int baseTick, bool farTier)
    {
        int size = ShipRecordCodec.RecordSize(snap.ForPilot, farTier);
        int n = ShipRecordCodec.Write(buf.AsSpan(w, size), in snap, anchor, baseTick, step);
        if (n <= 0) return false;
        w += n;
        return true;
    }

    /// <summary>Снимок слота на этот репликационный тик (кэш: один Ship читаем
    /// один раз, даже если он в тирах нескольких клиентов).</summary>
    private ref ShipSnapshotQ GetSnapshot(int slot)
    {
        if (!_snapValid[slot])
        {
            _snapValid[slot] = true;
            var n = _bodies[slot].Net!;
            _snapCache[slot] = new ShipSnapshotQ
            {
                NetId = (ushort)n.ShipNetId,
                SimTick = n.SimTick,
                Position = new Vec3(n.NetPosition.X, n.NetPosition.Y, n.NetPosition.Z),
                Rotation = new Quat(n.NetRotation.X, n.NetRotation.Y, n.NetRotation.Z, n.NetRotation.W),
                LinearVelocity = new Vec3(n.NetLinearVelocity.X, n.NetLinearVelocity.Y, n.NetLinearVelocity.Z),
                AngularVelocity = new Vec3(n.NetAngularVelocity.X, n.NetAngularVelocity.Y, n.NetAngularVelocity.Z),
                AppliedTick = n.AppliedTick,
                AppliedMask = n.AppliedMask,
            };
        }
        return ref _snapCache[slot];
    }

    // ------------------------------------------------------- учёт клиентов

    /// <summary>Сервер: зарегистрировать клиента (peer ≥ 2).</summary>
    public void AddClient(int peerId)
    {
        if (!_clients.ContainsKey(peerId))
            _clients[peerId] = new ClientStream();
    }

    public void RemoveClient(int peerId) => _clients.Remove(peerId);

    /// <summary>Сменить свой корабль клиента (спавн/респавн/смена корабля) —
    /// новый якорь интереса и адрес пилотской записи.</summary>
    public void SetOwnShip(int peerId, int shipNetId)
    {
        if (_clients.TryGetValue(peerId, out var st))
            st.OwnShipNetId = shipNetId;
    }

    // ---------------------------------------------------------- приём (клиент)

    /// <summary>Один wire-пакет батча (§2.2): заголовок + якорь + записи.
    /// Unreliable, канал 2: потеря восстанавливается следующим пакетом тира.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable, TransferChannel = 2)]
    public void RpcSnapshotBatch(byte[] payload)
    {
        if (NetworkManager.Instance?.IsServer == true)
            return; // батчи шлёт только сервер; клиент их лишь принимает
        ApplyBatch(payload);
    }

    /// <summary>Шов автеста (§3.9 п.15): «пакет пришёл» без UDP — тот же боевой
    /// путь распаковки, что у RPC.</summary>
    public void FeedBatchForTest(byte[] payload) => ApplyBatch(payload);

    private void ApplyBatch(ReadOnlySpan<byte> data)
    {
        var nm = NetworkManager.Instance;
        if (nm == null || data.Length < ShipRecordCodec.HeaderSize) return;

        // Якорь всегда в пакете (FlagHasAnchor на записи) — кэш по тирам не нужен.
        Vec3 anchor = Vec3.Zero;
        int off = ShipRecordCodec.ReadHeaderWithAnchor(data, out int baseTick, out int count,
                                                       out byte flags, out float posStep, ref anchor);
        if (count <= 0 || count > 4096) return; // битый пакет — молча гасим (Unreliable)

        bool farTier = (flags & ShipRecordCodec.FlagAllFarTier) != 0;
        bool hasPilot = (flags & ShipRecordCodec.FlagHasPilotRecord) != 0;

        int consumed = 0;
        int applied = 0;
        for (int i = 0; i < count; i++)
        {
            bool forPilot = hasPilot && !farTier && i == count - 1;
            int n = ShipRecordCodec.Read(data.Slice(off + consumed), out ShipSnapshotQ s,
                                         anchor, baseTick, posStep, forPilot, farTier);
            if (n == 0) break; // хвост короче записи — пакет обрезан, стоп
            consumed += n;
            var ship = nm.FindShip(s.NetId);
            if (ship != null)
            {
                ship.ApplyBatchRecord(in s);
                applied++;
                // Пилотская запись своего корабля = свежий эталон: сбрасываем
                // сторожевой счётчик пропадания (шаг R5).
                if (forPilot && s.NetId == _clientOwnShipNetId)
                    _gapWatch.OnFresh();
            }
        }
        nm.CountBatchRecv(applied);
    }

    /// <summary>Клиент просит полный снимок своего корабля (шаг R5): сработал
    /// сторожевой счётчик пропадания пилотской записи. Сервер отвечает одним
    /// пилотским батчем (`SendFullSnapshotTo`). Надёжный канал 1 (как ввод):
    /// сам запрос терять нельзя. Гейт безопасности: отвечаем только если
    /// запрошенный корабль — действительно пилотируемый этим пиром (иначе
    /// клиент мог бы выкачивать чужие полные снимки).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcRequestFullSnapshot(int shipNetId)
    {
        var nm = NetworkManager.Instance;
        if (nm == null || !nm.IsServer) return; // полный снимок шлёт только сервер

        int peerId = Multiplayer.GetRemoteSenderId();
        if (!_clients.TryGetValue(peerId, out var st)) return; // не наш клиент

        // Гейт: просить можно только свой корабль.
        if (shipNetId != st.OwnShipNetId)
        {
            GD.PushWarning($"SnapshotBroadcaster: пир {peerId} просит полный снимок " +
                           $"чужого корабля #{shipNetId} (его свой #{st.OwnShipNetId}) — отказ");
            return;
        }
        SendFullSnapshotTo(nm, peerId, st);
    }

    /// <summary>Шов автеста (§3.9 п.15): «пир запросил полный снимок» без UDP —
    /// тот же боевой путь ответа, что у RPC, но с явным peerId вместо
    /// GetRemoteSenderId (в headless-тесте реального соединения нет).</summary>
    public void FeedRequestFullSnapshotForTest(int peerId, int shipNetId)
    {
        var nm = NetworkManager.Instance;
        if (nm == null || !_clients.TryGetValue(peerId, out var st)) return;
        if (shipNetId != st.OwnShipNetId) return;
        SendFullSnapshotTo(nm, peerId, st);
    }

    // ------------------------------------------------------------- диагностика

    public void GetStats(out int packets, out int records)
    {
        packets = _sentPackets; records = _sentRecords;
    }
}
