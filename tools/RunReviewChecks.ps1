$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot
$audit = Join-Path $workspace 'ReviewChecks'
New-Item -ItemType Directory -Force -Path $audit | Out-Null
$source = Join-Path $workspace 'Hafiza'
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
    $relative = $file.FullName.Substring($source.Length + 1)
    $destination = Join-Path $audit $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
# Only the audit copy is changed: no listeners, registry changes, tray icon or real user storage.
$mainPath = Join-Path $audit 'MainWindow.xaml.cs'
$main = Get-Content -LiteralPath $mainPath -Raw
$start = $main.IndexOf('    public MainWindow()')
$end = $main.IndexOf('    private void FitWindowToWorkArea()', $start)
$constructor = @'
    public MainWindow()
    {
        _foregroundEventHandler = ForegroundWindowChanged;
        _state = new AppState();
        _entries = _state.Entries;
        _tabEntryOrders = _state.TabEntryOrders;
        _tabLibraryOrders = _state.TabLibraryOrders;
        _undoBatches = _state.UndoBatches;
        _folders = _state.Folders;
        Tabs = new ObservableCollection<ClipboardTab>();
        InitializeComponent();
        DataContext = this;
    }

'@
$main = $main.Substring(0, $start) + $constructor + $main.Substring($end)
Set-Content -LiteralPath $mainPath -Value $main -Encoding utf8
$storagePath = Join-Path $audit 'Services/StorageService.cs'
$storage = Get-Content -LiteralPath $storagePath -Raw
$storage = $storage.Replace('Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hafiza")', 'Environment.GetEnvironmentVariable("HAFIZA_REVIEW_DATA")!')
Set-Content -LiteralPath $storagePath -Value $storage -Encoding utf8
$projectPath = Join-Path $audit 'Hafiza.csproj'
$project = Get-Content -LiteralPath $projectPath -Raw
$project = $project.Replace('<OutputType>WinExe</OutputType>', '<OutputType>Exe</OutputType><StartupObject>ReviewProgram</StartupObject>')
Set-Content -LiteralPath $projectPath -Value $project -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReviewProgram.cs.txt') -Destination (Join-Path $audit 'ReviewProgram.cs') -Force
$env:DOTNET_CLI_HOME = Join-Path $workspace '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:HAFIZA_REVIEW_DATA = Join-Path $audit ('data-' + [guid]::NewGuid().ToString('N'))
& (Join-Path $workspace '.dotnet/dotnet.exe') run --project $projectPath -c Release
exit $LASTEXITCODE
