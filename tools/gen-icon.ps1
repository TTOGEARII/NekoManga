Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

# 앱 아이콘 생성기: 어두운 자주빛 둥근 사각형 + 고양이 귀 + 핑크 고양이 얼굴
# 사용: powershell -Sta -File gen-icon.ps1 <출력.ico>
# 256px PNG 한 장을 PNG 압축 ICO 컨테이너로 감싼다 (Vista+ 지원 형식).

$outIco = $args[0]

$xaml = @"
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      Width="256" Height="256">
  <!-- 고양이 귀 (배경 사각형 위로 삐죽) -->
  <Polygon Points="48,70 78,18 110,62" Fill="#FF77A8"/>
  <Polygon Points="208,70 178,18 146,62" Fill="#FF77A8"/>
  <Polygon Points="58,66 79,32 101,60" Fill="#141019"/>
  <Polygon Points="198,66 177,32 155,60" Fill="#141019"/>
  <Border Width="216" Height="196" Margin="20,48,20,12" CornerRadius="44" Background="#1E1826"
          BorderBrush="#FF77A8" BorderThickness="7"/>
  <!-- 얼굴 -->
  <Canvas>
    <!-- 눈 -->
    <Ellipse Canvas.Left="78"  Canvas.Top="118" Width="22" Height="34" Fill="#FFB7D5"/>
    <Ellipse Canvas.Left="156" Canvas.Top="118" Width="22" Height="34" Fill="#FFB7D5"/>
    <!-- 코 -->
    <Polygon Points="118,164 138,164 128,176" Fill="#FF77A8"/>
    <!-- 입 (ω) -->
    <Path Stroke="#FF77A8" StrokeThickness="6" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
          Data="M 104,186 Q 116,198 128,186 Q 140,198 152,186"/>
    <!-- 수염 -->
    <Line X1="40" Y1="150" X2="70" Y2="146" Stroke="#C5A3FF" StrokeThickness="5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
    <Line X1="40" Y1="172" X2="70" Y2="172" Stroke="#C5A3FF" StrokeThickness="5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
    <Line X1="216" Y1="150" X2="186" Y2="146" Stroke="#C5A3FF" StrokeThickness="5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
    <Line X1="216" Y1="172" X2="186" Y2="172" Stroke="#C5A3FF" StrokeThickness="5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
  </Canvas>
</Grid>
"@

$element = [Windows.Markup.XamlReader]::Parse($xaml)
$size = New-Object Windows.Size 256, 256
$element.Measure($size)
$element.Arrange((New-Object Windows.Rect 0, 0, 256, 256))
$element.UpdateLayout()
$bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap 256, 256, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($element)
$enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
$ms = New-Object IO.MemoryStream
$enc.Save($ms)
$png = $ms.ToArray()

# ICO 컨테이너: ICONDIR(6) + ICONDIRENTRY(16) + PNG
$fs = [IO.File]::Create($outIco)
$bw = New-Object IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)   # reserved, type=icon, count=1
$bw.Write([Byte]0); $bw.Write([Byte]0)                             # width=256, height=256 (0 = 256)
$bw.Write([Byte]0); $bw.Write([Byte]0)                             # palette, reserved
$bw.Write([UInt16]1); $bw.Write([UInt16]32)                        # planes, bpp
$bw.Write([UInt32]$png.Length); $bw.Write([UInt32]22)              # size, offset
$bw.Write($png)
$bw.Close()
Write-Output "icon saved: $outIco ($($png.Length + 22) bytes)"
