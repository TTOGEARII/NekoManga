using OpenCvSharp;

namespace NekoManga.Core;

/// <summary>유니코드 경로 안전 이미지 I/O.
///
/// <see cref="Cv2.ImRead(string, ImreadModes)"/>·<see cref="Cv2.ImWrite(string, Mat, int[])"/>는
/// 파일 경로를 ANSI(현재 코드페이지)로 마샬링한다. 만화 경로에는 일본어·한글이 흔해서
/// 코드페이지로 못 옮기는 문자가 있으면 "cannot marshal: Encountered unmappable character"로 터진다.
///
/// 그래서 파일 자체는 .NET(유니코드 경로 OK)으로 읽고/쓰고, OpenCV는 메모리 버퍼만
/// 디코드/인코드한다 — webp·avif 등 OpenCV 코덱은 그대로 유지된다
/// (System.Drawing은 이 포맷들을 못 읽으므로 OpenCV 디코드는 반드시 거쳐야 한다).</summary>
public static class ImgIo
{
    /// <summary>경로의 이미지를 읽는다. 실패 시 빈 Mat을 돌려준다(Cv2.ImRead와 동일 계약).</summary>
    public static Mat Read(string path, ImreadModes mode = ImreadModes.Color)
    {
        var bytes = File.ReadAllBytes(path);
        return Cv2.ImDecode(bytes, mode);
    }

    /// <summary>확장자에 맞춰 인코딩해 경로에 쓴다. 인코딩 실패 시 false(Cv2.ImWrite와 동일 계약).
    /// 부모 폴더가 없으면 만들어 준다 (사이드카 폴더 등 — 없어서 실제로 터진 적 있음).</summary>
    public static bool Write(string path, Mat img)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        if (!Cv2.ImEncode(ext, img, out var buf)) return false;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, buf);
        return true;
    }
}
