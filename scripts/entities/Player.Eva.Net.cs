using Godot;

namespace SpaceCorporation;

/// <summary>
/// Сетевая поза-реплика EVA (eva-plan, Блок D, Д1). Файл — часть
/// <c>partial class Player</c>: broadcast/apply живут здесь, точка входа
/// <see cref="PublishEvaPoseFrame"/> вызывается из <c>Player._PhysicsProcess</c>.
///
/// МОДЕЛЬ (зеркало Ship-репликации, но проще): у каждого игрока узел-авторитет =
/// его пир (<see cref="Player._isLocal"/>, Player.cs:93), поэтому владелец игрока
/// сам — авторитет своей позы и САМ её рассылает. Никакого серверного «пересчёта»
/// позы скафандра нет и не нужен: физика EVA идёт на клиенте-владельце (V26), а
/// сервер в этой модели — тоже игрок со своей позой. Поэтому Д1 = авторитет-
/// broadcast: владелец каждый N-й физшаг шлёт свою позу всем, остальные пирят
/// применяют её на своих копиях узла того же пути.
///
/// ЧЕМ ОТЛИЧАЕТСЯ ОТ Ship: там поза корабля авторитетна у СЕРВЕРА (В14=B, пилот
/// только шлёт ввод), поэтому реплика летит сервер→клиенты и реконсилируется по
/// кадрам ввода. У игрока авторитет = сам игрок, реконсиляции нет: чужое тело на
/// пире НЕ двигается (StepEva гейтится <c>_isLocal</c> и для не-владельца делает
/// nothing), значит между пакетами оно статично, а каждое применение — «прыжок»
/// к присланной pozе. При 30 Гц это плавно; при потерях/задержке прыжок режет
/// <see cref="EvaCorrectionDistM"/> — дальше поза ставится жёстко (teleport).
///
/// ПОТОК:
///   • BROADCAST (только владелец, <c>_isLocal</c>): каждый
///     <see cref="NetPublishEveryN"/>-й физшаг, если игрок СВОБОДЕН (не в кресле)
///     и есть реальный peer — шлём GlobalPosition, ориентацию тела (кватернион
///     базиса), режим, скорость, расходники. В кресле (Seat) поза =transform
///     родителя-точки, её реплицирует AttachTo/ApplyAttach (Блок C), дублировать
///     позу через ненадёжный канал нельзя (рассинхрон иерархии и позы).
///   • APPLY (не-владелец): принять от авторитета этого игрока
///     (<c>GetRemoteSenderId() == GetMultiplayerAuthority()</c>), гейт по монотонному
///     тиковому счётчику (unreliable переупорядочен), затем поставить GlobalTransform
///     (позиция + базис). Дальше > порога — жёсткий snap + сброс физ-интерполяции.
///
/// ОFFLINE / без peer: <c>HasRealPeer == false</c> ⇒ broadcast не запускается
/// вообще (Rpc в никуда паникует), apply не вызывается. Весь 986-тестовый офлайн-
/// прогон не трогает сетевой путь — безопасно.
/// </summary>
public partial class Player
{
    // ─── константы репликации EVA ─────────────────────────────────────────────
    /// <summary>Публиковать позу каждые N физических шагов: 2 ⇒ 30 Гц при фикс.
    /// 60 Гц. Это половина от корабельной 60-Гц репликации: EVA-игрок двигается
    /// медленно (потолок 10 м/с) и реконсиляции нет, поэтому 30 Гц достаточно и
    /// дешевле по бюджету пакетов (В6).</summary>
    private const int NetPublishEveryN = 2;

    /// <summary>Дальше этого расстояния (м) между текущей позой реплики и
    /// пришедшей — телепорт (жёсткий snap): прыжок больше порога означает потерю
    /// или долгое молчание unreliable-канала, и «дотягивать» его плавно нельзя —
    /// наблюдатель видел бы, как чужой игрок ползёт к истине секунды.</summary>
    private const float EvaCorrectionDistM = 2.5f;

    /// <summary>Оценка полезной нагрузки одного поза-пакета (байты аргументов RPC
    /// до транспортовой обвязки): tick(4) + pos(12) + quat(16) + mode(4) +
    /// linVel(12) + fuel(4) + battery(4) + flags(4) = 60. Это консервативная
    /// нижняя оценка Variant-упаковки Godot; служит для оценки бюджета «EVA-
    /// поза/с» (Д1 eva-plan). Реальный wire с ENet-обвязкой чуть выше.</summary>
    private const int EvaPosePayloadBytes = 60;

