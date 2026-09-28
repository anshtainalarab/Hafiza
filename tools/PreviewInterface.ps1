$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$workspace = Split-Path $PSScriptRoot
[System.Reflection.Assembly]::LoadFrom((Join-Path $workspace 'Hafiza/bin/Release/net8.0-windows/Hafiza.dll')) | Out-Null
$app = [Hafiza.App]::new()
$app.InitializeComponent()
[xml]$doc = Get-Content (Join-Path $workspace 'Hafiza/MainWindow.xaml') -Raw
$events = @('SourceInitialized','Closing','SizeChanged','PreviewKeyDown','MouseLeftButtonDown','MouseLeftButtonUp','Click','MouseRightButtonUp','PreviewMouseLeftButtonDown','PreviewMouseLeftButtonUp','PreviewMouseMove','DragOver','Drop','TextChanged','SelectionChanged')
foreach($node in $doc.SelectNodes('//*')) {
    foreach($attribute in @($node.Attributes)) {
        if($attribute.LocalName -eq 'Class' -or $events -contains $attribute.LocalName -or $attribute.LocalName -eq 'Icon') { $node.RemoveAttributeNode($attribute) | Out-Null }
    }
}
$markup = $doc.OuterXml.Replace('clr-namespace:Hafiza.Models','clr-namespace:Hafiza.Models;assembly=Hafiza').Replace('clr-namespace:Hafiza.Converters','clr-namespace:Hafiza.Converters;assembly=Hafiza')
$window = [System.Windows.Markup.XamlReader]::Parse($markup)
$window.Resources['TextPreviewHeight'] = [double]128
$text = [Hafiza.Models.ClipboardEntry]::new()
$text.Text = 'السلام عليكم .. معاك أدمن مقهى الرياضيات. تفاصيل مذكرات الجبر للصف الثاني الإعدادي.'
$folder = [Hafiza.Models.ClipboardFolder]::new()
$folder.Name = 'دروس الرياضيات'
$window.FindName('HistoryList').ItemsSource = @($text, $folder)
$window.FindName('FolderArea').Visibility = 'Collapsed'
$window.FindName('NewFolderButton').Visibility = 'Visible'
$window.Content.Measure([System.Windows.Size]::new(610,735))
$window.Content.Arrange([System.Windows.Rect]::new(0,0,610,735))
$window.Content.UpdateLayout()
$bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(610,735,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($window.Content)
$encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$output = Join-Path $workspace 'Release/interface-preview.png'
$stream = [System.IO.File]::Create($output)
try { $encoder.Save($stream) } finally { $stream.Dispose() }
Write-Output $output
