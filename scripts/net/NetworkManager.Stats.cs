using System;
using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial NetworkManager: вырезано из NetworkManager.cs при partial-декомпозиции.
public partial class NetworkManager : Node
{
    private long _pktsSent, _pktsRecv, _bytesSent, _bytesRecv;

    // Разделение трафика на «репликацию» и «ввод» (решение В6 = C, §2.7/§2.9).
    // Порог «< 50 пакетов/с на игрока» в prototype-plan считался БЕЗ учёта ввода:
    // он про репликационные снимки. Кадры ввода пилота (RpcSubmitInput) — отдельный
    // бюджет 60/с.
    //
    // ВАЖНО про метрику. Хостовый счётчик ENet (PopStatistic) считает ВСЕ пакеты
    // транспорта, включая ACK надёжного канала и фрагментацию — это НЕ «логическая
    // репликация», а обвязка соединения. Порог (г) про ЛОГИЧЕСКИЕ снимки состояния,
    // поэтому репликацию считаем ЯВНО: Ship-не-авторитет зовёт CountReplicationRecv
    // ровно когда получает НОВЫЙ снимок (сдвиг Net.SimTick — тот же признак, по
    // которому идёт реконсиляция/интерполяция). Ввод считаем ЯВНО на отправке и
    // приёме (CountInputSent/Recv). Хостовые ENet-счётчики оставляем как
    // «wire»-диагностику (сколько реально ушло пакетов с учётом ACK), но метрика
    // (г) читается по логическим repl/s и input/s, а не по wire.
    private long _inputPktsSent, _inputPktsRecv;
    private long _replRecv;
    // Счётчики батчей снапшотов (шаг R3): пакеты и записи репликационного
    // батча на клиента. Пороги §4: ≤ 150 пакетов/с и ≤ 5300 записей/с.
    private long _batchPktsSent, _batchPktsRecv;
    private long _batchRecsSent, _batchRecsRecv;
    // Какие ShipNetId дали сдвиг SimTick за текущую секунду — чтобы посчитать
    // репликацию НА ИГРОКА (порог §2.7 «< 50 пакетов/с на игрока»): клиент видит
    // все корабли (N × 30/с), но бюджет меряется на одного игрока = 30/с.
    private readonly System.Collections.Generic.HashSet<int> _replShipsThisSec = new();

    // Трафик EVA-позы (Д1 eva-plan): байты поза-пакетов, снятые за последнюю
    // секунду. Кумулятив ведёт Player.Eva.Net.cs; снимается вместе с ENet-
    // счётчиками (UpdateTrafficStats, раз в секунду), поэтому в GetNetInfoLine
    // (её зовёт и HUD чаще раза в секунду) значение стабильно, а не рвётся.
    private long _evaNetBytesLast, _evaNetBytesPerSec;

    /// <summary>Пилот отправил кадр ввода — учесть в бюджете ввода (не репликации).</summary>
    public void CountInputSent() => _inputPktsSent++;

    /// <summary>Сервер принял кадр ввода — учесть в бюджете ввода.</summary>
    public void CountInputRecv() => _inputPktsRecv++;

    /// <summary>Сервер отправил один wire-пакет батча снапшотов с `records`
    /// записями — учесть в бюджете репликации (§4: пакеты/с и записи/с).</summary>
    public void CountBatchSent(int records)
    {
        _batchPktsSent++;
        _batchRecsSent += records;
    }

    /// <summary>Клиент принял один wire-пакет батча с `records` записями.</summary>
    public void CountBatchRecv(int records)
    {
        _batchPktsRecv++;
        _batchRecsRecv += records;
    }

    /// <summary>Пир получил НОВЫЙ снимок состояния корабля `shipNetId` (сдвиг
    /// SimTick) — один логический репликационный пакет по порогу (г).</summary>
    public void CountReplicationRecv(int shipNetId)
    {
        _replRecv++;
        _replShipsThisSec.Add(shipNetId);
    }

    private void UpdateTrafficStats()
    {
        if (Multiplayer.MultiplayerPeer is not ENetMultiplayerPeer enet || enet.Host == null)
        {
            ResetTrafficStats();
            return;
        }
        var host = enet.Host;
        // PopStatistic возвращает double и обнуляет счётчик — читаем ровно раз в интервал.
        _pktsSent = (long)host.PopStatistic(ENetConnection.HostStatistic.SentPackets);
        _pktsRecv = (long)host.PopStatistic(ENetConnection.HostStatistic.ReceivedPackets);
        _bytesSent = (long)host.PopStatistic(ENetConnection.HostStatistic.SentData);
        _bytesRecv = (long)host.PopStatistic(ENetConnection.HostStatistic.ReceivedData);

        // Д1 eva-plan: поза-пакеты скафандра идут отдельным логическим счётом
        // (не ENet-статистикой — те смешаны со всем трафиком), поэтому снимаем
        // кумулятив Player.Eva.Net.cs в тот же секундный такт.
        long evaNow = Player.EvaNetBytesTotal();
        _evaNetBytesPerSec = evaNow - _evaNetBytesLast;
        _evaNetBytesLast = evaNow;
    }

    /// <summary>Снимок логических счётчиков за секунду: их печатает GetNetInfoLine,
    /// затем накопители обнуляются — в тот же момент, что и PopStatistic.
    /// Возвращает (всего снимков, число разных кораблей, ввод отправл, ввод принят).</summary>
    public (long replRecv, int replShips, long inputSent, long inputRecv,
            long batchPkts, long batchRecs) TakeLogicalTraffic()
    {
        var snap = (_replRecv, _replShipsThisSec.Count, _inputPktsSent, _inputPktsRecv,
                    _batchPktsSent + _batchPktsRecv, _batchRecsSent + _batchRecsRecv);
        _replRecv = 0;
        _replShipsThisSec.Clear();
        _inputPktsSent = 0;
        _inputPktsRecv = 0;
        _batchPktsSent = 0;
        _batchPktsRecv = 0;
        _batchRecsSent = 0;
        _batchRecsRecv = 0;
        return snap;
    }