    /// <summary>Суммарно опубликовано поза-байтов всеми EVA-игроками этого пира
    /// (кумулятив). Снимается раз в секунду NetworkManager для строки «Eva»
    /// (Д1 eva-plan: «+eva/s: KB, чтобы померить цену»).</summary>
    private static long _evaNetBytesTotal;

    /// <summary>Кumulativ поза-байтов (для rate-счётчика в GetNetInfoLine).</summary>
    public static long EvaNetBytesTotal() => _evaNetBytesTotal;

    // ─── состояние broadcast/apply (только для сетевого пути) ─────────────────
    /// <summary>Фаза-счётчик периода публикации: публикуем, когда
    /// <c>++_evaPubPhase % NetPublishEveryN == 0</c>. Локален владельцу.</summary>
    private int _evaPubPhase;

    /// <summary>Монотонный счётчик отправленных поз этого игрока — им гейтуем
    /// переупорядоченные/дубли unreliable-пакетов на принимающей стороне.</summary>
    private uint _evaPubTick;

    /// <summary>Последний принятый тик позы на не-владельце (гейт монотонности).</summary>
    private uint _evaLastRecvTick;

    /// <summary>Режим, присланный последней позой (для HUD/отладки на пире;
    /// локальный <c>_mode</c> у не-владельца не используется).</summary>
    private EvaMode _remoteEvaMode = EvaMode.Jetpack;
    /// <summary>Реплика расходников чужого игрока (Д2 сервер-как-источник later;
    /// сейчас едет попутно в позе-пакете и показывается на пире).</summary>
    private float _remoteFuel = SuitModel.MassFuelTotal;
    private float _remoteBattery = SuitModel.BatteryTotal;

    /// <summary>Последняя принятая скорость чужого игрока — только для отладки
    /// (телепорт-модель её не интегрирует: между пакетами тело стоит).</summary>
    private Vector3 _remoteEvaVelocity;

    // ==================================================================
    // BROADCAST (владелец)
    // ==================================================================
    /// <summary>Вызывается из <c>Player._PhysicsProcess</c> каждый физшаг
    /// (до EVA-ветки и её ранних return'ов — публикация обязана идти и в покое).
    /// Ничего не делает, если это не владелец, нет реального peer, или игрок
    /// сидит в кресле (его позу реплицирует иерархия AttachTo).</summary>
    private void PublishEvaPoseFrame()
    {
        // Только локально-владелецный игрок публикует СВОЮ позу (авторитет=он сам).
        if (!_isLocal)
            return;
        // Без реального соединения некому слать (Rpc в никуда с null-peer паникует
        // и печатает ERROR — тот же баг-класс, что BUG-011 в Ship).
        if (NetworkManager.Instance is not { HasRealPeer: true })
            return;
        // В кресле поза = transform точки крепления; её уже несёт AttachTo/
        // ApplyAttach (надёжный канал, иерархия). Дублировать по креслу через
        // unreliable позу = гонка двух источников над одним телом.
        if (_mode == EvaMode.Seat)
            return;
        // Пока игрок ждёт дефолтную точку крепления на спавне, он ещё не «в бою»:
        // тот же гейт, что включает EVA-шаг (Player.cs:257) — иначе спавн-кадры
        // шлют позу из нулевого трансформa раньше, чем тело поставлено на спавн.
        if (AutoAttachOnSpawn && _awaitingDefaultPoint)
            return;

        // Период 1 из NetPublishEveryN: при =2 это каждый 2-й шаг = 30 Гц.
        if (++_evaPubPhase % NetPublishEveryN != 0)
            return;

        // Ориентация тела → кватернион базиса (GlobalTransform.Basis везёт и мировой
        // поворот, и поворот, полученный Reparent'ом от корабля, — на свободном
        // теле это просто базис EVA-тела).
        Quaternion bodyQuat = GlobalTransform.Basis.GetRotationQuaternion();
        uint tick = ++_evaPubTick;

        // Расходники пакуем в проценты-байты (0..100): на wire 0..100 целое,
        // точность 1 % достаточна для HUD-индикатора расходников (Д2 перенесёт
        // источник на сервер, но формат поля тот же).
        Rpc(nameof(RpcEvaState), tick, GlobalPosition, bodyQuat, (int)_mode,
            Velocity, (int)_suit.Fuel, (int)_suit.Battery, 0u);

        // Учёт полезной нагрузки поза-пакета для строки «Eva» (Д1 eva-plan:
        // измерить цену +KB/s на EVA-игрока). Считаем до обвязки ENet; NetworkManager
        // снимает кумулятив раз в секунду так же, как ENet-счётчики.
        _evaNetBytesTotal += EvaPosePayloadBytes;
    }

