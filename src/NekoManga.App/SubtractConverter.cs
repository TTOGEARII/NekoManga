using System.Globalization;
using System.Windows.Data;

namespace NekoManga.App;

/// <summary>숫자에서 ConverterParameter만큼 뺀다(음수면 0). 결과 창 '선택한 풍선' 콤보의 항목 MaxWidth = 콤보 폭 − 항목 안쪽 여백(32px)에 쓴다(2026-11-26).
/// 펼친 목록(Popup)은 항목을 폭 제한 없이 재서, 항목에 상한이 없으면 가장 긴 글만큼 화면 폭까지 넓어진다 — 콤보 폭 그대로 걸면 여백만큼(32px) 목록이 콤보보다 넓다.</summary>
public sealed class SubtractConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value is double d ? d : 0;
        double sub = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
        return Math.Max(0, v - sub);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
