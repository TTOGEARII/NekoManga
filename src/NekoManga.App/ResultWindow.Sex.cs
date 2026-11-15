using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
// UseWindowsForms가 켜져 있어 Brush·Cursors 등이 WinForms와 겹친다 — 별칭을 유지할 것.
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Size = OpenCvSharp.Size;

namespace NekoManga.App;

// 검열 지우기 탭 — 조각의 성별 표시(재생성일 때): 조각마다 어느 성별의 태그로 그릴지 보여 주고, 눌러서 바꾸면 그 장의 사이드카에 기억한다(MaskSidecar.RegenSex)
public partial class ResultWindow
{
    // ── 조각의 성별 (2026-11-15 사용자 "앞으로 작업하는 모든 페이지가 남/여 구별되서 들어가야해") ──
    // 재생성은 검열 조각마다 성별을 정해 그쪽 태그를 넣는다(RegenRegion — 부위 감지의 근거). 감지가 못 잡는 조각이 있어(사용자 원고 146조각 중 23개) 자동만으로는 다 가릴 수 없다 —
    // 마스크 보기에서 조각마다 표시(남·여·남녀·?)를 띄우고, 누르면 남성 → 여성 → 남녀 → 자동으로 바뀐다. 고른 것은 조각 안의 한 점으로 기억한다(SexMark — 마스크를 고쳐도 그 자리의 조각을 따라간다).
    // 저장은 마스크와 같은 길이다: 바꾸면 '고친 장'이 되어 장을 넘길 때 사이드카에 적히고, 끝난 장이면 ✎([고친 장 모두 다시 렌더링]의 대상)가 붙는다. 원래대로 돌아오면 고친 것이 아니다(SettleMaskDirty).
    // 표시는 SexLayer(배경 없는 Canvas)에 올린다 — 표시만 클릭을 받고 빈 곳은 아래 ImageHost가 칠하기를 받는다. 조각을 세는 일은 점선 미리보기와 같이 돈다(RefreshRegionPreview).
    // 조각은 **2차가 실제로 쓰는 꼴**로 센다(SexRunMask·SexPlan): 저장되는 마스크를(여유 px 없이) 2차가 그리는 그림의 크기로 — 채색된 장은 채색 뒤 그림이 마스크와 크기가 달라,
    // 마스크 좌표로 세면 영역이 다르게 묶여 표시는 '남성 태그'인데 실제로는 남녀 태그로 그려졌다(검수 2026-11-15). 표시(SexMark)는 늘 마스크 좌표로 들고 다닌다(러너가 그릴 때 환산한다).
    private List<SexMark> _sexMarks = [];      // 지금 장에 사용자가 고른 성별 (마스크 좌표)
    private List<SexMark> _sexSaved = [];      // 읽어 온(또는 마지막으로 저장한) 표시 — 달라졌는지 가린다
    private List<(RegenPiece Piece, bool Mixed)>? _sexPieces;   // 표시할 조각(마스크 좌표) — Mixed: 그 조각의 영역에 성별이 다른 조각이 같이 있다
    private int _sexTooMany;                   // 조각이 너무 많아 표시를 생략했을 때의 조각 수 (0 = 생략 안 함)

    /// <summary>조각이 이보다 많으면 표시를 생략한다 — 영역 계획은 조각마다 그림 크기 마스크를 둘씩 든다(검수 실측: 3000×4000에서 조각 40개에 0.9초·1GB). 그런 장은 [모두 남성]·[모두 여성]으로 고른다.</summary>
    private const int SexBadgeMaxPieces = 30;

    private bool SexBadgesWanted => MaskMode && EngineMode == "ComfyRegen" && App.Settings.RegenTagsBySex && _maskMat != null && _maskPre != null;

    /// <summary>조각에 고른 성별이 읽어 온 것과 달라졌는가 — 정리한 꼴(RegenRegion.ResolveMarks: 조각마다 하나, 조각의 점에, 조각 순서로)로 견준다.
    /// 표시를 한 바퀴 돌려 제자리로 오면(찍힌 순서·점의 자리는 달라졌어도) 고친 것이 아니다.</summary>
    private bool SexDirty
    {
        get
        {
            if (_maskMat == null || (_sexMarks.Count == 0 && _sexSaved.Count == 0)) return false;
            var (mask, fx, fy) = SexRunMask();
            using (mask)
                return SexMarks.Format(RegenRegion.ResolveMarks(mask, SexMarks.Scaled(_sexMarks, fx, fy)))
                    != SexMarks.Format(RegenRegion.ResolveMarks(mask, SexMarks.Scaled(_sexSaved, fx, fy)));
        }
    }

