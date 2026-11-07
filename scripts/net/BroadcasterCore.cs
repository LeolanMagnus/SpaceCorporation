using System;
using System.Collections.Generic;

namespace SpaceCorporation.Net;

/// <summary>
/// Ядро серверного рассылщика снимков (шаг R3 плана net-refactor-plan.md).
///
/// Отвечает за ТРИ вещи и ничего больше:
///   1) календарь репликационных тиков: near публикуется чаще всего (top Гц),
///      mid/far — на своих тиках (период = top / hz, целочисленный);
///   2) упаковка записей тира в один переиспользуемый буфер и нарезка его на
///      MTU-вместимые батчи (один RPC-пакет на батч);
///   3) учёт записей/пакетов/байт для метрик.
///
/// Ядро НЕ зависит от Godot и НЕ знает про транспорт: оно считает байты и
/// пакеты по той же модели, что tools/traffic_estimate.py (нормативный бюджет
/// §4). Godot-узел `SnapshotBroadcaster` вызывает это ядро и гонит готовые
/// байты через RpcId на канале 2.
///
/// Размер записи здесь — ПАРАМЕТР (RecordSize у потока), а не жёсткая константа
/// кодека: R3 проверяет паковку/пейсинг, кодирование записи — это R1. Тест
/// подаёт консервативные размеры из оценки (30/14 Б), чтобы свериться с
/// нормативом; реальный кодек даёт 21/14 Б — то есть ЕЩЁ дешевле (в бюджете).
/// </summary>
public sealed class BroadcasterCore
{
    // Транспортные константы — те же, что в traffic_estimate.py.
    public const int Mtu = 1400;
    public const int EnetOverhead = 4;
    public const int UdpIpOverhead = 28;
    public const int RpcPath = 12;
    public const int RpcMethod = 2;
    /// <summary>Заголовок батча: базовый тик + число записей (см. ShipRecordCodec).</summary>
    public const int HeaderSize = 8;

    /// <summary>Полезная нагрузка одного wire-пакета батча (байт записей).</summary>
    public const int BatchPayloadCap =
        Mtu - (EnetOverhead + UdpIpOverhead + RpcPath + RpcMethod + HeaderSize); // = 1346

    /// <summary>Один тир интереса для одного клиента: сколько тел, с какой
    /// частотой и какого размера запись.</summary>
    public struct TierStream
    {
        public int Count;        // тел в тире
        public int Hz;           // частота публикации (Гц)
        public int RecordSize;   // байт на запись

        public TierStream(int count, int hz, int recordSize)
        {
            Count = count; Hz = hz; RecordSize = recordSize;
        }
    }

    /// <summary>Верх календаря = максимальная частота тиров (обычно near, 20 Гц).
    /// Все периоды целые относительно него.</summary>
    public static int TopHz(IReadOnlyList<TierStream> tiers)
    {
        int top = 0;
        foreach (var t in tiers) if (t.Hz > top) top = t.Hz;
        return top;
    }

    /// <summary>Публикуется ли тир на репликационном тике t (0..top-1).</summary>
    public static bool TierFires(in TierStream tier, int top, int t)
    {
        int period = top / tier.Hz;      // целочисленный период в тиках near
        return period <= 1 || (t % period == 0);
    }

    /// <summary>Итог одной секунды по календарю: записей, wire-пакетов и байт
    /// «вниз» на клиента (payload + обвязка пакетов + заголовки + события).
    /// Считает ровно так же, как target_model() в traffic_estimate.py, — чтобы
    /// стенд R3 сверялся с нормативом §4.</summary>
    public static (long records, long packets, long downBytes) MeasureSecond(
        IReadOnlyList<TierStream> tiers, double perRecordExtraBytes = 0.0,
        double eventBytesPerSec = 0.0)
    {
        int top = TopHz(tiers);
        long records = 0, packets = 0;
        double payload = 0.0;

        for (int t = 0; t < top; t++)
        {
            double vol = 0.0;            // байт записей на этом тике
            foreach (var tier in tiers)
            {
                if (!TierFires(tier, top, t)) continue;
                vol += tier.Count * (tier.RecordSize + perRecordExtraBytes);
                records += tier.Count;
            }
            payload += vol;
            packets += Math.Max(1, (int)Math.Ceiling(vol / BatchPayloadCap));
        }

        // wire-байты: payload + обвязка каждого пакета + заголовок батча + события.
        double down = payload
                    + packets * (EnetOverhead + UdpIpOverhead)
                    + packets * HeaderSize
                    + eventBytesPerSec;
        return (records, packets, (long)Math.Round(down));
    }

    /// <summary>Нарезает байты записей одного тика на батчи по MTU. Возвращает
    /// число пакетов. Реальный Godot-узел пишет записи в буфер и льёт их в
    /// RpcId порциями по BatchPayloadCap; здесь — та же арифметика нарезки.</summary>
    public static int SplitIntoBatches(int recordBytes)
        => Math.Max(1, (int)Math.Ceiling(recordBytes / (double)BatchPayloadCap));

    /// <summary>Логические обновления → wire-пакеты с учётом пачинга/фрагментации
    /// (та же формула, что packets_per_sec() в traffic_estimate.py): в один MTU
    /// влезает floor(MTU / (размер_обновления + обвязка_RPC)) обновлений.</summary>
    public static double PacketsPerSec(double updatesPerSec, int bytesPerUpdate)
    {
        int perPacket = Math.Max(1, Mtu / (bytesPerUpdate + RpcPath + RpcMethod));
        return updatesPerSec / perPacket;
    }

    /// <summary>Полезные байты + обвязка пакетов (та же формула, что wire_bytes()).</summary>
    public static double WireBytes(double payloadBytesPerSec, double packets)
        => payloadBytesPerSec + packets * (EnetOverhead + UdpIpOverhead);

