#!/usr/bin/env python3
"""Быстрая проверка готовой панорамы (без Godot): шов, полюса, распределение
яркости. Читает PNG и печатает числа, по которым видно, что генератор не выдал
заглушку/кашу и что панораму можно зашивать в скайбокс.

Запуск: python tools/check_starmap.py [assets/sky/starmap_equirect.png]
"""
import re
import sys

import numpy as np
from PIL import Image

path = sys.argv[1] if len(sys.argv) > 1 else "assets/sky/starmap_equirect.png"
img = np.asarray(Image.open(path), np.float32) / 255.0
h, w = img.shape[:2]
# sRGB -> линейные значения: именно так их трактует скайбокс Godot (PNG
# считается HDR-картой «as-is», без обратного преобразования).
lin = np.where(img <= 0.04045, img / 12.92, ((img + 0.055) / 1.055) ** 2.4)
lum = lin.mean(axis=2)
print(f"{path}: {w}x{h}")
print(f"  mean={lum.mean():.4f}  p50={np.median(lum):.5f}  p99={np.percentile(lum,99):.4f}  max={lum.max():.4f}")

# 1) Горизонтальный шов: панорама замыкается, значит крайние столбцы обязаны
#    отличаться не сильнее, чем соседние внутри карты.
d_seam = np.abs(lum[:, -1] - lum[:, 0]).mean()
d_inner = np.abs(np.diff(lum, axis=1)).mean()
print(f"  шов: |Δ| средняя = {d_seam:.5f}, внутри карты = {d_inner:.5f}, "
      f"отношение = {d_seam / d_inner:.2f}  ({'OK' if d_seam < 3 * d_inner else 'ПЛОХО'})")

# 2) Полюса: верхние/нижние 2% строк не должны быть светлее середины
#    (иначе в зените/надире вырастает светлое пятно).
band = max(1, int(h * 0.02))
print(f"  полюса: top2%={lum[:band].mean():.5f}  bottom2%={lum[-band:].mean():.5f}  "
      f"середина={lum[h//4:3*h//4].mean():.5f}")

# 3) Пояс: brighter-пояс должен читаться как максимум по наклону.
# Пояс ищем в той же системе координат, что и генератор: equirect-направление
# (sin zen*cos az, sin zen*sin az, cos zen), нормаль плоскости пояса
# (sin TILT, 0, cos TILT) — TILT берём из gen_starmap, чтобы конвенции не
# разъезжались.
zen = (np.arange(h) + 0.5) / h * np.pi
az = (np.arange(w) + 0.5) / w * 2 * np.pi - np.pi
zz, aa = np.meshgrid(zen, az, indexing="ij")
tilt = float(re.search(r"TILT_DEG = ([\d.]+)", open("tools/gen_starmap.py", encoding="utf-8").read()).group(1))
tilt = np.radians(tilt)
ndot = (np.sin(zz) * np.cos(aa) * np.sin(tilt) + np.cos(zz) * np.cos(tilt))
dist = np.abs(np.arcsin(np.clip(ndot, -1, 1)))
inband = dist < np.radians(12.0)
print(f"  пояс ±12°: доля площади={inband.mean():.3f}  "
      f"средняя яркость внутри={lum[inband].mean():.5f}  вне={lum[~inband].mean():.5f}  "
      f"контраст={lum[inband].mean()/max(lum[~inband].mean(),1e-6):.2f}")

# 4) Счётчик точек: сколько пикселей заметно ярче фона (реальное число
#    звёзд на карте может отличаться от COUNT_STARS из-за тусклых и перекрытий).
bg = np.median(lum)
for k in (4, 10, 40):
    print(f"  пикселей ярче {k}x медианы фона: {(lum > bg * k).sum()}")