    private void ResetSexState(MaskSidecar? sc)
    {
        _sexSaved = [.. SexMarks.Parse(sc?.RegenSex)];
        _sexMarks = [.. _sexSaved];
        ClearSexPieces();
    }

    private void ClearSexPieces()
    {
        _sexPieces = null; _sexTooMany = 0;
        SexLayer.Children.Clear();
    }

    /// <summary>조각을 세는 마스크 — 2차가 쓰는 꼴: 저장되는 마스크(여유 px 없이)를 2차가 그리는 그림(RunImage)의 크기로. fx·fy = 마스크 좌표 → 그 그림의 좌표. 호출자가 해제한다.</summary>
    private (Mat Mask, double Fx, double Fy) SexRunMask()
    {
        var size = RunImage().Img.Size();
        if (size == _maskMat!.Size()) return (_maskMat.Clone(), 1, 1);
        var m = new Mat();
        Cv2.Resize(_maskMat, m, size, 0, 0, InterpolationFlags.Nearest);   // 러너가 마스크를 그림 크기에 맞추는 것과 같게
        return (m, (double)size.Width / _maskMat.Width, (double)size.Height / _maskMat.Height);
    }

    /// <summary>사이드카에 적을 표시(마스크 좌표) — 지금 마스크의 조각에 맞춰 정리한 것. 지운 조각의 표시는 빠진다.</summary>
    private List<SexMark> SexMarksToSave()
    {
        if (_maskMat == null || _sexMarks.Count == 0) return [];
        var (mask, fx, fy) = SexRunMask();
        using (mask) return [.. SexMarks.Scaled(RegenRegion.ResolveMarks(mask, SexMarks.Scaled(_sexMarks, fx, fy)), 1 / fx, 1 / fy)];
    }

    /// <summary>2차와 같은 영역 계획으로 조각을 센다(백그라운드에서 — 창의 Mat을 만지지 않는다). <paramref name="maskPlain"/>: 저장되는 마스크의 사본,
    /// <paramref name="partsRun"/>·<paramref name="runSize"/>: 2차가 그리는 그림의 좌표로 된 부위 상자와 그 크기. 돌려주는 조각은 마스크 좌표.</summary>
    private static List<(RegenPiece Piece, bool Mixed)> SexPlan(Mat maskPlain, IReadOnlyList<BodyPart> partsRun, Size runSize, double pad, double grow, int cropSize, double conf, IReadOnlyList<SexMark> marks)
    {
        bool same = runSize.Width <= 0 || runSize == maskPlain.Size();
        using Mat? resized = same ? null : new Mat();
        if (!same) Cv2.Resize(maskPlain, resized!, runSize, 0, 0, InterpolationFlags.Nearest);
        var m = same ? maskPlain : resized!;
        double fx = (double)m.Width / maskPlain.Width, fy = (double)m.Height / maskPlain.Height;
        var plan = RegenRegion.BuildRegions(m, partsRun, pad, grow, m.Size(), cropSize, conf, SexMarks.Scaled(marks, fx, fy));
        try
        {
            RegenPiece ToMask(RegenPiece p) => same ? p : p with
            {
                Box = new OpenCvSharp.Rect((int)Math.Round(p.Box.X / fx), (int)Math.Round(p.Box.Y / fy), Math.Max(1, (int)Math.Round(p.Box.Width / fx)), Math.Max(1, (int)Math.Round(p.Box.Height / fy))),
                Anchor = new OpenCvSharp.Point((int)Math.Round(p.Anchor.X / fx), (int)Math.Round(p.Anchor.Y / fy)),
            };
            return plan.SelectMany(r => r.Pieces.Select(p => (ToMask(p), r.SexMap != null))).ToList();
        }
        finally { foreach (var r in plan) r.Dispose(); }
    }

    private static (string Label, Brush Fill, string Name) SexLook(RegenKind sex) => sex switch
    {
        RegenKind.Male => ("남", Brushes.DodgerBlue, "남성"),
        RegenKind.Female => ("여", Brushes.DeepPink, "여성"),
        RegenKind.Both => ("남녀", Brushes.MediumPurple, "남녀"),
        _ => ("?", Brushes.DimGray, "모름"),
    };

