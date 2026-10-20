# NekoManga 자가 학습 (2026-10-20) — 검토 창에서 사용자가 고친 마스크(%AppData%\NekoManga\training\corrections)로 검열 감지 모델을 짧게 미세조정하고,
# 보류(홀드아웃) 페이지에서 전/후를 재서 result.json으로 돌려준다. 앱(SelfTrainer.cs)이 ComfyUI의 python_embeded(torch+ultralytics 내장)로 이 스크립트를 띄운다.
#   python selftrain.py --corrections <dir> --work <dir> --init <censor_seg.pt> --out-onnx <path> [--epochs 5] [--lr0 0.0002] [--freeze 10] [--min-pages 20] [--holdout 0.2]
# 설계 메모: 사용자 재료만으로 c11을 이어 학습하면 과적합·망각 위험이 있어 lr 낮게·에폭 짧게·백본 동결이 기본이고, 적용 여부는 사람이 전/후 수치를 보고 정한다.
# 클래스는 마스크에 없으므로 성분의 밝기로 추정한다(평균 <70 검정 / >200 흰 / 그 외 모자이크). 출력 줄 "PROGRESS <단계> <설명>"은 앱 상태 표시에 쓴다.
import os, sys, json, glob, random, hashlib, argparse, shutil, time
import numpy as np, cv2

def log(stage, msg): print(f"PROGRESS {stage} {msg}", flush=True)
def rd(p, flag=cv2.IMREAD_COLOR): return cv2.imdecode(np.fromfile(p, dtype=np.uint8), flag)
def wr(p, img, ext=".png"): cv2.imencode(ext, img)[1].tofile(p)

CROP = 1024

def load_entries(cdir):
    ents = []
    for idx in sorted(glob.glob(os.path.join(cdir, "*", "index.jsonl"))):
        d = os.path.dirname(idx)
        for line in open(idx, encoding="utf-8"):
            line = line.strip()
            if not line: continue
            try: e = json.loads(line)
            except json.JSONDecodeError: continue
            page = os.path.join(d, e["stem"] + ".page.png"); mask = os.path.join(d, e["stem"] + ".mask.png")
            if os.path.exists(page) and os.path.exists(mask):
                e["page_path"], e["mask_path"] = page, mask
                e["auto_path"] = os.path.join(d, e["stem"] + ".auto.png")
                ents.append(e)
    # 같은 원본을 여러 번 검토했으면 마지막 것만
    last = {}
    for e in ents: last[e.get("source", e["stem"])] = e
    return list(last.values())

def cls_of(gray, comp_mask):
    m = float(gray[comp_mask].mean()) if comp_mask.any() else 128.0
    return 1 if m < 70 else 0 if m > 200 else 2

