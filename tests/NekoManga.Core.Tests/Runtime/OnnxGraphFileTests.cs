using System.Text;
using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>OnnxGraphFile — ONNX 파일을 protobuf 와이어 수준으로 읽고, 필요한 노드만 새 파일로 잘라 낸다(Aletheia Mask R-CNN 단계 분할).
/// 실제 모델 대신 onnx.proto의 필드 번호대로 테스트 안에서 조립한 작은 그래프를 쓴다 — 세션은 만들지 않는다.</summary>
public class OnnxGraphFileTests
{
    // ── protobuf 조립 ──

    private static byte[] Varint(ulong v)
    {
        var bytes = new List<byte>();
        while (v >= 0x80) { bytes.Add((byte)(v | 0x80)); v >>= 7; }
        bytes.Add((byte)v);
        return [.. bytes];
    }

    private static byte[] Join(IEnumerable<byte[]> parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>길이 구분 필드(wire 2).</summary>
    private static byte[] Msg(int field, byte[] payload) => Join([Varint(((ulong)field << 3) | 2), Varint((ulong)payload.Length), payload]);

    private static byte[] Str(int field, string s) => Msg(field, Encoding.UTF8.GetBytes(s));

    /// <summary>varint 필드(wire 0). 음수 int64는 10바이트 varint가 된다.</summary>
    private static byte[] Num(int field, long v) => Join([Varint((ulong)field << 3), Varint(unchecked((ulong)v))]);

    /// <summary>고정 4바이트 필드(wire 5).</summary>
    private static byte[] Fixed32(int field, float v) => Join([Varint(((ulong)field << 3) | 5), BitConverter.GetBytes(v)]);

    private static byte[] Floats(params float[] values) => values.SelectMany(v => BitConverter.GetBytes(v)).ToArray();

    // ── onnx.proto 메시지 ──

    /// <summary>NodeProto: input 1 · output 2 · name 3 · op_type 4 · attribute 5.</summary>
    private static byte[] Node(string name, string op, string[] inputs, string[] outputs, params byte[][] attributes) =>
        Join([.. inputs.Select(i => Str(1, i)), .. outputs.Select(o => Str(2, o)), Str(3, name), Str(4, op), .. attributes.Select(a => Msg(5, a))]);

    /// <summary>AttributeProto: name 1 · f 2 · i 3 · t 5 · g 6 · graphs 11 · type 20.</summary>
    private static byte[] IntAttr(string name, long v) => Join([Str(1, name), Num(3, v), Num(20, 2)]);

    private static byte[] FloatAttr(string name, float v) => Join([Str(1, name), Fixed32(2, v), Num(20, 1)]);

    private static byte[] TensorAttr(string name, byte[] tensor) => Join([Str(1, name), Msg(5, tensor), Num(20, 4)]);

    private static byte[] GraphAttr(string name, byte[] graph) => Join([Str(1, name), Msg(6, graph), Num(20, 5)]);

    private static byte[] GraphsAttr(string name, params byte[][] graphs) => Join([Str(1, name), .. graphs.Select(g => Msg(11, g)), Num(20, 10)]);

    /// <summary>TensorProto: dims 1 · data_type 2(FLOAT=1) · name 8 · raw_data 9. protobuf는 필드 순서가 자유라 이름이 raw_data 뒤에 올 수도 있다.</summary>
    private static byte[] Tensor(string name, byte[] raw, bool nameAfterData = false) => nameAfterData
        ? Join([Num(1, raw.Length / 4), Num(2, 1), Msg(9, raw), Str(8, name)])
        : Join([Num(1, raw.Length / 4), Num(2, 1), Str(8, name), Msg(9, raw)]);

    /// <summary>ValueInfoProto: name 1 · type 2 → TypeProto.tensor_type 1 → elem_type 1(FLOAT=1) · shape 2 → dim 1 → dim_value 1.</summary>
    private static byte[] Value(string name, params long[] dims)
    {
        var tensorType = dims.Length == 0 ? Num(1, 1) : Join([Num(1, 1), Msg(2, Join(dims.Select(d => Msg(1, Num(1, d)))))]);
        return Join([Str(1, name), Msg(2, Msg(1, tensorType))]);
    }

    /// <summary>GraphProto: node 1 · name 2 · initializer 5 · input 11 · output 12.</summary>
    private static byte[] Graph(string name, byte[][] nodes, byte[][]? inits = null, byte[][]? inputs = null, byte[][]? outputs = null) =>
        Join([.. nodes.Select(n => Msg(1, n)), Str(2, name), .. (inits ?? []).Select(i => Msg(5, i)),
              .. (inputs ?? []).Select(v => Msg(11, v)), .. (outputs ?? []).Select(v => Msg(12, v))]);

    /// <summary>ModelProto: ir_version 1 · producer_name 2 · graph 7 · opset_import 8 — 그래프를 일부러 다른 필드 사이에 둔다.</summary>
    private static byte[] Model(byte[] graph) =>
        Join([Num(1, 8), Str(2, "neko-test"), Msg(7, graph), Msg(8, Join([Str(1, ""), Num(2, 17)]))]);

    // ── 결과 판독 — 검증용 최소 protobuf 읽기 ──

    private sealed record Field(int Number, ulong Value, byte[] Payload);

    private static List<Field> Fields(byte[] data)
    {
        var fields = new List<Field>();
        int p = 0;
        ulong Read()
        {
            ulong v = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = data[p++];
                v |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) return v;
            }
        }
        while (p < data.Length)
        {
            ulong tag = Read();
            int number = (int)(tag >> 3);
            switch (tag & 7)
            {
                case 0:
                    fields.Add(new(number, Read(), []));
                    break;
                case 2:
                {
                    int len = (int)Read();
                    fields.Add(new(number, 0, data[p..(p + len)]));
                    p += len;
                    break;
                }
                default:
                    throw new InvalidDataException($"검증용 판독기가 모르는 wire type {tag & 7}");
            }
        }
        return fields;
    }

    private static List<byte[]> Repeated(byte[] message, int number) =>
        Fields(message).Where(f => f.Number == number).Select(f => f.Payload).ToList();

    private static string NameOf(byte[] message, int nameNumber) =>
        Encoding.UTF8.GetString(Fields(message).First(f => f.Number == nameNumber).Payload);

    private static byte[] GraphOf(string modelPath) => Repeated(File.ReadAllBytes(modelPath), 7).Single();

    /// <summary>ValueInfoProto의 elem_type (type 2 → tensor_type 1 → elem_type 1).</summary>
    private static ulong ElemType(byte[] valueInfo) =>
        Fields(Repeated(Repeated(valueInfo, 2).Single(), 1).Single()).Single(f => f.Number == 1).Value;

    private static string Signature(Field f) => $"{f.Number}:{f.Value}:{Convert.ToHexString(f.Payload)}";

    private static string[] Sorted(IEnumerable<string> names) => names.Order(StringComparer.Ordinal).ToArray();

    private static string Write(TempDir t, string name, byte[] bytes)
    {
        var path = t.At(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ── 시험용 그래프 ──

    /// <summary>x ─Mul(w)→ a ─Add(b)→ c ─Softmax(axis=-1)→ d ─┐
    ///  x ─LeakyRelu(alpha=0.1)→ e ────────────────────────┴ Add → y (그래프 출력)
    ///  Constant → k : 아무도 쓰지 않는 노드.
    /// 속성 값(음수 정수 = 10바이트 varint, float = 고정 4바이트, 텐서)은 건너뛰어야 하고 그래프 속성으로 오해하면 안 된다.</summary>
    private static byte[] LinearModel() => Model(Graph("linear",
        [
            Node("mul", "Mul", ["x", "w"], ["a"]),
            Node("add", "Add", ["a", "b"], ["c"]),
            Node("softmax", "Softmax", ["c"], ["d"], IntAttr("axis", -1)),
            Node("leaky", "LeakyRelu", ["x"], ["e"], FloatAttr("alpha", 0.1f)),
            Node("const", "Constant", [], ["k"], TensorAttr("value", Tensor("k_value", Floats(1f)))),
            Node("out", "Add", ["d", "e"], ["y"]),
        ],
        inits: [Tensor("w", Floats(2f)), Tensor("b", Floats(3f))],
        inputs: [Value("x", 1, 4)],
        outputs: [Value("y", 1, 4)]));

    /// <summary>Loop 본문이 바깥 텐서를 끌어다 쓰는 그래프 — Aletheia Mask R-CNN의 ROI 정렬 Loop와 같은 꼴.
    ///  x ─Relu→ c,  x ─Neg→ z,  Constant → M,  w2 = 초기값
    ///  loop = Loop(M, "") 본문: 입력 i·cond / t = Mul(c, w2) / u = If(cond){ then: Add(t, z) | else: Identity(t) } / cond_out = Identity(cond)
    ///  branches = 본문 여러 개(graphs 속성): Identity(w2) · Neg(z)
    ///  after = Relu(loop_out) → y</summary>
    private static byte[] LoopModel() => Model(Graph("loop",
        [
            Node("pre", "Relu", ["x"], ["c"]),
            Node("zsrc", "Neg", ["x"], ["z"]),
            Node("trip", "Constant", [], ["M"], TensorAttr("value", Tensor("M_value", Floats(4f)))),
            Node("loop", "Loop", ["M", ""], ["loop_out"], GraphAttr("body", Graph("body",
                [
                    Node("body_mul", "Mul", ["c", "w2"], ["t"]),
                    Node("body_if", "If", ["cond"], ["u"],
                        GraphAttr("then_branch", Graph("then", [Node("then_add", "Add", ["t", "z"], ["u_then"])], outputs: [Value("u_then")])),
                        GraphAttr("else_branch", Graph("else", [Node("else_id", "Identity", ["t"], ["u_else"])], outputs: [Value("u_else")]))),
                    Node("body_cond", "Identity", ["cond"], ["cond_out"]),
                ],
                inputs: [Value("i"), Value("cond")],
                outputs: [Value("cond_out"), Value("u")]))),
            Node("branches", "Custom", ["x"], ["s"], GraphsAttr("bodies",
                Graph("g1", [Node("g1_id", "Identity", ["w2"], ["g1_out"])], outputs: [Value("g1_out")]),
                Graph("g2", [Node("g2_neg", "Neg", ["z"], ["g2_out"])], outputs: [Value("g2_out")]))),
            Node("after", "Relu", ["loop_out"], ["y"]),
        ],
        inits: [Tensor("w2", Floats(0.5f))],
        inputs: [Value("x", 1, 4)],
        outputs: [Value("y")]));

    // ── 읽기 ──

    [Fact]
    public void Load_LinearGraph_ReadsNodesInFileOrder()
    {
        using var t = new TempDir();

        var g = OnnxGraphFile.Load(Write(t, "linear.onnx", LinearModel()));

        // 노드 순서는 파일 순서(= 위상 순서) 그대로 — 잘라 낸 모델도 이 순서로 적는다
        Assert.Equal(new[] { "mul", "add", "softmax", "leaky", "const", "out" }, g.Nodes.Select(n => n.Name));
        Assert.Equal(new[] { "Mul", "Add", "Softmax", "LeakyRelu", "Constant", "Add" }, g.Nodes.Select(n => n.OpType));
        Assert.Equal(new[] { "a", "b" }, g.Nodes[1].Inputs);
        Assert.Equal(new[] { "c" }, g.Nodes[1].Outputs);
        Assert.Empty(g.Nodes[4].Inputs);
        // 본문 그래프가 없는 노드는 암묵 입력이 없다 — 텐서 속성(Constant)도 그래프가 아니다
        Assert.All(g.Nodes, n => Assert.Empty(n.Implicit));
    }

    [Fact]
    public void Load_SubgraphAttributes_RecordOuterTensorsAsImplicitInputs()
    {
        using var t = new TempDir();

        var g = OnnxGraphFile.Load(Write(t, "loop.onnx", LoopModel()));

        // 최상위 노드만 목록에 든다 — Loop·If 본문의 노드는 속성 안에 남는다
        Assert.Equal(new[] { "pre", "zsrc", "trip", "loop", "branches", "after" }, g.Nodes.Select(n => n.Name));
        // 본문이 스스로 정의하지 않고 쓰는 텐서만 암묵 입력이다: c·w2(본문 노드), z(If의 then 분기 — 중첩 본문을 거쳐 올라온다).
        // t는 본문 안에서 만들고 i·cond는 본문 입력이라 빠진다
        Assert.Equal(new[] { "c", "w2", "z" }, Sorted(g.Nodes.Single(n => n.Name == "loop").Implicit));
        // 여러 본문(graphs) 속성도 같은 규칙. 명시 입력(x)은 암묵 입력이 아니다
        var branches = g.Nodes.Single(n => n.Name == "branches");
        Assert.Equal(new[] { "w2", "z" }, Sorted(branches.Implicit));
        Assert.Equal(new[] { "x" }, branches.Inputs);
    }

    [Theory]
    [InlineData("")]                       // 빈 파일
    [InlineData("0808")]                   // ir_version만 있고 그래프가 없다
    [InlineData("3801")]                   // 그래프 번호(7)지만 길이 구분 필드가 아니다
    [InlineData("0B")]                     // wire type 3(옛 group) — ONNX에 쓰이지 않는 형식
    [InlineData("3A010F")]                 // 그래프 안에 wire type 7
    [InlineData("FFFFFFFFFFFFFFFFFFFF")]   // 64비트를 넘는 varint
    public void Load_MalformedFile_ThrowsInvalidData(string hex)
    {
        using var t = new TempDir();
        var path = Write(t, "깨진 모델.onnx", Convert.FromHexString(hex));

        // 호출자(AletheiaDetector)는 실패하면 통짜 모델로 돌아간다 — 손상은 엉뚱한 예외가 아니라 InvalidDataException으로 알린다
        Assert.Throws<InvalidDataException>(() => OnnxGraphFile.Load(path));
    }

    // ── 잘라 내기 ──

    [Fact]
    public void Extract_IntermediateOutput_KeepsOnlyItsAncestors()
    {
        using var t = new TempDir();
        var g = OnnxGraphFile.Load(Write(t, "linear.onnx", LinearModel()));
        var outPath = t.At("단계 모델 폴더", "앞 단계.onnx");   // 아직 없는 하위 폴더

        var inputs = g.Extract(outPath, ["c"], []);

        Assert.Equal(new[] { "x" }, inputs);
        Assert.Equal(new[] { "mul", "add" }, OnnxGraphFile.Load(outPath).Nodes.Select(n => n.Name));
        var graph = GraphOf(outPath);
        Assert.Equal(new[] { "b", "w" }, Sorted(Repeated(graph, 5).Select(i => NameOf(i, 8))));
        // 원래 그래프 입력은 형상까지 원문 그대로 옮긴다
        Assert.Equal(Value("x", 1, 4), Assert.Single(Repeated(graph, 11)));
        // 중간 텐서를 출력으로 삼으면 float 텐서로 새로 적는다
        var output = Assert.Single(Repeated(graph, 12));
        Assert.Equal("c", NameOf(output, 1));
        Assert.Equal(1UL, ElemType(output));
        Assert.False(File.Exists(outPath + ".part"));
    }

    [Fact]
    public void Extract_StopAtTensor_BecomesFloatInputAndCutsUpstream()
    {
        using var t = new TempDir();
        var g = OnnxGraphFile.Load(Write(t, "linear.onnx", LinearModel()));
        var outPath = t.At("뒤 단계.onnx");
        File.WriteAllText(outPath, "지난번에 잘라 둔 조각");   // 원본 모델이 바뀌면 같은 이름으로 다시 자른다

        // k는 y와 무관한 멈춤 텐서 — 실제로 쓰인 입력만 돌려준다
        var inputs = g.Extract(outPath, ["y"], ["c", "k"]);

        Assert.Equal(new[] { "c", "x" }, inputs);
        Assert.Equal(new[] { "softmax", "leaky", "out" }, OnnxGraphFile.Load(outPath).Nodes.Select(n => n.Name));
        var graph = GraphOf(outPath);
        Assert.Empty(Repeated(graph, 5));   // w·b는 멈춤 지점 위쪽이라 따라오지 않는다
        var declared = Repeated(graph, 11);
        Assert.Equal(new[] { "c", "x" }, declared.Select(v => NameOf(v, 1)));
        Assert.Equal(1UL, ElemType(declared[0]));                         // 멈춤 텐서는 float 입력으로 새로 적는다
        Assert.Equal(Value("x", 1, 4), declared[1]);                      // 원래 입력은 원문 그대로
        Assert.Equal(Value("y", 1, 4), Assert.Single(Repeated(graph, 12)));   // 원래 출력도 원문 그대로
    }

    [Fact]
    public void Extract_LoopNode_PullsInTensorsItsBodyUses()
    {
        using var t = new TempDir();
        var g = OnnxGraphFile.Load(Write(t, "loop.onnx", LoopModel()));

        // 본문이 끌어다 쓰는 c(pre)·z(zsrc)·w2(초기값)가 빠지면 잘라 낸 모델을 실행할 수 없다
        var whole = t.At("loop_whole.onnx");
        Assert.Equal(new[] { "x" }, g.Extract(whole, ["loop_out"], []));
        Assert.Equal(new[] { "pre", "zsrc", "trip", "loop" }, OnnxGraphFile.Load(whole).Nodes.Select(n => n.Name));
        Assert.Equal(new[] { "w2" }, Repeated(GraphOf(whole), 5).Select(i => NameOf(i, 8)));

        // 특징 맵(c)을 멈춤 목록에 넣으면 그 위쪽(pre)은 빠지고 c가 입력이 된다 — AletheiaDetector가 단계를 나누는 방식
        var tail = t.At("loop_tail.onnx");
        Assert.Equal(new[] { "c", "x" }, g.Extract(tail, ["loop_out"], ["c"]));
        var part = OnnxGraphFile.Load(tail);
        Assert.Equal(new[] { "zsrc", "trip", "loop" }, part.Nodes.Select(n => n.Name));
        // Loop 본문은 원문 그대로 옮겨져 다시 읽어도 암묵 입력이 같다
        Assert.Equal(new[] { "c", "w2", "z" }, Sorted(part.Nodes.Single(n => n.Name == "loop").Implicit));
    }

    [Fact]
    public void Extract_LargeInitializer_CopiedByteForByteWithModelFields()
    {
        using var t = new TempDir();
        // 길이 varint가 3바이트가 되는 크기(16383바이트 초과) — 그래프 길이를 잘못 세면 다시 읽을 수 없다
        var raw = Enumerable.Range(0, 70_000).Select(i => (byte)(i % 251)).ToArray();
        var big = Tensor("big", raw, nameAfterData: true);   // 이름이 큰 raw_data 뒤에 있어도 찾는다
        var model = Model(Graph("big",
            [Node("add", "Add", ["x", "big"], ["s"]), Node("relu", "Relu", ["s"], ["y"])],
            inits: [big], inputs: [Value("x", 17_500)], outputs: [Value("y", 17_500)]));
        var g = OnnxGraphFile.Load(Write(t, "big.onnx", model));
        var outPath = t.At("big_part.onnx");

        Assert.Equal(new[] { "x" }, g.Extract(outPath, ["s"], []));

        // 그래프 밖의 ModelProto 필드(ir_version·producer·opset)는 그대로 — 실행기가 opset을 보고 연산자를 고른다
        Assert.Equal(Fields(model).Where(f => f.Number != 7).Select(Signature),
                     Fields(File.ReadAllBytes(outPath)).Where(f => f.Number != 7).Select(Signature));
        // 가중치는 다시 쓰지 않고 원문 바이트 그대로 옮긴다
        Assert.Equal(big, Assert.Single(Repeated(GraphOf(outPath), 5)));
        Assert.Equal(new[] { "add" }, OnnxGraphFile.Load(outPath).Nodes.Select(n => n.Name));
    }

    [Fact]
    public void Extract_UnicodeNamesAndOmittedOptionalInputs_SurviveRoundTrip()
    {
        using var t = new TempDir();
        // 이름의 UTF-8 바이트 수는 글자 수와 다르다 — 새로 적는 입력의 길이를 바이트로 세야 다시 읽힌다.
        // 빈 이름 입력은 생략한 선택 입력(ONNX 규약)이라 의존성으로 찾지 않는다
        var model = Model(Graph("유니코드 그래프",
            [
                Node("특징 추출", "Relu", ["입력 영상"], ["特徴マップ"]),
                Node("크기 조정", "Resize", ["特徴マップ", "", "", "배율"], ["결과 텐서"]),
            ],
            inits: [Tensor("배율", Floats(1f, 1f, 2f, 2f))],
            inputs: [Value("입력 영상", 1, 3, 64, 64)],
            outputs: [Value("결과 텐서", 1, 3, 128, 128)]));
        var g = OnnxGraphFile.Load(Write(t, "유니코드 모델.onnx", model));
        Assert.Equal(new[] { "특징 추출", "크기 조정" }, g.Nodes.Select(n => n.Name));
        var outPath = t.At("뒤 단계 テスト.onnx");

        Assert.Equal(new[] { "特徴マップ" }, g.Extract(outPath, ["결과 텐서"], ["特徴マップ"]));

        var resize = Assert.Single(OnnxGraphFile.Load(outPath).Nodes);
        Assert.Equal(new[] { "特徴マップ", "", "", "배율" }, resize.Inputs);
        var input = Assert.Single(Repeated(GraphOf(outPath), 11));
        Assert.Equal("特徴マップ", NameOf(input, 1));
        Assert.Equal(1UL, ElemType(input));
    }

    [Fact]
    public void Extract_TensorWithoutSource_ThrowsAndWritesNothing()
    {
        using var t = new TempDir();
        var g = OnnxGraphFile.Load(Write(t, "linear.onnx", LinearModel()));
        var broken = OnnxGraphFile.Load(Write(t, "broken.onnx", Model(Graph("broken",
            [Node("add", "Add", ["x", "ghost"], ["y"])], inputs: [Value("x")], outputs: [Value("y")]))));
        var outPath = t.At("실패.onnx");

        Assert.Throws<InvalidDataException>(() => g.Extract(outPath, ["없는 텐서"], []));
        // 노드가 만들지 않는 텐서(그래프 입력·초기값)는 출력으로 삼을 수 없다
        Assert.Throws<InvalidDataException>(() => g.Extract(outPath, ["x"], []));
        Assert.Throws<InvalidDataException>(() => g.Extract(outPath, ["w"], []));
        // 노드 입력이 어디서도 오지 않는 끊긴 그래프
        Assert.Throws<InvalidDataException>(() => broken.Extract(outPath, ["y"], []));

        // 실패한 추출은 반쯤 쓴 파일을 남기지 않는다
        Assert.False(File.Exists(outPath));
        Assert.False(File.Exists(outPath + ".part"));
    }
}