    /// <summary>조각마다 상자 테두리와 표시를 다시 올린다 — 조각을 다시 센 뒤, 확대가 바뀐 뒤(Redraw), 표시를 누른 뒤.</summary>
    private void DrawSexBadges()
    {
        SexLayer.Children.Clear();
        if (!SexBadgesWanted || _sexPieces == null) { SexLayer.Visibility = Visibility.Collapsed; return; }
        SexLayer.Visibility = Visibility.Visible;
        bool perPiece = App.Settings.RegenTagsPerPiece;
        foreach (var (piece, mixed) in _sexPieces)
        {
            if (piece.Speck) continue;   // 티끌(근거 없는 작은 조각)은 가까운 조각을 따른다 — 표시하지 않는다
            var (label, fill, name) = SexLook(piece.Sex);
            double x = piece.Box.X * _scale - 3, y = piece.Box.Y * _scale - 3;
            var frame = new System.Windows.Shapes.Rectangle
            {
                Width = piece.Box.Width * _scale + 6, Height = piece.Box.Height * _scale + 6, Stroke = fill, StrokeThickness = 1.5, IsHitTestVisible = false,
                StrokeDashArray = piece.ByUser ? null : new DoubleCollection { 3, 2 },   // 자동 판단은 점선, 직접 고른 것은 실선
            };
            Canvas.SetLeft(frame, x); Canvas.SetTop(frame, y);
            SexLayer.Children.Add(frame);

            string what = piece.Sex switch
            {
                RegenKind.Male or RegenKind.Female => $"{name} 태그로 그려요",
                RegenKind.Both => "남녀 태그를 같이 넣어요",
                _ => "성별을 알 수 없는 조각이라 남녀 태그를 같이 넣어요(부위 감지가 못 잡았거나 꺼져 있어요) — 눌러서 골라 주세요",
            };
            string how = piece.ByUser ? $"직접 고름 (자동 판단은 {SexLook(piece.Auto).Name})" : "자동 판단";
            string note = mixed && !perPiece && piece.Sex is RegenKind.Male or RegenKind.Female
                ? "\n※ 같은 범위에 성별이 다른(또는 모르는) 조각이 있어 지금은 남녀 태그로 그려져요 — 그 조각도 골라 주거나, ⚙ 설정의 '자리마다 그 성별의 태그만'을 켜 주세요" : "";
            var badge = new Border
            {
                Background = fill, BorderBrush = piece.ByUser ? Brushes.Gold : Brushes.White, BorderThickness = new Thickness(piece.ByUser ? 2 : 1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Cursor = Cursors.Hand,
                ToolTip = $"{what} — {how}\n누르면 바뀌어요: 남성 → 여성 → 남녀 → 자동 (지금 것의 다음으로){note}",
                Child = new TextBlock { Text = label, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12 },
            };
            var target = piece;
            badge.MouseLeftButtonDown += (_, e) => { e.Handled = true; CycleSex(target); };   // 아래 ImageHost의 칠하기로 넘어가지 않게
            badge.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(badge, Math.Max(0, x)); Canvas.SetTop(badge, Math.Max(0, y - badge.DesiredSize.Height + 1));   // 상자 왼쪽 위에 꼬리표처럼 — 조각을 가리지 않는다
            SexLayer.Children.Add(badge);
        }
    }

    /// <summary>표시를 누를 때 — 남성 → 여성 → 남녀 → 자동. 자동 판단이 보이는 조각은 그와 다른 쪽부터(자동이 남성이면 여성부터 — 같은 것으로 바뀌면 누른 티가 안 난다).</summary>
    private void CycleSex(RegenPiece piece)
    {
        if (_busy || _loading || _maskMat == null) return;
        RegenKind? next = piece.ByUser
            ? piece.Sex switch { RegenKind.Male => RegenKind.Female, RegenKind.Female => RegenKind.Both, _ => null }
            : piece.Auto == RegenKind.Male ? RegenKind.Female : RegenKind.Male;
        if (!ApplySex([piece], next)) { TxtStatus.Text = "조각을 다시 세는 중이에요 — 잠시 뒤 다시 눌러 주세요"; return; }
        TxtStatus.Text = next == null ? $"이 조각을 자동 판단({SexLook(piece.Auto).Name})으로 되돌렸어요"
            : $"이 조각을 {SexLook(next.Value).Name}(으)로 골랐어요 — 다시 그릴 때 {(next == RegenKind.Both ? "남녀 태그를 같이" : SexLook(next.Value).Name + " 태그만")} 넣어요";
    }

    /// <summary>조각들의 성별을 바꾼다(null = 자동). 하나도 못 바꿨으면 false — 누르는 사이 마스크가 바뀌어 그 자리에 조각이 없다(곧 다시 센다).</summary>
    private bool ApplySex(IReadOnlyList<RegenPiece> pieces, RegenKind? sex)
    {
        var (mask, fx, fy) = SexRunMask();
        IReadOnlyList<SexMark> marks = SexMarks.Scaled(_sexMarks, fx, fy);
        var hit = new List<RegenPiece>();
        using (mask)
            foreach (var p in pieces)
            {
                var at = new OpenCvSharp.Point((int)Math.Round(p.Anchor.X * fx), (int)Math.Round(p.Anchor.Y * fy));
                marks = RegenRegion.WithSex(mask, marks, at, sex, out bool onPiece);
                if (onPiece) hit.Add(p);
            }
        if (hit.Count == 0) { RefreshRegionPreview(); return false; }
        _sexMarks = [.. SexMarks.Scaled(marks, 1 / fx, 1 / fy)];
        // 화면은 바로 바꾼다 — 영역이 섞였는지(풍선말의 ※)는 곧 다시 센다(RefreshRegionPreview)
        if (_sexPieces != null)
            _sexPieces = [.. _sexPieces.Select(x => hit.Contains(x.Piece) ? (x.Piece with { Sex = sex ?? x.Piece.Auto, ByUser = sex != null }, x.Mixed) : x)];
        SexTouched();
        return true;
    }

    /// <summary>성별을 바꾼 것도 이 장을 고친 것이다 — 마스크처럼 장을 넘길 때 저장되고, 끝난 장이면 ✎가 붙는다. 읽어 온 것과 같아졌으면 고친 것이 아니다.
    /// 마스크를 손으로 고친 것(_maskEdited — 사이드카의 '수정됨')은 아니다.</summary>
    private void SexTouched()
    {
        if (HasStoredMask)
        {
            _maskDirty = true; BtnSave.IsEnabled = true;
            SettleMaskDirty();
        }
        UpdateRerenderButton();
        UpdateMaskInfo();
        DrawSexBadges();
        RefreshRegionPreview();
    }

    private void BtnSexAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _loading || _maskMat == null) return;
        // 표시가 보이는 마스크 보기에서만 — 결과 보기에서는 아래 [저장]이 결과 그림의 저장이라, 여기서 켜면 엉뚱한 것을 저장하게 된다
        if (!MaskMode) { TxtStatus.Text = "조각의 성별은 [🎭 마스크 보기]에서 골라요"; return; }
        string tag = (sender as FrameworkElement)?.Tag as string ?? "Auto";
        if (tag == "Auto")
        {
            if (_sexMarks.Count == 0) { TxtStatus.Text = "이 장에는 직접 고른 조각이 없어요 — 전부 자동 판단이에요"; return; }
            _sexMarks = [];
            if (_sexPieces != null) _sexPieces = [.. _sexPieces.Select(x => (x.Piece with { Sex = x.Piece.Auto, ByUser = false }, x.Mixed))];
            SexTouched();
            TxtStatus.Text = "이 장의 조각을 전부 자동 판단으로 되돌렸어요";
            return;
        }
        var sex = tag == "Male" ? RegenKind.Male : RegenKind.Female;
        var (mask, fx, fy) = SexRunMask();
        List<OpenCvSharp.Point> anchors;
        using (mask) anchors = RegenRegion.PieceAnchors(mask);
        if (anchors.Count == 0) { TxtStatus.Text = "마스크에 조각이 없어요 — 지울 곳을 먼저 칠해 주세요"; return; }
        _sexMarks = [.. SexMarks.Scaled(anchors.Select(a => new SexMark(a, sex)).ToList(), 1 / fx, 1 / fy)];   // 조각마다 그 점에 — 지난 표시는 전부 갈린다
        if (_sexPieces != null) _sexPieces = [.. _sexPieces.Select(x => (x.Piece with { Sex = sex, ByUser = true }, x.Mixed))];
        SexTouched();
        TxtStatus.Text = $"이 장의 조각 {anchors.Count}개를 전부 {SexLook(sex).Name}(으)로 골랐어요 — 다시 그릴 때 {SexLook(sex).Name} 태그만 넣어요";
    }

    /// <summary>마스크 정보 줄에 붙는 말 — 조각의 성별이 어떻게 나뉘었는지.</summary>
    private string SexInfo()
    {
        if (!SexBadgesWanted) return "";
        if (_sexTooMany > 0) return $" · 조각이 많아({_sexTooMany}개) 성별 표시는 생략 — [모두 남성]·[모두 여성]으로 골라요";
        if (_sexPieces == null) return "";
        var shown = _sexPieces.Where(x => !x.Piece.Speck).Select(x => x.Piece.Sex).ToList();
        if (shown.Count == 0) return "";
        int Count(RegenKind k) => shown.Count(s => s == k);
        var parts = new List<string>();
        if (Count(RegenKind.Male) > 0) parts.Add($"남 {Count(RegenKind.Male)}");
        if (Count(RegenKind.Female) > 0) parts.Add($"여 {Count(RegenKind.Female)}");
        if (Count(RegenKind.Both) > 0) parts.Add($"남녀 {Count(RegenKind.Both)}");
        if (Count(RegenKind.Unknown) > 0) parts.Add($"모름 {Count(RegenKind.Unknown)}");
        return " · 조각 성별: " + string.Join(" · ", parts) + " (표시를 눌러 바꿔요)";
    }
}
