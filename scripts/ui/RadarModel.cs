using System;
using System.Collections.Generic;
using SpaceCorporation.Net;

namespace SpaceCorporation.UI;

/// <summary>
/// Один блип радара после фильтрации/поворота (<see cref="RadarModel"/>).
/// Позиция уже в системе координат панели: ScreenY — «вперёд по курсу»,
/// ScreenX — «вправо», HeightOffset — ΔY (рисочка гравидара). Метры.
/// </summary>
public sealed class RadarBlip
{
    public RadarContact Contact;
    public float ScreenX;
    public float ScreenY;
    public float HeightOffset;
    /// <summary>Видимость: 1 = живой, гаснет к 0 у исчезнувших (fade 0.5 с).</summary>
    public float Fade = 1f;
    /// <summary>Контакт пропал из последней сборки (корабль удалён/вылетел).</summary>
    public bool Stale;
}

/// <summary>
/// Модель радара (radar-plan.md §2.1): реестр контактов → список блипов.
/// Чистая, без Godot, как <c>SpatialIndex</c> — тестируется без движка
/// (tools/RadarModelTest).
///
/// Правила:
///  • <see cref="Sync"/> — раз в 0.2 с (5 Гц) от RadarSource: фильтр по радиусу
///    с гистерезисом ±10 % (по образцу SpatialIndex.QueryTiers: вход 0.9R,
///    удержание 1.1R — блип не мигает на границе), слияние в кэш по NetId;
///  • между сборками <see cref="Advance"/> каждый кадр экстраполирует RelPos по
///    RelVel·Δt (линейно) — блипы движутся плавно, по образцу интерполяции hull;
///  • исчезнувший контакт гаснет за <see cref="FadeTime"/> и удаляется — в X4
///    цель «теряется» плавно;
///  • сортировка по убыванию дистанции: панель рисует список по порядку,
///    ближние ложатся поверх дальних.
/// </summary>
public sealed class RadarModel
{
    /// <summary>Длительность затухания исчезнувшего блипа, с.</summary>
    public const float FadeTime = 0.5f;

    /// <summary>Гистерезис радиуса, доля (вход 0.9R, выход 1.1R).</summary>
    public const float Hysteresis = 0.1f;

    /// <summary>Множитель клиппинга отрисовки: блип рисуют вплоть до 1.1R
    /// (столько же, до чего дотягивает выход гистерезиса в Sync).</summary>
    public static float HysteresisOut() => 1f + Hysteresis;

    /// <summary>Текущий радиус обзора, м (500/1000/2000/4000 у панели).</summary>
    public float Range { get; set; } = DefaultRange;

    public const float DefaultRange = 2000f;

    /// <summary>Курс центра (свой корабль), рад вокруг Y. forward = (sin Y, cos Y).</summary>
    public float ForwardYaw { get; private set; }

    // Кэш блипов по NetId — переживает сборки; затухание и гистерезис живут здесь.
    private readonly Dictionary<int, RadarBlip> _blips = new(64);
    // Собранные флаги «контакт был в этой сборке» (переиспользуется без аллокаций).
    private readonly HashSet<int> _seen = new(64);
    // Отсортированный снимок для панели.
    private readonly List<RadarBlip> _view = new(64);

    public IReadOnlyList<RadarBlip> Blips => _view;

    /// <summary>Число ЖИВЫХ контактов (без затухающих) — подпись под панелью.</summary>
    public int LiveCount { get; private set; }

    // -------------------------------------------------------------- Sync (5 Гц)

    /// <summary>
    /// Слить свежую сборку контактов (RelPos/RelVel уже относительные, от центра
    /// радара). Контакты вне 0.9R (для новых) / 1.1R (для уже видимых) отбрасываются;
    /// кэшированные, которых нет в сборке, помечаются Stale и гаснут в Advance.
    /// </summary>
    public void Sync(IReadOnlyList<RadarContact> contacts)
    {
        _seen.Clear();
        float rIn = Range * (1f - Hysteresis), rOut = Range * (1f + Hysteresis);
        float rIn2 = rIn * rIn, rOut2 = rOut * rOut;

        for (int i = 0; i < contacts.Count; i++)
        {
            RadarContact c = contacts[i];
            float d2 = c.RelPos.LengthSquared;
            _blips.TryGetValue(c.NetId, out RadarBlip? blip);
            bool wasVisible = blip != null && !blip.Stale;

            // Радиусный фильтр с гистерезисом: новый входит до 0.9R,
            // видимый удерживается до 1.1R (по образцу SpatialIndex).
            if (d2 > (wasVisible ? rOut2 : rIn2))
                continue;

            c.Distance = MathF.Sqrt(d2);
            _seen.Add(c.NetId);

            if (blip == null)
            {
                blip = new RadarBlip { Contact = c };
                _blips[c.NetId] = blip;
            }
            else
            {
                blip.Contact = c;
                if (blip.Stale) blip.Stale = false; // контакт вернулся — оживаем
            }
            blip.Fade = 1f;
        }

        // Кого не было в сборке — помечаем stale (гаснет в Advance).
        foreach (KeyValuePair<int, RadarBlip> kv in _blips)
            if (!_seen.Contains(kv.Key))
                kv.Value.Stale = true;
    }

    // ------------------------------------------------------------ Advance (кадр)

    /// <summary>
    /// Кадровый шаг: экстраполяцияRelPos += RelVel·Δt, поворот на курс, fade
    /// исчезнувших, пересборка отсортированного вида. Вызывать каждый кадр.
    /// </summary>
    public void Advance(float dt, float forwardYaw)
    {
        ForwardYaw = forwardYaw;
        float fx = MathF.Sin(forwardYaw), fz = MathF.Cos(forwardYaw);
        // Правый вектор радара: forward × up? (для forward=(sinY,cosY) right=(cosY,-sinY))
        float rx = fz, rz = -fx;

        _view.Clear();
        LiveCount = 0;

        foreach (KeyValuePair<int, RadarBlip> kv in _blips)
        {
            RadarBlip b = kv.Value;
            if (b.Stale)
            {
                b.Fade -= dt / FadeTime;
                if (b.Fade <= 0f) continue; // блип погас — удалим ниже
            }
            else
            {
                // Линейная экстраполяция между сборками 5 Гц.
                b.Contact.RelPos += b.Contact.RelVel * dt;
                b.Contact.Distance = MathF.Sqrt(b.Contact.RelPos.LengthSquared);
                LiveCount++;
            }

            b.ScreenX = b.Contact.RelPos.X * rx + b.Contact.RelPos.Z * rz;
            b.ScreenY = b.Contact.RelPos.X * fx + b.Contact.RelPos.Z * fz;
            b.HeightOffset = b.Contact.RelPos.Y;
            _view.Add(b);
        }

        // Чистим погасшие блипы.
        if (_blips.Count > 0)
        {
            _removeBuf.Clear();
            foreach (KeyValuePair<int, RadarBlip> kv in _blips)
                if (kv.Value.Stale && kv.Value.Fade <= 0f)
                    _removeBuf.Add(kv.Key);
            for (int i = 0; i < _removeBuf.Count; i++)
                _blips.Remove(_removeBuf[i]);
        }

        // Дальние первыми: панель рисует по порядку, ближние — поверх.
        _view.Sort((a, b) => b.Contact.Distance.CompareTo(a.Contact.Distance));
    }

    private readonly List<int> _removeBuf = new(16);

    /// <summary>Блип контакта (для теста/панели), null — если контакта нет.</summary>
    public RadarBlip? FindBlip(int netId) => _blips.TryGetValue(netId, out var b) ? b : null;
}