    // ==================================================================
    // APPLY (не-владелец)
    // ==================================================================
    /// <summary>Принять позу-реплику EVA-игрока. Имя метода == имя RPC, поэтому
    /// на принимающем пире исполняется копия узла того же пути. Гейты (§2.5):
    /// принимает только не-владелец (у владельца <c>CallLocal=false</c> и он сам
    /// рассылает); прислать позу может только авторитет этого игрока; тик обязан
    /// строго расти (unreliable переупорядочен).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable, TransferChannel = 2)]
    public void RpcEvaState(uint tick, Vector3 pos, Quaternion bodyQuat, int mode,
                            Vector3 linVel, int fuel, int battery, uint flags)
    {
        // Владелец этой копии узла не принимает чужие позы (он их сам шлёт).
        if (_isLocal)
            return;
        // Гейт отправителя: только авторитет игрока вправе сообщать его позу
        // (иначе сторонний пир «нарисовал бы» чужого игрока где угодно).
        if (Multiplayer.GetRemoteSenderId() != GetMultiplayerAuthority())
            return;
        // Монотонность: просроченный/дублирующийся пакет не должен откатывать позу.
        if (tick <= _evaLastRecvTick)
            return;
        _evaLastRecvTick = tick;

        _remoteEvaMode = (EvaMode)mode;
        _remoteEvaVelocity = linVel;
        _remoteFuel = Mathf.Clamp(fuel, 0f, SuitModel.MassFuelTotal);
        _remoteBattery = Mathf.Clamp(battery, 0f, SuitModel.BatteryTotal);
        // flags зарезервирован (фонарь/обогрев/ботинки-вкл) — Д2 выведет в HUD.
        // Пока не используем, но параметр держим:wire-формат зафиксирован.

        // Свободный EVA-игрок на пире — ребёнок _worldRoot (он не в кресле, иначе
        // позы бы не слались), поэтому мировая поза пишется прямо в GlobalTransform.
        float jump = GlobalPosition.DistanceTo(pos);
        GlobalTransform = new Transform3D(new Basis(bodyQuat), pos);
        if (jump > EvaCorrectionDistM)
            // Жёсткий snap: сбрасываем физ-интерполяцию, чтобы телепорт не
            // «размазался» по кадрам, если интерполяция вдруг включена.
            ResetPhysicsInterpolation();
    }

    // ==================================================================
    // Тестовые швы (Stage7Test)
    // ==================================================================
    /// <summary>Последний принятый тик позы на не-владельце (0 = пакетов не было).</summary>
    public uint EvaNetLastRecvTick() => _evaLastRecvTick;
    /// <summary>Сколько поз уже опубликовано владельцем (счётчик тика broadcast).</summary>
    public uint EvaNetPubTick() => _evaPubTick;
    /// <summary>Режим, пришедший последней поза-репликой (для assert'ов на пире).</summary>
    public EvaMode EvaRemoteMode() => _remoteEvaMode;
    /// <summary>Реплика fuel/battery чужого игрока (для assert'ов Д1/Д2 на пире).</summary>
    public float EvaRemoteFuel() => _remoteFuel;
    public float EvaRemoteBattery() => _remoteBattery;

    /// <summary>Шов автотеста (§3.9 п.15): «поза пришла от пира X» без UDP. В
    /// headless нет реального соединения, <c>GetRemoteSenderId()</c> вне RPC = 0,
    /// поэтому тест дергает тот же apply, что и RPC, явно указывая пира-отправителя.</summary>
    public void TestApplyEvaState(int senderPeer, uint tick, Vector3 pos, Quaternion bodyQuat,
                                  int mode, Vector3 linVel, int fuel, int battery)
    {
        if (_isLocal)
            return;
        if (senderPeer != GetMultiplayerAuthority())
            return;
        if (tick <= _evaLastRecvTick)
            return;
        _evaLastRecvTick = tick;
        _remoteEvaMode = (EvaMode)mode;
        _remoteEvaVelocity = linVel;
        _remoteFuel = Mathf.Clamp(fuel, 0f, SuitModel.MassFuelTotal);
        _remoteBattery = Mathf.Clamp(battery, 0f, SuitModel.BatteryTotal);
        float jump = GlobalPosition.DistanceTo(pos);
        GlobalTransform = new Transform3D(new Basis(bodyQuat), pos);
        if (jump > EvaCorrectionDistM)
            ResetPhysicsInterpolation();
    }
}
