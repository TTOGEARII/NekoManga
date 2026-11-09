using System.Text;

namespace NekoManga.Core.Engine;

/// <summary>ONNX 파일을 protobuf **와이어 수준**에서 읽어 부분 그래프를 새 파일로 잘라 내는 최소 도구 (라이브러리 의존 없음).
///
/// 왜 필요한가: Aletheia Lens의 Mask R-CNN(weights.onnx)은 tf2onnx가 ROI 정렬(tf.image.crop_and_resize)을 **ROI마다 특징 맵 전체를 Slice하는 Loop**로
/// 풀어 놓아 CPU에서 쪽당 8.5초 중 5.5초를 거기서 쓰고, 그 Loop 안의 Resize(tf_crop_and_resize)는 DirectML에서 틀린 값을 낸다(2026-11-09 실측).
/// 그래서 그 Loop만 빼고 앞·뒤 단계를 따로 실행한다(<see cref="AletheiaDetector"/>) — 그러려면 모델을 단계별 파일로 잘라야 한다.
///
/// 노드·초기값은 **원문 바이트 그대로** 옮긴다(가중치를 다시 쓰지 않는다). 읽는 필드:
/// ModelProto.graph(7) · GraphProto.node(1)/name(2)/initializer(5)/input(11)/output(12) · NodeProto.input(1)/output(2)/name(3)/op_type(4)/attribute(5) ·
/// AttributeProto.g(6)/graphs(11) · TensorProto.name(8) · ValueInfoProto.name(1). Loop/If 본문이 바깥에서 끌어다 쓰는 텐서(암묵 입력)도 의존성으로 친다.</summary>
internal sealed class OnnxGraphFile
{
    public sealed record Node(int Start, int Length, string Name, string OpType, string[] Inputs, string[] Outputs, string[] Implicit);

