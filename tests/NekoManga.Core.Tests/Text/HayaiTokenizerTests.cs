using System.Text;
using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>Hayai OCR의 글자표 읽기(<see cref="HayaiTokenizer"/> — 바이트 단위 BPE의 풀기만)와 위치 표 파일(<see cref="NpyFile"/>).
/// 토큰 글자는 실제 tokenizer.json에서 그대로 옮겨 적었다(바이트 하나를 글자 하나로 적는 GPT-2식 표기: 0x20 = Ġ, E3 81 8A = "ãģĬ" = お).
/// 기대값은 원본 tokenizers 라이브러리의 decode 결과다(2026-11-27).</summary>
public class HayaiTokenizerTests
{
    /// <summary>실제 파일과 같은 꼴의 작은 tokenizer.json — 어휘는 시험에 쓰는 것만.</summary>
    private static HayaiTokenizer Load(TempDir t)
    {
        string[] vocab =
        {
            "[PAD]", "[UNK]", "[BOS]", "[EOS]",                          // 0~3 특수
            "A",                                                         // 4
            "ã", "ģ", "Ĭ",                                // 5~7  E3 · 81 · 8A — お의 세 바이트가 토큰 셋으로 갈린 것
            "ãģĬãģ¯ãĤĪãģĨ",   // 8  おはよう
            "Ġp", "res", "ent", "ed", "Ġby",                   // 9~13  " presented by"
            "ìĹ°", "ìĬµ", "íĸĪëĭ¤", "Ġ", "ä¸Ģç·Ĵãģ«",   // 14~18  연 · 습 · 했다 · 빈칸 · 一緒に
            "ï¼ģï¼Ł", "âĻ¡",   // 19~20  ！？ · ♡
        };
        var sb = new StringBuilder("{\"version\":\"1.0\",\"added_tokens\":[");
        sb.Append("{\"id\":0,\"content\":\"[PAD]\",\"special\":true},{\"id\":1,\"content\":\"[UNK]\",\"special\":true},");
        sb.Append("{\"id\":2,\"content\":\"[BOS]\",\"special\":true},{\"id\":3,\"content\":\"[EOS]\",\"special\":true},");
        sb.Append("{\"id\":21,\"content\":\"<pad>\",\"special\":true},{\"id\":22,\"content\":\"<bos>\",\"special\":true},{\"id\":23,\"content\":\"<eos>\",\"special\":true}],");
        sb.Append("\"decoder\":{\"type\":\"ByteLevel\"},\"model\":{\"type\":\"BPE\",\"vocab\":{");
        for (int i = 0; i < vocab.Length; i++)
            sb.Append(i == 0 ? "" : ",").Append(System.Text.Json.JsonSerializer.Serialize(vocab[i])).Append(':').Append(i);
        sb.Append("},\"merges\":[]}}");
        var path = t.At("tokenizer.json");
        File.WriteAllText(path, sb.ToString());
        return HayaiTokenizer.Load(path);
    }

    [Fact]
    public void Decode_OneTokenOfSeveralCharacters()
    {
        using var t = new TempDir();
        Assert.Equal("おはよう", Load(t).Decode([8]));
    }

    [Fact]
    public void Decode_ACharacterSplitAcrossTokens_IsJoinedBeforeUtf8()
    {
        // 토큰마다 따로 UTF-8로 풀면 깨진 글자 셋이 된다 — 바이트를 다 이은 뒤에 한 번 푼다
        using var t = new TempDir();
        Assert.Equal("お", Load(t).Decode([5, 6, 7]));
    }

    [Fact]
    public void Decode_SpacesAreKept_AsTheReferenceDoes()
    {
        using var t = new TempDir();
        var tok = Load(t);
        Assert.Equal(" presented by", tok.Decode([9, 10, 11, 12, 13]));
        Assert.Equal("연습했다 一緒に！？♡", tok.Decode([14, 15, 16, 17, 18, 19, 20]));
    }

