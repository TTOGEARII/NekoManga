using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NekoManga.Core.Engine;

/// <summary>Hayai OCR의 글자표(tokenizer.json) — **풀기만** 한다(모델이 낸 토큰 번호 → 글). 바이트 단위 BPE(GPT-2식 ByteLevel)라
/// 토큰 글자 하나가 바이트 하나다(0x21~0x7E 등은 그 글자 그대로, 빈칸·제어 바이트·0x7F~0xA0은 U+0100부터 차례로 — 빈칸 0x20 = "Ġ").
/// 한 글자(UTF-8 세 바이트)가 토큰 여럿에 갈려 나올 수 있어, 토큰의 바이트를 다 이은 뒤에 한 번 UTF-8로 푼다.</summary>
internal sealed class HayaiTokenizer
{
    /// <summary>토큰 번호 → 바이트. 특수 토큰(시작·끝·채움·모름)은 null — 글이 아니다.</summary>
    private readonly byte[]?[] _bytes;

    private HayaiTokenizer(byte[]?[] bytes) => _bytes = bytes;

    private static readonly Dictionary<char, byte> ByteOf = BuildByteMap();

    private static Dictionary<char, byte> BuildByteMap()
    {
        static bool Own(int b) => b is >= 0x21 and <= 0x7E or >= 0xA1 and <= 0xAC or >= 0xAE and <= 0xFF;
        var map = new Dictionary<char, byte>(256);
        int n = 0;
        for (int b = 0; b < 256; b++) map[Own(b) ? (char)b : (char)(256 + n++)] = (byte)b;
        return map;
    }

    /// <summary>tokenizer.json을 읽는다. 바이트 단위 BPE가 아니면(다른 모델의 파일·내려받다 깨진 파일) <see cref="InvalidDataException"/> — 조용히 빈 글을 내지 않는다.</summary>
    public static HayaiTokenizer Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var model = doc.RootElement.GetProperty("model");
            if (model.GetProperty("type").GetString() != "BPE" || doc.RootElement.GetProperty("decoder").GetProperty("type").GetString() != "ByteLevel")
                throw new InvalidDataException(Path.GetFileName(path));
            var vocab = model.GetProperty("vocab");
            int size = 0;
            foreach (var entry in vocab.EnumerateObject()) size = Math.Max(size, entry.Value.GetInt32() + 1);
            if (size == 0) throw new InvalidDataException(Path.GetFileName(path));
            var bytes = new byte[]?[size];
            var one = new List<byte>(32);
            foreach (var entry in vocab.EnumerateObject())
            {
                one.Clear();
                foreach (char c in entry.Name)
                {
                    if (ByteOf.TryGetValue(c, out byte b)) one.Add(b);
                    else one.AddRange(Encoding.UTF8.GetBytes(c.ToString()));   // 표에 없는 글자(바이트 표기가 아닌 토큰) — 글자 그대로
                }
                bytes[entry.Value.GetInt32()] = one.ToArray();
            }
            if (doc.RootElement.TryGetProperty("added_tokens", out var added))
                foreach (var token in added.EnumerateArray())
                {
                    int id = token.GetProperty("id").GetInt32();
                    if (id >= 0 && id < size && token.TryGetProperty("special", out var special) && special.GetBoolean()) bytes[id] = null;
                }
            return new HayaiTokenizer(bytes);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(Path.GetFileName(path), ex);
        }
    }

    /// <summary>토큰 번호들을 글로 푼다. 특수 토큰과 어휘 밖의 번호(모델의 시작·끝·채움 번호 16000~)는 건너뛴다. 깨진 바이트는 U+FFFD가 된다(원본 tokenizers와 같다).</summary>
    public string Decode(IEnumerable<int> ids)
    {
        var all = new List<byte>(64);
        foreach (int id in ids)
            if (id >= 0 && id < _bytes.Length && _bytes[id] is { } b) all.AddRange(b);
        return Encoding.UTF8.GetString(all.ToArray());
    }
}

/// <summary>NumPy .npy 파일에서 float32 2차원 표를 읽는다 — Hayai OCR의 위치 표(pos_embedding.npy, 256 × 768)용. 행 우선 little-endian float32만 받는다.</summary>
internal static class NpyFile
{
    public static (float[] Data, int Rows, int Cols) ReadFloat2D(string path)
    {
        var raw = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        if (raw.Length < 12 || raw[0] != 0x93 || Encoding.ASCII.GetString(raw, 1, 5) != "NUMPY") throw new InvalidDataException(name);
        // 판 1은 머리말 길이가 2바이트, 판 2·3은 4바이트
        int lenBytes = raw[6] == 1 ? 2 : 4;
        int headerLen = lenBytes == 2 ? BitConverter.ToUInt16(raw, 8) : checked((int)BitConverter.ToUInt32(raw, 8));
        int dataStart = 8 + lenBytes + headerLen;
        if (dataStart > raw.Length) throw new InvalidDataException(name);
        string header = Encoding.ASCII.GetString(raw, 8 + lenBytes, headerLen);
        var shape = Regex.Match(header, @"'shape'\s*:\s*\(\s*(\d+)\s*,\s*(\d+)\s*,?\s*\)");
        if (!Regex.IsMatch(header, @"'descr'\s*:\s*'(<|\|)f4'") || !Regex.IsMatch(header, @"'fortran_order'\s*:\s*False") || !shape.Success)
            throw new InvalidDataException(name);
        int rows = int.Parse(shape.Groups[1].Value), cols = int.Parse(shape.Groups[2].Value);
        long count = (long)rows * cols;
        if (count <= 0 || raw.Length - dataStart != count * 4) throw new InvalidDataException(name);
        var data = new float[count];
        Buffer.BlockCopy(raw, dataStart, data, 0, (int)(count * 4));
        return (data, rows, cols);
    }
}
