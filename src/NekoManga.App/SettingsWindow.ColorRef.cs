using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using NekoManga.Core.Engine;
using NekoManga.Core.Runtime;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace NekoManga.App;

// 채색 탭의 표지 색 참조 — 표지 끌어다 놓기·찾기, 미리보기, 색 태그 뽑기
public partial class SettingsWindow
{
    private void RefDropZone_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void RefDropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            TxtColorRefImage.Text = files[0];
            TxtColorRefTags.Text = "";   // 표지가 바뀌면 캐시된 태그는 무효
        }
    }

    private void TxtColorRefImage_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateRefPreview();

    /// <summary>등록된 표지의 미리보기를 드롭존에 띄운다 (없으면 안내 문구).</summary>
    private void UpdateRefPreview()
    {
        var path = TxtColorRefImage.Text.Trim();
        System.Windows.Media.Imaging.BitmapImage? bmp = null;
        if (File.Exists(path))
            try
            {
                bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // 파일 잠금 방지
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelHeight = 256;
                bmp.EndInit();
                bmp.Freeze();
            }
            catch { bmp = null; }
        ImgRefPreview.Source = bmp;
        TxtRefDropHint.Visibility = bmp == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnRefBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "컬러 표지/참고 일러스트 고르기",
            Filter = "이미지|*.png;*.jpg;*.jpeg;*.webp;*.bmp|모든 파일|*.*",
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtColorRefImage.Text = dlg.FileName;
            TxtColorRefTags.Text = "";   // 표지가 바뀌면 캐시된 태그는 무효
        }
    }

    private async void BtnRefExtract_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtColorRefImage.Text.Trim();
        if (!File.Exists(path))
        {
            TxtColorRefTags.Text = "";
            System.Windows.MessageBox.Show("먼저 표지 이미지를 골라주세요.", "색 뽑기");
            return;
        }
        BtnRefExtract.IsEnabled = false;
        var old = BtnRefExtract.Content;
        BtnRefExtract.Content = "뽑는 중...";
        try
        {
            // 현재 입력값으로 뽑는다 (창을 닫기 전이라 settings엔 아직 저장 전일 수 있음)
            var s = App.Settings;
            var tags = await NekoManga.Core.Comfy.ColorRefHelper.ExtractAsync(
                s, path, new Progress<string>(m => BtnRefExtract.Content = m));
            TxtColorRefTags.Text = tags.Length > 0 ? tags : "";
            if (tags.Length == 0)
                System.Windows.MessageBox.Show("색 태그를 찾지 못했어요 — 컬러 이미지가 맞는지 확인해 주세요.", "색 뽑기");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("색 뽑기 실패: " + ex.Message, "색 뽑기");
        }
        finally
        {
            BtnRefExtract.IsEnabled = true;
            BtnRefExtract.Content = old;
        }
    }
}
