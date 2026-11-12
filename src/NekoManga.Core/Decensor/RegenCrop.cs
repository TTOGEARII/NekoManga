using OpenCvSharp;

namespace NekoManga.Core.Decensor;

/// <summary>재생성 v3의 자를 창 — 영역마다 그 주변을 정사각(기본 1024)으로 잘라 SDXL에 보낸다. Window는 그림 좌표의 창, Send는 보내는 크기(8의 배수), Scale = Send/Window.</summary>
public readonly record struct RegenWindow(Rect Window, int SendW, int SendH, double Scale);

/// <summary>재생성 v3(2026-11-12): 쪽 전체를 한 번에 보내면 부위가 화면의 작은 조각이라 세부가 약하고 VRAM을 많이 쓴다 — SDXL은 1024 근처에서 가장 잘 그린다.</summary>
public static class RegenCrop
{
    /// <summary>영역 상자보다 넉넉한 창 — 한 변 = max(cropSize, 긴 변×1.6)을 영역 중심에 두고 그림 안으로 밀어 넣는다(그림보다 크면 그림 전체).
    /// 창이 cropSize보다 크면 그 비율로 줄여 보내고(호출자가 되돌린다), 보내는 크기는 8의 배수로 내린다(SDXL 잠재 격자).</summary>
    public static RegenWindow WindowFor(Rect bounds, Size img, int cropSize)
    {
        int side = Math.Max(cropSize, (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height) * 1.6));
        int w = Math.Min(side, img.Width), h = Math.Min(side, img.Height);
        int cx = bounds.X + bounds.Width / 2, cy = bounds.Y + bounds.Height / 2;
        int x = Math.Clamp(cx - w / 2, 0, img.Width - w), y = Math.Clamp(cy - h / 2, 0, img.Height - h);
        var window = new Rect(x, y, w, h);
        double scale = Math.Min(1.0, (double)cropSize / Math.Max(w, h));
        int sendW = Math.Max(64, (int)(w * scale) / 8 * 8), sendH = Math.Max(64, (int)(h * scale) / 8 * 8);
        return new RegenWindow(window, sendW, sendH, (double)sendW / w);
    }

    /// <summary>창을 잘라 보내는 크기로 맞춘 사본 셋(그림은 Area, 마스크는 Nearest — 값이 0/255만 남게). 호출자가 해제.</summary>
    public static (Mat Img, Mat Region, Mat Erase) CropAndScale(Mat img, Mat region, Mat erase, RegenWindow w)
    {
        var send = new Size(w.SendW, w.SendH);
        Mat Cut(Mat src, InterpolationFlags f)
        {
            using var roi = new Mat(src, w.Window);
            if (roi.Size() == send) return roi.Clone();
            var dst = new Mat();
            Cv2.Resize(roi, dst, send, 0, 0, f);
            return dst;
        }
        return (Cut(img, InterpolationFlags.Area), Cut(region, InterpolationFlags.Nearest), Cut(erase, InterpolationFlags.Nearest));
    }
}