def polys(mask_cls, x0, y0, cls):
    sub = mask_cls[y0:y0 + CROP, x0:x0 + CROP]
    cs, _ = cv2.findContours(sub.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    out = []
    for c in cs:
        if cv2.contourArea(c) < 150: continue
        ap = cv2.approxPolyDP(c, 1.5, True).reshape(-1, 2)
        if len(ap) >= 3: out.append((cls, ap))
    return out

def pad_to_crop(img, mask):
    H, W = mask.shape
    if H >= CROP and W >= CROP: return img, mask
    ph, pw = max(0, CROP - H), max(0, CROP - W)
    img = cv2.copyMakeBorder(img, 0, ph, 0, pw, cv2.BORDER_CONSTANT, value=(255, 255, 255))
    mask = cv2.copyMakeBorder(mask, 0, ph, 0, pw, cv2.BORDER_CONSTANT, value=0)
    return img, mask

def build_dataset(train_ents, out, npos=4, nneg=2, nhard=3):
    for sub in ("images/train", "labels/train", "images/val", "labels/val"):
        os.makedirs(os.path.join(out, sub), exist_ok=True)
    idx = 0; n_inst = 0
    rng = random.Random(7)
    for e in train_ents:
        img = rd(e["page_path"]); mask = rd(e["mask_path"], cv2.IMREAD_GRAYSCALE)
        if img is None or mask is None: continue
        mask = (mask > 127).astype(np.uint8)
        img, mask = pad_to_crop(img, mask)
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
        img = cv2.cvtColor(gray, cv2.COLOR_GRAY2BGR)
        H, W = mask.shape
        # 성분별 클래스 마스크
        nc, lab, st, _ = cv2.connectedComponentsWithStats(mask, 8)
        cls_masks = {0: np.zeros_like(mask), 1: np.zeros_like(mask), 2: np.zeros_like(mask)}
        boxes = []
        for i in range(1, nc):
            if st[i][4] < 100: continue
            comp = lab == i
            c = cls_of(gray, comp)
            cls_masks[c][comp] = 1
            boxes.append((int(st[i][0]), int(st[i][1]), int(st[i][2]), int(st[i][3])))
        windows = []
        def around(x, y, w, h, n):
            for _ in range(n):
                xl, xh = max(0, x + w - CROP), min(x, W - CROP); yl, yh = max(0, y + h - CROP), min(y, H - CROP)
                if xl > xh: xl = xh = min(max(0, x + w // 2 - CROP // 2), W - CROP)
                if yl > yh: yl = yh = min(max(0, y + h // 2 - CROP // 2), H - CROP)
                windows.append((rng.randint(xl, xh), rng.randint(yl, yh)))
        for b in boxes: around(*b, npos)
        for _ in range(nneg): windows.append((rng.randint(0, W - CROP), rng.randint(0, H - CROP)))
        # 하드네거티브: 자동 감지는 켰는데 사용자가 지운 자리
        if os.path.exists(e["auto_path"]):
            auto = rd(e["auto_path"], cv2.IMREAD_GRAYSCALE)
            if auto is not None and auto.shape == (H - (0), W - (0)) or (auto is not None and auto.shape[0] <= H and auto.shape[1] <= W):
                a2 = np.zeros_like(mask); a2[:auto.shape[0], :auto.shape[1]] = (auto > 127).astype(np.uint8)
                far = cv2.dilate(mask, np.ones((3, 3), np.uint8), iterations=12)
                nh, _, sth, _ = cv2.connectedComponentsWithStats(((a2 > 0) & (far == 0)).astype(np.uint8), 8)
                for i in range(1, nh):
                    if sth[i][4] >= 300: around(int(sth[i][0]), int(sth[i][1]), int(sth[i][2]), int(sth[i][3]), nhard)
        windows = list(dict.fromkeys(windows))
        for k, (x0, y0) in enumerate(windows):
            split = "val" if (idx % 10 == 0) else "train"
            crop = img[y0:y0 + CROP, x0:x0 + CROP].copy()
            if rng.random() < 0.5:
                crop = cv2.imdecode(cv2.imencode(".jpg", crop, [cv2.IMWRITE_JPEG_QUALITY, rng.randint(78, 95)])[1], 1)
            name = f"{idx:06d}"
            wr(os.path.join(out, "images", split, name + ".jpg"), crop, ".jpg")
            with open(os.path.join(out, "labels", split, name + ".txt"), "w") as f:
                for c in (0, 1, 2):
                    for (cc, poly) in polys(cls_masks[c], x0, y0, c):
                        f.write(f"{cc} " + " ".join(f"{px/CROP:.5f} {py/CROP:.5f}" for px, py in poly) + "\n"); n_inst += 1
            idx += 1
    with open(os.path.join(out, "data.yaml"), "w", encoding="utf-8") as f:
        f.write(f"path: {out}\ntrain: images/train\nval: images/val\nnames:\n  0: white\n  1: black\n  2: mosaic\n")
    return idx, n_inst

def evaluate(model_path, ents, imgsz=1024, conf=0.40):
    from ultralytics import YOLO
    m = YOLO(model_path)
    hits = total = 0; fp_px = 0; clean_fired = 0; clean_n = 0
    for e in ents:
        img = rd(e["page_path"]); mask = rd(e["mask_path"], cv2.IMREAD_GRAYSCALE)
        if img is None or mask is None: continue
        mask = mask > 127
        H, W = mask.shape
        pred = np.zeros((H, W), bool)
        try:
            r = m.predict(img, imgsz=imgsz, conf=conf, iou=0.6, retina_masks=True, verbose=False)[0]
            if r.masks is not None:
                for mm in r.masks.data.cpu().numpy():
                    mm = mm > 0.5
                    if mm.shape != (H, W): mm = cv2.resize(mm.astype(np.uint8), (W, H), interpolation=cv2.INTER_NEAREST) > 0
                    pred |= mm
        except Exception as ex:
            log("eval", f"predict failed {os.path.basename(e['page_path'])}: {ex}")
        if not mask.any():
            clean_n += 1; clean_fired += int(pred.any()); continue
        nc, lab, st, _ = cv2.connectedComponentsWithStats(mask.astype(np.uint8), 8)
        for i in range(1, nc):
            if st[i][4] < 100: continue
            comp = lab == i; total += 1
            if (pred & comp).sum() >= 0.3 * comp.sum(): hits += 1
        far = cv2.dilate(mask.astype(np.uint8), np.ones((3, 3), np.uint8), iterations=12) > 0
        fp_px += int((pred & ~far).sum())
    return dict(hits=hits, total=total, fp_px=fp_px, clean_pages=clean_n, clean_fired=clean_fired)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--corrections", required=True); ap.add_argument("--work", required=True)
    ap.add_argument("--init", required=True); ap.add_argument("--out-onnx", required=True)
    ap.add_argument("--epochs", type=int, default=5); ap.add_argument("--lr0", type=float, default=0.0002)
    ap.add_argument("--freeze", type=int, default=10); ap.add_argument("--min-pages", type=int, default=20)
    ap.add_argument("--holdout", type=float, default=0.2); ap.add_argument("--batch", type=int, default=4)
    a = ap.parse_args()
    t0 = time.time()
    ents = load_entries(a.corrections)
    log("scan", f"재료 {len(ents)}쪽")
    if len(ents) < a.min_pages:
        json.dump(dict(ok=False, error=f"재료가 {len(ents)}쪽이라 아직 부족해요 (최소 {a.min_pages}쪽)"), open(os.path.join(a.work, "result.json"), "w", encoding="utf-8"), ensure_ascii=False)
        return 2
    # 보류 페이지: 원본 경로 해시로 고정 분할 (같은 페이지가 늘 같은 쪽에)
    hold = [e for e in ents if int(hashlib.md5(e.get("source", e["stem"]).encode()).hexdigest(), 16) % 100 < a.holdout * 100]
    if len(hold) == 0 and len(ents) >= 5: hold = ents[:max(1, len(ents) // 5)]
    hold_keys = {id(e) for e in hold}
    train = [e for e in ents if id(e) not in hold_keys]
    log("split", f"학습 {len(train)}쪽 · 보류 {len(hold)}쪽")
    ds = os.path.join(a.work, "dataset")
    if os.path.exists(ds): shutil.rmtree(ds)
    n_crops, n_inst = build_dataset(train, ds)
    log("data", f"크롭 {n_crops}장 · 라벨 {n_inst}개")
    if n_crops < 10:
        json.dump(dict(ok=False, error="학습 크롭이 너무 적어요 (마스크가 있는 페이지가 더 필요)"), open(os.path.join(a.work, "result.json"), "w", encoding="utf-8"), ensure_ascii=False)
        return 2
    from ultralytics import YOLO
    log("train", f"미세조정 시작 ({a.epochs}에폭, lr {a.lr0}, freeze {a.freeze})")
    model = YOLO(a.init)
    model.train(data=os.path.join(ds, "data.yaml"), imgsz=CROP, epochs=a.epochs, batch=a.batch, device=0, workers=2,
                project=a.work, name="run", exist_ok=True, patience=a.epochs, lr0=a.lr0, optimizer="AdamW",
                freeze=(a.freeze if a.freeze > 0 else None), hsv_h=0.0, hsv_s=0.2, hsv_v=0.25, degrees=5, translate=0.06, scale=0.35,
                fliplr=0.5, mosaic=0.3, close_mosaic=2, copy_paste=0.0, erasing=0.0, plots=False, verbose=False)
    best = os.path.join(a.work, "run", "weights", "best.pt")
    if not os.path.exists(best):
        json.dump(dict(ok=False, error="학습 결과 가중치가 없어요 (로그 확인)"), open(os.path.join(a.work, "result.json"), "w", encoding="utf-8"), ensure_ascii=False)
        return 1
    log("export", "ONNX로 내보내는 중")
    onnx = YOLO(best).export(format="onnx", imgsz=CROP, opset=12, simplify=True, nms=False, dynamic=False)
    shutil.copy2(onnx, a.out_onnx)
    shutil.copy2(best, os.path.splitext(a.out_onnx)[0] + ".pt")
    log("eval", f"보류 {len(hold)}쪽 전/후 비교")
    before = evaluate(a.init, hold) if hold else None
    after = evaluate(best, hold) if hold else None
    res = dict(ok=True, pages=len(ents), train_pages=len(train), holdout_pages=len(hold), crops=n_crops, instances=n_inst,
               before=before, after=after, out_onnx=a.out_onnx, out_pt=os.path.splitext(a.out_onnx)[0] + ".pt",
               minutes=round((time.time() - t0) / 60, 1))
    json.dump(res, open(os.path.join(a.work, "result.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    log("done", f"완료 {res['minutes']}분")
    return 0

if __name__ == "__main__":
    sys.exit(main())
