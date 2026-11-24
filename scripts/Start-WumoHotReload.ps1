param([switch] $ReducedPrivileges)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/PixelThumbs.App/PixelThumbs.App.csproj'
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $locator -PathType Leaf)) {
    throw 'Для XAML Hot Reload установите Visual Studio с компонентом «Разработка классических приложений .NET».'
}

$ide = & $locator -latest -products '*' -requires Microsoft.VisualStudio.Workload.ManagedDesktop -find 'Common7\IDE\devenv.exe' |
    Select-Object -First 1
if (-not $ide -or -not (Test-Path -LiteralPath $ide -PathType Leaf)) {
    throw 'Visual Studio с компонентом «Разработка классических приложений .NET» не найдена.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
finally { $identity.Dispose() }

if ($elevated) {
    if ($ReducedPrivileges) { throw 'Windows не предоставила обычные права для запуска Visual Studio.' }
    $engine = (Get-Process -Id $PID).Path
    $command = '"{0}" -NoProfile -File "{1}" -ReducedPrivileges' -f $engine, $PSCommandPath
    & (Join-Path $env:SystemRoot 'System32/runas.exe') '/trustlevel:0x20000' $command
    if ($LASTEXITCODE -ne 0) { throw "Не удалось запустить Visual Studio с обычными правами. Код Windows $LASTEXITCODE." }
    return
}

$profile = Join-Path $root 'artifacts/wumo-hot-reload/profile'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
$settings = Join-Path $profile 'settings.json'
if (-not (Test-Path -LiteralPath $settings -PathType Leaf)) {
    [IO.File]::WriteAllText($settings, '{"QuickPreview":false}')
}

$previousProfile = $env:PIXELTHUMBS_DATA_DIR
try {
    $env:PIXELTHUMBS_DATA_DIR = $profile
    Start-Process -FilePath $ide -WorkingDirectory $root -ArgumentList @('/Run', ('"{0}"' -f $project))
}
finally { $env:PIXELTHUMBS_DATA_DIR = $previousProfile }
