#!/usr/bin/env python3
"""Процедурная генерация звёздного неба (equirectangular) для скайбокса Space Corporation.

Poly Haven в момент написания не предлагал ни одного «чистого космоса»: все
ночные HDRI там земные — с горизонтом, землёй, луной и деревьями, а для
equirect-скайбокса нижняя полусфера обязана быть тем же космосом, иначе под
кораблём вырастает силуэт холмов. Поэтому небо генерируется здесь и остаётся
в репозитории как артефакт (лицензия — наша, ограничений нет).

Вход: не нужен. Выход: PNG 4096x2048, где u = азимут [-180,180], v = зенит
[0,180] — ровно то, что ждёт PanoramaSkyMaterial в Godot.

Запуск:  python tools/gen_starmap.py [--out assets/sky/starmap_equirect.png]
                                        [--w 4096] [--h 2048] [--seed 7]
Требует: numpy, Pillow.
"""

from __future__ import annotations

import argparse
import math
import os

import numpy as np
from PIL import Image

# --- Настройки вида -------------------------------------------------------
# Всё ниже — про «правдоподобно, но читаемо»: галактическая полоса должна
# давать направленность в кадре (иначе в однородной черноте нет ощущения
# движения), звёзды — остаться точками, а не кашей.

COUNT_STARS = 9000          # звёзд на всю сферу (по всей поверхности)
STAR_MAG_MIN = 0.0            # нижняя граница яркости (0 = порог видности)
STAR_MAG_MAX = 3.4            # верхняя: самые яркие точки
STAR_BRIGHT_BASE = 0.045    # линейный пик звезды нижней величины (в ~5 раз тусклее прежнего)
STAR_BRIGHT_MAX = 0.20      # потолок яркости самой крупной звезды (был 1.0 — выгоравшие кляксы)
STAR_SIGMA_MIN = 0.32       # гауссово «ядро» в пикселях карты: <1 px = точка, как в природе
STAR_SIGMA_MAX = 0.50       # максимум для самых ярких (у старых доходило до 3.0 — «астероиды»)
BAND_SIGMA_DEG = 11.0         # угловая ширина Млечного Пути
BAND_DENSITY = 1.9            # во сколько раз звёзд в поясе больше, чем вне
GLOW_PEAK = 0.018             # пиковая яркость полоски Млечного Пути (было 0.03 — полоса была слишком светлой)
NEBULA_PEAK = 0.012           # красный оттенок туманностей вдоль пояса
TILT_DEG = 58.0               # наклон пояса к «экватору» панорамы


def unit_sphere(w: int, h: int) -> np.ndarray:
    """Единичные направления пикселей equirect-панорамы, форма (h, w, 3)."""
    u = (np.arange(w, dtype=np.float32) + 0.5) / w          # 0..1 по азимуту
    v = (np.arange(h, dtype=np.float32) + 0.5) / h          # 0..1 по зениту
    az = (u - 0.5) * 2.0 * math.pi
    zen = v * math.pi                                       # 0 сверху, pi снизу
    z = np.cos(zen)[:, None] * np.ones((1, w), np.float32)
    r = np.sin(zen)[:, None] * np.ones((1, w), np.float32)
    x = r * np.cos(az)[None, :]
    y = r * np.sin(az)[None, :]
    return np.stack([x, y, z], axis=-1)