    // Апстрим (ввод пилота + запросы выстрелов) — константы норматива §4.
    /// <summary>Размер кадра ввода InputFrame, байт.</summary>
    public const int InputFrameBytes = 16;
    /// <summary>Кадры ввода на пилота в секунду (60 Гц).</summary>
    public const int InputHz = 60;
    /// <summary>Запросы выстрелов на пилота в секунду (8 выстрелов/с).</summary>
    public const int ShotsPerSec = 8;
    /// <summary>Размер запроса выстрела: path+method+netId(3)+tick(3)+aim(3×f32=12).</summary>
    public const int ShotReqBytes = RpcPath + RpcMethod + 3 + 3 + 12;

    /// <summary>Апстрим одного клиента за секунду: кадры ввода + запросы
    /// выстрелов. Возвращает (wire-байт/с, wire-пакетов/с). Совпадает с
    /// UP_PAYLOAD/UP_PKTS в traffic_estimate.py.</summary>
    public static (double bytes, double packets) MeasureUpstream()
    {
        double payload = InputHz * (InputFrameBytes + RpcPath + RpcMethod)
                       + ShotsPerSec * ShotReqBytes;
        double packets = PacketsPerSec(InputHz, InputFrameBytes)
                       + PacketsPerSec(ShotsPerSec, ShotReqBytes - RpcPath - RpcMethod);
        return (WireBytes(payload, packets), packets);
    }

    // События стадии 3 (hitscan) и репликация целей по смене — норматив §4.
    /// <summary>Подтверждение попадания RpcOnHitConfirmed: path+method +
    /// targetNetId(3) + damage(2) + hitPoint(3×f32=12) + shooterNetId(3) + tick(3).</summary>
    public const int HitEventBytes = RpcPath + RpcMethod + 3 + 2 + 12 + 3 + 3;
    /// <summary>Подтверждений попадания на клиента в секунду (при 8 выстрелах/с
    /// на игрока и 4 игроках ≈ 6 подтверждений/с доходит до каждого).</summary>
    public const int HitEventsPerClientPerSec = 6;
    /// <summary>Ссылка на цель при смене: VarInt targetId (2 Б). Точку прицела
    /// клиент достраивает сам (цель уже в его снимке), поэтому в реплике едет
    /// только ID — вариант «onchange» норматива.</summary>
    public const int TargetChangeBytes = 2;
    /// <summary>Смен цели в секунду на всё поле: по одной на корабль (400),
    /// ИИ переставляет цель ≈ раз в секунду. Это событие, а не поле записи.</summary>
    public const int TargetChangesPerSec = 400;

    /// <summary>
    /// Клиентский сторожевой счётчик пилотской реплики (шаг R5, §3 шаг R5).
    ///
    /// Пилотская запись своего корабля — эталон реконсиляции (AppliedTick/
    /// AppliedMask). Она приходит в каждом near-батче (20 Гц). Батч идёт по
    /// Unreliable-каналу 2 и может потеряться; если пилотская запись не
    /// приходила дольше `Timeout`, пилот теряет эталон и обязан запросить
    /// полный снимок у сервера (`RpcRequestFullSnapshot`) — тот ответит одним
    /// пилотским батчем по тому же каналу.
    ///
    /// Запрос шлётся НЕ чаще, чем раз в `RequestEvery` — иначе при устойчивой
    /// потере (тест: 30 %) клиент устроит шторм запросов. Состояние чистое
    /// (без Godot), чтобы тестировать его без транспорта.
    /// </summary>
    public struct PilotGapWatch
    {
        /// <summary>Порог «запись пропала», с (0.5 с по §3 шаг R5).</summary>
        public const float DefaultTimeout = 0.5f;
        /// <summary>Кулдаун между запросами полного снимка, с.</summary>
        public const float DefaultRequestEvery = 0.5f;
        /// <summary>Порог «запись пропала», с (0.5 с по §3 шаг R5).</summary>
        public float Timeout;
        /// <summary>Минимальный интервал между запросами полного снимка, с.</summary>
        public float RequestEvery;

        private float _fresh;     // время с последней принятой пилотской записи
        private float _sinceReq;  // время с последнего отправленного запроса

        /// <summary>Явный параметрический конструктор. В этом тулчейне инициализаторы
        /// полей struct НЕ выполняются для неявного `new S()` (проверено изолированно:
        /// `new S()` даёт нули), поэтому пороги задаём здесь, а не инициализаторами.
        /// `default(PilotGapWatch)` по-прежнему нулевой — в бою используется только
        /// `new(...)`.</summary>
        public PilotGapWatch()
        {
            Timeout = DefaultTimeout; RequestEvery = DefaultRequestEvery;
            _fresh = 0f; _sinceReq = 0f;
        }

        public PilotGapWatch(float timeout, float requestEvery)
        {
            Timeout = timeout; RequestEvery = requestEvery;
            _fresh = 0f; _sinceReq = 0f;
        }

        /// <summary>Отметить, что пилотская запись пришла (свежий эталон).</summary>
        public void OnFresh() => _fresh = 0f;

        /// <summary>Продвинуть на `dt`. Возвращает true, если СЕЙЧАС надо
        /// отправить запрос полного снимка (порог пройден и кулдаун вышел).
        /// Возврат true сам сбрасывает кулдаун запроса.</summary>
        public bool Tick(float dt)
        {
            _fresh += dt;
            _sinceReq += dt;
            if (_fresh >= Timeout && _sinceReq >= RequestEvery)
            {
                _sinceReq = 0f;
                return true;
            }
            return false;
        }

        /// <summary>Сколько секунд пилот уже без эталона (для метрик/отладки).</summary>
        public readonly float FreshAge => _fresh;
    }
}
