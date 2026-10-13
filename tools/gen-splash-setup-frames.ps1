Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

# Setup.exe 설치 스플래시 프레임 생성기 (바 없음 — 하단 공간에 Velopack이 실제 진행바를 그린다)
# 사용: powershell -Sta -File gen-splash-setup-frames.ps1 <프레임출력폴더>

$outDir = $args[0]
New-Item -ItemType Directory -Force $outDir | Out-Null
$frames = 24

for ($i = 0; $i -lt $frames; $i++) {
    $t = $i / $frames
    $bobY = [Math]::Round(-5 * [Math]::Sin(2 * [Math]::PI * $t), 2)
    $op = @()
    foreach ($ph in 0.0, 0.2, 0.45, 0.65, 0.85) {
        $op += [Math]::Round(0.15 + 0.85 * (0.5 + 0.5 * [Math]::Sin(2 * [Math]::PI * ($t + $ph))), 2)
    }

    $xaml = @"
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      Width="480" Height="300" Background="#141019">
  <Border Margin="18,18,18,52" CornerRadius="14" Background="#1E1826"
          BorderBrush="#FF77A8" BorderThickness="1.5">
    <StackPanel VerticalAlignment="Center">
      <TextBlock Text="(=^&#xFF65;&#x3C9;&#xFF65;^=)&#x3065;" FontSize="40" FontFamily="Segoe UI, Malgun Gothic"
                 HorizontalAlignment="Center" Foreground="#FFB7D5">
        <TextBlock.RenderTransform><TranslateTransform Y="$bobY"/></TextBlock.RenderTransform>
      </TextBlock>
      <TextBlock Text="NekoManga 설치하는 중이에요..." FontSize="16" FontFamily="Malgun Gothic"
                 HorizontalAlignment="Center" Margin="0,16,0,0" Foreground="#F2EDF7"/>
      <TextBlock Text="☆ 만화에 마법을 부릴 준비 ☆" FontSize="12" FontFamily="Malgun Gothic"
                 HorizontalAlignment="Center" Margin="0,10,0,0" Foreground="#9A8FA8"/>
      <TextBlock Text="NekoManga" FontSize="11" FontFamily="Segoe UI"
                 HorizontalAlignment="Center" Margin="0,18,0,0" Foreground="#5F5370"/>
    </StackPanel>
  </Border>
  <Canvas>
    <TextBlock Canvas.Left="70"  Canvas.Top="62"  Text="&#x2727;" FontSize="20" Foreground="#FF77A8" Opacity="$($op[0])"/>
    <TextBlock Canvas.Left="372" Canvas.Top="52"  Text="&#x2726;" FontSize="15" Foreground="#FFB7D5" Opacity="$($op[1])"/>
    <TextBlock Canvas.Left="96"  Canvas.Top="170" Text="&#x2726;" FontSize="13" Foreground="#C5A3FF" Opacity="$($op[2])"/>
    <TextBlock Canvas.Left="356" Canvas.Top="166" Text="&#x2727;" FontSize="18" Foreground="#FF77A8" Opacity="$($op[3])"/>
    <TextBlock Canvas.Left="236" Canvas.Top="34"  Text="&#x2606;" FontSize="13" Foreground="#C5A3FF" Opacity="$($op[4])"/>
  </Canvas>
</Grid>
"@

    $element = [Windows.Markup.XamlReader]::Parse($xaml)
    $size = New-Object Windows.Size 480, 300
    $element.Measure($size)
    $element.Arrange((New-Object Windows.Rect 0, 0, 480, 300))
    $element.UpdateLayout()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap 480, 300, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($element)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $path = Join-Path $outDir ("frame{0:d2}.png" -f $i)
    $out = [IO.File]::Create($path)
    $enc.Save($out)
    $out.Close()
}
Write-Output "saved $frames frames to $outDir"