    [Fact]
    public void Decode_SkipsSpecialTokensAndIdsOutsideTheVocabulary()
    {
        // 시작·끝·채움(어휘 안의 [BOS] 등과 덧붙은 <bos> 등 둘 다)과 모델이 낼 수 없는 번호는 글이 아니다
        using var t = new TempDir();
        Assert.Equal("Aおはよう", Load(t).Decode([22, 2, 4, 0, 8, 23, 21, 3, 99999, -1]));
    }

    [Fact]
    public void Decode_BrokenBytes_BecomeTheReplacementCharacter_LikeTheReference()
    {
        // E3 81(끝이 잘린 3바이트 글자) → U+FFFD 하나. E3 · A · 81 → U+FFFD A U+FFFD (tokenizers의 decode와 같다)
        using var t = new TempDir();
        var tok = Load(t);
        Assert.Equal("�", tok.Decode([5, 6]));
        Assert.Equal("�A�", tok.Decode([5, 4, 6]));
    }

    [Fact]
    public void Load_FileThatIsNotABytelevelBpeTokenizer_Throws()
    {
        // 다른 모델의 tokenizer.json을 잘못 받았으면(어휘가 없다) 조용히 빈 글을 내지 않고 여기서 멈춘다
        using var t = new TempDir();
        var path = t.At("tokenizer.json");
        File.WriteAllText(path, "{\"model\":{\"type\":\"WordPiece\"}}");
        Assert.Throws<InvalidDataException>(() => HayaiTokenizer.Load(path));
    }

    // ── 위치 표(.npy) ──

    private static byte[] Npy(string descr, string shape, float[] data, bool fortran = false, int dataBytes = -1)
    {
        string header = $"{{'descr': '{descr}', 'fortran_order': {(fortran ? "True" : "False")}, 'shape': {shape}, }}";
        int pad = 64 - (10 + header.Length + 1) % 64;
        header += new string(' ', pad % 64) + "\n";
        using var ms = new MemoryStream();
        ms.Write([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0]);
        ms.Write(BitConverter.GetBytes((ushort)header.Length));
        ms.Write(Encoding.ASCII.GetBytes(header));
        var raw = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, raw, 0, raw.Length);
        ms.Write(raw, 0, dataBytes < 0 ? raw.Length : dataBytes);
        return ms.ToArray();
    }

    [Fact]
    public void NpyFile_ReadsAFloatTable()
    {
        using var t = new TempDir();
        float[] data = { 1.5f, -2f, 0f, 3.25f, 100f, -0.125f };
        File.WriteAllBytes(t.At("pos.npy"), Npy("<f4", "(2, 3)", data));

        var (read, rows, cols) = NpyFile.ReadFloat2D(t.At("pos.npy"));

        Assert.Equal((2, 3), (rows, cols));
        Assert.Equal(data, read);
    }

    [Theory]
    [InlineData("<f8", "(2, 3)", false, -1)]   // 배정밀도 — 4바이트씩 읽으면 엉뚱한 값이 된다
    [InlineData("<f4", "(6,)", false, -1)]     // 1차원
    [InlineData("<f4", "(2, 3)", true, -1)]    // 열 우선 저장
    [InlineData("<f4", "(2, 3)", false, 20)]   // 내려받다 끊긴 파일(값이 모자란다)
    public void NpyFile_AnythingButACompleteRowMajorFloat32Table_Throws(string descr, string shape, bool fortran, int dataBytes)
    {
        using var t = new TempDir();
        File.WriteAllBytes(t.At("pos.npy"), Npy(descr, shape, new float[6], fortran, dataBytes));

        Assert.Throws<InvalidDataException>(() => NpyFile.ReadFloat2D(t.At("pos.npy")));
    }

    [Fact]
    public void NpyFile_NotANpyFile_Throws()
    {
        using var t = new TempDir();
        File.WriteAllText(t.At("pos.npy"), "<html>Not Found</html>");
        Assert.Throws<InvalidDataException>(() => NpyFile.ReadFloat2D(t.At("pos.npy")));
    }
}