    private readonly byte[] _data;
    private readonly List<(int Start, int Length)> _modelFields = new();   // graph를 뺀 ModelProto 필드 (태그 포함 원문)
    private (int Start, int Length) _graphName;                             // 태그 포함 원문 (없으면 길이 0)
    private readonly Dictionary<string, (int Start, int Length)> _initializers = new(StringComparer.Ordinal);   // payload 범위
    private readonly Dictionary<string, (int Start, int Length)> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Start, int Length)> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _producer = new(StringComparer.Ordinal);

    public List<Node> Nodes { get; } = new();

    private OnnxGraphFile(byte[] data) => _data = data;

    public static OnnxGraphFile Load(string path)
    {
        var f = new OnnxGraphFile(File.ReadAllBytes(path));
        f.ParseModel();
        return f;
    }

    // ── 와이어 읽기 ──
    private ulong Varint(ref int p)
    {
        ulong v = 0; int shift = 0;
        while (true)
        {
            byte b = _data[p++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("ONNX 파일이 손상됐어요 (varint)");
        }
    }

    /// <summary>필드 하나를 읽는다. 길이 구분 필드면 payload 범위를, 아니면 길이 0을 돌려준다. p는 필드 끝으로 간다.</summary>
    private (int Field, int Wire, int Start, int Length) Field(ref int p)
    {
        ulong tag = Varint(ref p);
        int field = (int)(tag >> 3), wire = (int)(tag & 7);
        switch (wire)
        {
            case 0: Varint(ref p); return (field, wire, p, 0);
            case 1: p += 8; return (field, wire, p, 0);
            case 5: p += 4; return (field, wire, p, 0);
            case 2:
            {
                int len = checked((int)Varint(ref p));
                int start = p;
                p = checked(p + len);
                return (field, wire, start, len);
            }
            default: throw new InvalidDataException($"ONNX 파일이 손상됐어요 (wire type {wire})");
        }
    }

    private string Str(int start, int len) => Encoding.UTF8.GetString(_data, start, len);

    private void ParseModel()
    {
        int p = 0, graphStart = -1, graphLen = 0;
        while (p < _data.Length)
        {
            int tagStart = p;
            var (field, wire, start, len) = Field(ref p);
            if (field == 7 && wire == 2) { graphStart = start; graphLen = len; }
            else _modelFields.Add((tagStart, p - tagStart));
        }
        if (graphStart < 0) throw new InvalidDataException("ONNX 파일에 그래프가 없어요");

        p = graphStart; int end = graphStart + graphLen;
        while (p < end)
        {
            int tagStart = p;
            var (field, wire, start, len) = Field(ref p);
            if (wire != 2) continue;
            switch (field)
            {
                case 1: Nodes.Add(ParseNode(start, len)); break;
                case 2: _graphName = (tagStart, p - tagStart); break;
                case 5: _initializers[NameField(start, len, 8)] = (start, len); break;
                case 11: _inputs[NameField(start, len, 1)] = (start, len); break;
                case 12: _outputs[NameField(start, len, 1)] = (start, len); break;
            }
        }
        for (int i = 0; i < Nodes.Count; i++)
            foreach (var o in Nodes[i].Outputs)
                if (o.Length > 0) _producer[o] = i;
    }

    /// <summary>메시지 안에서 문자열 필드 하나(이름)만 찾는다 — 큰 raw_data는 길이만 보고 건너뛴다.</summary>
    private string NameField(int start, int len, int nameField)
    {
        int p = start, end = start + len;
        while (p < end)
        {
            var (field, wire, s, l) = Field(ref p);
            if (field == nameField && wire == 2) return Str(s, l);
        }
        return "";
    }

    private Node ParseNode(int start, int len)
    {
        var inputs = new List<string>(); var outputs = new List<string>(); var implicitIn = new HashSet<string>(StringComparer.Ordinal);
        string name = "", op = "";
        int p = start, end = start + len;
        while (p < end)
        {
            var (field, wire, s, l) = Field(ref p);
            if (wire != 2) continue;
            switch (field)
            {
                case 1: inputs.Add(Str(s, l)); break;
                case 2: outputs.Add(Str(s, l)); break;
                case 3: name = Str(s, l); break;
                case 4: op = Str(s, l); break;
                case 5: AttributeGraphs(s, l, implicitIn); break;
            }
        }
        return new Node(start, len, name, op, inputs.ToArray(), outputs.ToArray(), implicitIn.ToArray());
    }

    private void AttributeGraphs(int start, int len, HashSet<string> implicitIn)
    {
        int p = start, end = start + len;
        while (p < end)
        {
            var (field, wire, s, l) = Field(ref p);
            if (wire == 2 && field is 6 or 11) SubgraphImplicit(s, l, implicitIn);
        }
    }

    /// <summary>본문 그래프가 자기 안에서 정의하지 않고 쓰는 텐서 이름 = 바깥 범위에서 끌어온 것.</summary>
    private void SubgraphImplicit(int start, int len, HashSet<string> result)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        int p = start, end = start + len;
        while (p < end)
        {
            var (field, wire, s, l) = Field(ref p);
            if (wire != 2) continue;
            switch (field)
            {
                case 1:
                {
                    var n = ParseNode(s, l);
                    foreach (var o in n.Outputs) defined.Add(o);
                    foreach (var i in n.Inputs) if (i.Length > 0) used.Add(i);
                    foreach (var i in n.Implicit) used.Add(i);
                    break;
                }
                case 5: defined.Add(NameField(s, l, 8)); break;
                case 11: defined.Add(NameField(s, l, 1)); break;
            }
        }
        foreach (var u in used) if (!defined.Contains(u)) result.Add(u);
    }

    // ── 잘라 내기 ──
    /// <summary><paramref name="outputs"/>를 만드는 데 필요한 노드만 담은 모델을 쓴다. <paramref name="stopAt"/>의 텐서는 더 거슬러 올라가지 않고 **입력**으로 삼는다
    /// (전부 float 텐서여야 한다 — 새 입력·출력의 형식을 float로 적는다). 실제로 쓰인 입력 이름을 돌려준다.</summary>
    public IReadOnlyList<string> Extract(string outPath, IReadOnlyCollection<string> outputs, IReadOnlyCollection<string> stopAt)
    {
        var stop = new HashSet<string>(stopAt, StringComparer.Ordinal);
        var nodeSet = new HashSet<int>(); var initSet = new HashSet<string>(StringComparer.Ordinal);
        var inputList = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(outputs);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (t.Length == 0 || !seen.Add(t)) continue;
            if (stop.Contains(t)) { inputList.Add(t); continue; }
            if (_producer.TryGetValue(t, out int ni))
            {
                if (nodeSet.Add(ni))
                {
                    foreach (var i in Nodes[ni].Inputs) stack.Push(i);
                    foreach (var i in Nodes[ni].Implicit) stack.Push(i);
                }
            }
            else if (_initializers.ContainsKey(t)) initSet.Add(t);
            else if (_inputs.ContainsKey(t)) inputList.Add(t);
            else throw new InvalidDataException($"ONNX 그래프에서 텐서를 찾지 못했어요: {t}");
        }
        foreach (var o in outputs)
            if (!_producer.ContainsKey(o)) throw new InvalidDataException($"ONNX 그래프에 출력 텐서가 없어요: {o}");

        // 그래프 본문 조립 (노드는 원래 순서 = 위상 순서)
        var parts = new List<(byte Tag, int Start, int Length, byte[]? Bytes)>();
        foreach (int ni in nodeSet.OrderBy(i => i)) parts.Add((0x0A, Nodes[ni].Start, Nodes[ni].Length, null));
        foreach (var n in initSet.OrderBy(s => s, StringComparer.Ordinal)) parts.Add((0x2A, _initializers[n].Start, _initializers[n].Length, null));
        inputList.Sort(StringComparer.Ordinal);
        foreach (var n in inputList)
        {
            if (!stop.Contains(n) && _inputs.TryGetValue(n, out var r)) parts.Add((0x5A, r.Start, r.Length, null));
            else parts.Add((0x5A, 0, 0, FloatValueInfo(n)));
        }
        foreach (var n in outputs)
        {
            if (_outputs.TryGetValue(n, out var r)) parts.Add((0x62, r.Start, r.Length, null));
            else parts.Add((0x62, 0, 0, FloatValueInfo(n)));
        }
        long graphLen = _graphName.Length;
        foreach (var (_, _, length, bytes) in parts)
        {
            int l = bytes?.Length ?? length;
            graphLen += 1 + VarintSize((ulong)l) + l;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var tmp = outPath + ".part";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            foreach (var (s, l) in _modelFields) fs.Write(_data, s, l);
            fs.WriteByte(0x3A); WriteVarint(fs, (ulong)graphLen);
            if (_graphName.Length > 0) fs.Write(_data, _graphName.Start, _graphName.Length);
            foreach (var (tag, start, length, bytes) in parts)
            {
                fs.WriteByte(tag);
                if (bytes != null) { WriteVarint(fs, (ulong)bytes.Length); fs.Write(bytes, 0, bytes.Length); }
                else { WriteVarint(fs, (ulong)length); fs.Write(_data, start, length); }
            }
        }
        File.Move(tmp, outPath, overwrite: true);
        return inputList;
    }

    /// <summary>ValueInfoProto { name, type { tensor_type { elem_type = FLOAT } } } — 형상은 적지 않는다(실행 때 정해진다).</summary>
    private static byte[] FloatValueInfo(string name)
    {
        var nb = Encoding.UTF8.GetBytes(name);
        using var ms = new MemoryStream();
        ms.WriteByte(0x0A); WriteVarint(ms, (ulong)nb.Length); ms.Write(nb, 0, nb.Length);
        ms.Write([0x12, 0x04, 0x0A, 0x02, 0x08, 0x01]);
        return ms.ToArray();
    }

    private static int VarintSize(ulong v) { int n = 1; while (v >= 0x80) { v >>= 7; n++; } return n; }

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }
}