def value_noise(shape: tuple[int, int], octaves: int, seed: int) -> np.ndarray:
    """FBM из value-noise с замыканием по X (шов панорамы) и clamp по Y."""
    rng = np.random.default_rng(seed)
    out = np.zeros(shape, np.float32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        gx, gy = 8 * 2 ** o, 4 * 2 ** o
        g = rng.random((gy, gx)).astype(np.float32)
        yy = np.linspace(0.0, gy - 1.0, shape[0], dtype=np.float32)
        # Период по X ровно равен ширине сетки: шаг (gx-1)/px, как было раньше,
        # оставляет на шве «щель» в одну ячейку — по ней чекер и видит разрыв.
        xx = (np.arange(shape[1], dtype=np.float32) + 0.5) / shape[1] * gx
        y0 = np.floor(yy).astype(np.int32); wy = (yy - y0)[:, None]
        xf = np.floor(xx); x0 = xf.astype(np.int32) % gx
        wx = (xx - xf)[None, :]
        y0 = np.clip(y0, 0, gy - 2); y1 = np.clip(y0 + 1, 0, gy - 1)
        x1 = (x0 + 1) % gx                                  # бесшовность по X
        a = g[np.ix_(y0, x0)]; b = g[np.ix_(y0, x1)]
        c = g[np.ix_(y1, x0)]; d = g[np.ix_(y1, x1)]
        n = (a * (1 - wx) * (1 - wy) + b * wx * (1 - wy)
             + c * (1 - wx) * wy + d * wx * wy)
        n = (n - n.mean()) / (n.std() + 1e-6)
        out += amp * np.clip(n * 0.5 + 0.5, 0.0, 1.0)
        total += amp; amp *= 0.55
    return out / total


def band_mask(dirs: np.ndarray, shape: tuple[int, int]) -> np.ndarray:
    """Пояс Млечного Пути: гаусс по угловому расстоянию до большой окружности."""
    n = np.array([math.sin(math.radians(TILT_DEG)), 0.0,
                  math.cos(math.radians(TILT_DEG))], np.float32)
    dist = np.abs(dirs @ n)                                 # sin угла к плоскости
    ang = np.arcsin(np.clip(dist, -1.0, 1.0))
    sig = math.radians(BAND_SIGMA_DEG)
    core = np.exp(-0.5 * (ang / sig) ** 2)
    # Тонкая структура: пылевые прожилки и «дыры» — без них пояс выглядит
    # наклеенной полосой.
    return core


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="assets/sky/starmap_equirect.png")
    ap.add_argument("--w", type=int, default=4096)
    ap.add_argument("--h", type=int, default=2048)
    ap.add_argument("--seed", type=int, default=7)
    args = ap.parse_args()
    w, h, seed = args.w, args.h, args.seed

    lin = np.zeros((h, w, 3), np.float32)
    dirs = unit_sphere(w, h)

    # --- 1) Млечный Путь как фон -----------------------------------------
    # Слой считается СРАЗУ в полном разрешении. Раньше его строили в 2048x1024
    # и растягивали PIL-ресайзом: у ресайза края не заворачиваются (clamp), и
    # на шве панорамы возникал реальный разрыв яркости (чекер показывал
    # отношение ~8). Аналитический band_mask + периодичный value_noise дают
    # идеально стыкующуюся по X карту без всякого ресайза.
    core = band_mask(dirs, (h, w))
    fbm = value_noise((h, w), 4, seed)
    dust = value_noise((h, w), 4, seed + 101)

    # Плотность полосы: мягкая модуляция яркости вдоль пояса. Раньше сюда
    # входил пылевой множитель (1.0 - 0.75*dust**2), который в местах с большим
    # dust проваливал плотность почти до нуля — на небе это выглядело как
    # ЧЁРНЫЕ пятна/дыры внутри Млечного Пути. Убрали его: теперь полоса даёт
    # только лёгкие вариации (0.65..1.05), без провалов в темноту.
    dens = np.clip(core * (0.65 + 0.40 * fbm), 0.0, 1.0)
    dens_full = dens

    # Цвет полосы: холоднее в середине, тёплый там, где «туманности».
    warm_full = np.clip((dust - 0.62) * 3.0, 0.0, 1.0)
    glow = GLOW_PEAK * dens_full
    lin[..., 0] += glow * (1.0 + 1.6 * warm_full)
    lin[..., 1] += glow * (0.92 + 0.35 * warm_full)
    lin[..., 2] += glow * (0.98 - 0.25 * warm_full)
    # Едва заметная красноватая дымка отдельных «туманностей».
    neb = NEBULA_PEAK * np.clip(dens_full - 0.55, 0.0, 1.0)
    lin[..., 0] += neb * 2.2
    lin[..., 1] += neb * 0.6
    lin[..., 2] += neb * 0.8
    # Фоновая чернота не абсолютно нулевая: чуть-чуть холодного «светофора»
    # полушарий, чтобы градиент от планет/света не обрывался стеной.
    lin += 0.0035 * np.array([0.75, 0.82, 1.0], np.float32)

    # --- 2) Звёзды --------------------------------------------------------
    rng = np.random.default_rng(seed + 1)
    n_dir = unit_sphere(1, 1)  # only for shape helper; generate dirs directly
    # Равномерные направления на сфере + клестеринг к поясу через важность.
    zc = rng.uniform(-1.0, 1.0, COUNT_STARS).astype(np.float32)
    az = rng.uniform(-math.pi, math.pi, COUNT_STARS).astype(np.float32)
    r = np.sqrt(np.maximum(0.0, 1.0 - zc * zc))
    sx = (r * np.cos(az)).astype(np.float32)
    sy = (r * np.sin(az)).astype(np.float32)
    sn = np.stack([sx, sy, zc], axis=-1)
    # Вес = вероятность попасть в плотную область пояса.
    wgt = 1.0 + (BAND_DENSITY - 1.0) * np.interp(
        np.abs(sn @ np.array([math.sin(math.radians(TILT_DEG)), 0.0,
                             math.cos(math.radians(TILT_DEG))], np.float32)),
        [0.0, 1.0],
        [1.0, float(core.max()) if core.max() > 0 else 1.0])
    wgt = wgt / wgt.sum()
    pick = rng.choice(COUNT_STARS, size=COUNT_STARS, p=wgt)
    sn = sn[pick]

    # Величины: много тусклых, мало ярких (геометрический хвост).
    t = rng.random(COUNT_STARS).astype(np.float32)
    mag = STAR_MAG_MIN + (STAR_MAG_MAX - STAR_MAG_MIN) * (1.0 - t) ** 2.2
    # Линейный пик яркости: от порога видности до STAR_BRIGHT_MAX. Прежняя
    # шкала (0.30 * 2.512**(0.6*mag), потолок 1.0) давала выгоревшие в белую
    # кляксы — самые яркие точки теперь в ~3 раза тусклее. величина mag
    # нормализована на STAR_MAG_MAX: иначе mag**2.2 при mag=3.4 даёт 14.8,
    # «потолок» STAR_BRIGHT_MAX не удерживает яркость (пик доходил до 10.6
    # и выгорал в белую точку у полюсов).
    bright = STAR_BRIGHT_BASE + (STAR_BRIGHT_MAX - STAR_BRIGHT_BASE) * (mag / STAR_MAG_MAX) ** 2.2

    # Цвет по «случайной» температуре: большинство бело-жёлтые, встречаются
    # голубые и оранжевые — это читается как реалистичность днём и ночью.
    ct = rng.random(COUNT_STARS).astype(np.float32)
    tint = np.ones((COUNT_STARS, 3), np.float32)
    cool = ct < 0.18
    warm = ct > 0.72
    tint[cool] *= np.array([0.72, 0.84, 1.0], np.float32)
    tint[warm] *= np.array([1.0, 0.80, 0.62], np.float32)

    # u,v в пикселях карты (непрерывные координаты).
    uu = (np.arctan2(sn[..., 1], sn[..., 0]) / (2.0 * math.pi) + 0.5) * w
    vv = (np.arccos(np.clip(sn[..., 2], -1.0, 1.0)) / math.pi) * h

    # --- Звёзды: точечные, как в природе ----------------------------------
    # На этом угловом разрешении (0.088°/px) угловой размер звезды меньше
    # пикселя карты, поэтому звезда — это один пиксель с билинейным разбиением
    # энергии между 4 соседями: координата звезды не «прыгает» по сетке, а
    # размер не зависит от яркости (так же, как в реальных звёздных каталогах
    # для этого разрешения). Прежние гауссы с sigma до 3 px давали диски
    # 6-8 px — на экране это выглядело как светящиеся «астероиды»; теперь
    # «ядро» меньше в ~10 раз (1 px против ~10 px).
    # Компенсация сходимости долгот у полюсов (иначе звёзды у полюсов
    # тускнеют): амплитуда × 1/sqrt(sin zen), с потолком — иначе в 10 px от
    # полюса звезда выгорала бы в 30-кратной компенсацией.
    near_pole_rows = np.clip(np.arange(h, dtype=np.float64), 0, h - 1)
    pole_comp = 1.0 / np.maximum(1e-3, np.sin(np.pi * (near_pole_rows + 0.5) / h)) ** 0.5
    pole_comp = np.minimum(pole_comp, 2.0)

    for i in range(COUNT_STARS):
        v = float(uu[i])
        u = float(vv[i])
        x0, y0 = int(math.floor(v)), int(math.floor(u))
        fx, fy = v - x0, u - y0
        if not (0 <= y0 < h - 1):
            continue
        b = bright[i] * pole_comp[min(h - 1, y0 + 1)]

        for dy, wy in ((0, 1.0 - fy), (1, fy)):
            for dx, wx in ((0, 1.0 - fx), (1, fx)):
                lin[y0 + dy, (x0 + dx) % w] += b * wx * wy * tint[i]

    # Дифракционный «венец» только у самых ярких точек: крошечные боковые
    # отпечатки (1 px слева/справа/сверху/снизу, амплитуда 15% звезды) —
    # читается как искра, суммарно не больше 3 px. Векторно, по всей карте.
    spark = np.nonzero(mag > 0.95)[0]
    if spark.size:
        sx = np.mod(np.round(uu[spark]).astype(np.int64), w)
        sy = np.round(vv[spark]).astype(np.int64)
        keep = (sy >= 1) & (sy <= h - 2)
        sx, sy = sx[keep], sy[keep]
        amp = (0.15 * bright[spark][keep])[:, None] * pole_comp[sy][:, None] * tint[spark[keep]]
        lin[sy, np.mod(sx - 1, w)] += amp
        lin[sy, np.mod(sx + 1, w)] += amp
        lin[np.clip(sy - 1, 0, h - 1), sx] += amp
        lin[np.clip(sy + 1, 0, h - 1), sx] += amp


    # --- 3) Тоналность и запись -------------------------------------------
    maxv = float(lin.max())
    print(f"линейный максимум до тоналлности: {maxv:.3f}")
    # Линейный -> sRGB. Звёзды при этом остаются ярче неба в 3-5 раз.
    img = np.clip(lin, 0.0, 1.0)
    srgb = np.where(img <= 0.0031308, img * 12.92,
                    1.055 * img ** (1.0 / 2.4) - 0.055)
    out = args.out
    os.makedirs(os.path.dirname(out), exist_ok=True)
    Image.fromarray((np.clip(srgb, 0, 1) * 255.0 + 0.5).astype(np.uint8), "RGB").save(out, optimize=True)
    print(f"записано: {out}  ({w}x{h}, {os.path.getsize(out) / 1e6:.2f} МБ)")


if __name__ == "__main__":
    main()
