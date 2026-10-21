"""스플래시 프레임 PNG → GIF 조립 (80ms, 무한 반복, 64색 — 먹색·회색뿐이라 충분하다).

사용:
  NekoManga.exe --gen-splash <프레임폴더>
  python make-splash-gif.py <프레임폴더> <repo>/assets
→ assets/splash.gif (업데이트 적용, UpdateService가 WinForms로 띄움), assets/splash-setup.gif (Setup.exe, vpk --splashImage)
"""
import sys
from pathlib import Path
from PIL import Image

src = Path(sys.argv[1]); out = Path(sys.argv[2])
for sub, name in (("update", "splash.gif"), ("setup", "splash-setup.gif")):
    frames = [Image.open(p).convert("RGB").quantize(colors=64, method=Image.Quantize.MEDIANCUT)
              for p in sorted((src / sub).glob("frame*.png"))]
    frames[0].save(out / name, save_all=True, append_images=frames[1:], duration=80, loop=0, optimize=False)
    print(name, len(frames), "frames", (out / name).stat().st_size, "bytes")