    private void ResetTrafficStats()
    {
        _pktsSent = _pktsRecv = _bytesSent = _bytesRecv = 0;
        _inputPktsSent = _inputPktsRecv = 0;
        _replRecv = 0;
        _replShipsThisSec.Clear();
        _batchPktsSent = _batchPktsRecv = 0;
        _batchRecsSent = _batchRecsRecv = 0;
        // EVA-поза: baseline = текущий кумулятив, чтобы первый снимок после
        // реконнекта не показал накопленное до соединения (как ENet-счётчики выше).
        _evaNetBytesLast = Player.EvaNetBytesTotal();
        _evaNetBytesPerSec = 0;
    }

    /// <summary>RTT по каждому пиру, мс (ENetPacketPeer.GetStatistic, §2.7).
    /// У клиента пир-сервер в GetPeers() не состоит, но соединение с id = 1 есть —
    /// его спрашиваем отдельно.</summary>
    private string BuildRttString()
    {
        if (Multiplayer.MultiplayerPeer is not ENetMultiplayerPeer enet)
            return "rtt = -";
        var parts = new List<string>();
        foreach (long id in Multiplayer.GetPeers())
            AppendRtt(parts, enet, (int)id);
        if (IsClient)
            AppendRtt(parts, enet, 1);
        return parts.Count == 0 ? "rtt = -" : "rtt = " + string.Join(",", parts);
    }

    private static void AppendRtt(List<string> parts, ENetMultiplayerPeer enet, int id)
    {
        var peer = enet.GetPeer(id);
        if (peer == null)
            return;
        double rtt = peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime);
        parts.Add($"{id}={rtt:F0}ms");
    }

    /// <summary>Пинг до сервера (RTT), мс; -1 — соединения нет (offline/хост без
    /// внешних пиров). Читает статистику ENet — не обнуляет счётчики (в отличие
    /// от PopStatistic). Нужен HUD (Этап H6, левый верх).</summary>
    public int PingMs
    {
        get
        {
            if (Multiplayer.MultiplayerPeer is not ENetMultiplayerPeer enet)
                return -1;
            // У клиента пир-сервер в GetPeers() не состоит — спрашиваем id = 1;
            // на сервере — минимальный RTT среди подключённых клиентов.
            int best = -1;
            if (IsClient)
            {
                var peer = enet.GetPeer(1);
                if (peer != null)
                    best = (int)peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime);
            }
            else
            {
                foreach (long id in Multiplayer.GetPeers())
                {
                    var peer = enet.GetPeer((int)id);
                    if (peer == null)
                        continue;
                    int rtt = (int)peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime);
                    if (best < 0 || rtt < best)
                        best = rtt;
                }
            }
            return best;
        }
    }

    /// <summary>Строка Net для панели и stdout (§2.7): роль, self, пиры, число
    /// игроков, пакеты/с и байты/с (порог плана — < 50 пакетов/с на игрока), RTT.</summary>
    public string GetNetInfoLine()
    {
        var peers = Multiplayer.GetPeers();
        // На сервере пиры = подключившиеся клиенты; у хоста ещё свой игрок (авторитет 1),
        // он не входит в GetPeers() (стадия 0-rework).
        int players = IsServer ? peers.Length + (IsHost ? 1 : 0) : (IsClient ? 1 : 0);
        long packets = _pktsSent + _pktsRecv;
        long bytes = _bytesSent + _bytesRecv;
        // Логические счётчики за секунду (см. TakeLogicalTraffic). Порог (г) §2.7
        // «< 50 пакетов/с на игрока» меряется по РЕПЛИКАЦИОННЫМ СНИМКАМ (решение
        // В6 = C), а не по wire-пакетам ENet (в них входят ACK надёжного канала и
        // фрагментация — обвязка соединения, не состояние). Ввод пилота — отдельный
        // бюджет 60/с (Unreliable), в порог (г) не входит (§2.9).
        var (replRecv, replShips, inputSent, inputRecv, batchPkts, batchRecs) = TakeLogicalTraffic();
        // Репликация НА ИГРОКА: клиент видит все корабли (N × 30/с), но бюджет
        // §2.9 выведен «на игрока» = 30 снимков/с. Делим принятые снимки на число
        // разных кораблей, давших сдвиг SimTick за секунду. replShips==0 → 0.
        long replPerPlayer = replShips > 0 ? replRecv / replShips : 0;
        string key = PlayerKey;
        return $"net: role = {Role.ToString().ToLower()}, self = {SelfId}, " +
               $"key = {key[..System.Math.Min(8, key.Length)]}, " +
               $"peers = [{string.Join(",", peers)}], players = {players}, " +
               $"repl/player/s = {replPerPlayer} (всего снимков {replRecv} по {replShips} кораблям), " +
               $"batch/s = {batchPkts}, rec/s = {batchRecs}, " +
               $"input/s = {inputSent}/{inputRecv}, " +
               $"wire pkts/s = {packets} (tx {_pktsSent}/rx {_pktsRecv}), " +
               $"KB/s = {bytes / 1024f:F1}, " +
               $"eva/s = {_evaNetBytesPerSec / 1024f:F2} KB, {BuildRttString()}, err = {LastError}";
    }

}
