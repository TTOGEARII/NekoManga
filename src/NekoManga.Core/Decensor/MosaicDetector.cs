using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NekoManga.Core.Decensor;

/// <summary>모자이크 블록 패턴을 픽셀 분석으로 감지해 마스크를 만든다.
/// 모자이크 = 일정 크기 블록 안에서 픽셀값 표준편차가 낮은 패턴 (기존 remove_mosaic.py 휴리스틱 이식).</summary>
public static class MosaicDetector
{
    /// <summary>감지된 영역을 알파 0으로 뚫은 RGBA PNG 바이트를 돌려준다 (ComfyUI LoadImage 마스크 규약).
    /// 감지된 블록이 하나도 없으면 null (검열 없음 → 단계 건너뜀).</summary>
    public static byte[]? BuildMaskedPng(string imagePath, int blockSize, int threshold)
    {
        using var src = new Bitmap(imagePath);
        using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var px = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, px, 0, px.Length);

            bool any = false;
            for (int by = 0; by + blockSize <= bmp.Height; by += blockSize)
            for (int bx = 0; bx + blockSize <= bmp.Width; bx += blockSize)
            {
                // 블록 내 밝기 표준편차
                double sum = 0, sumSq = 0;
                int n = blockSize * blockSize;
                for (int y = by; y < by + blockSize; y++)
                for (int x = bx; x < bx + blockSize; x++)
                {
                    int i = y * stride + x * 4;
                    double lum = 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2]; // BGR
                    sum += lum; sumSq += lum * lum;
                }
                double mean = sum / n;
                double std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
                // 순백/순흑 평탄 블록(배경·말풍선)은 제외하고, 중간 톤의 평탄 블록만 모자이크로 본다.
                if (std < threshold && mean is > 25 and < 230)
                {
                    any = true;
                    for (int y = by; y < by + blockSize; y++)
                    for (int x = bx; x < bx + blockSize; x++)
                        px[y * stride + x * 4 + 3] = 0; // 알파 0 = 마스크
                }
            }
            if (!any) return null;
            Marshal.Copy(px, 0, data.Scan0, px.Length);
        }
        finally { bmp.UnlockBits(data); }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
