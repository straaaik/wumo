param([Parameter(Mandatory = $true)][string] $PackageDirectory)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Для обновления системной установки Wumo нужны права администратора.'
}
if ($env:PIXELTHUMBS_DATA_DIR) {
    throw 'Системная установка использует основной профиль Wumo.'
}

function Get-BuildRevision([string] $Directory) {
    $library = Get-Item -LiteralPath (Join-Path $Directory 'PixelThumbs.dll') -ErrorAction SilentlyContinue
    if ($library -and $library.VersionInfo.ProductName -eq 'PixelThumbs' -and
        $library.VersionInfo.ProductVersion -match '\+pt\.(\d+)') {
        return [long] $Matches[1]
    }
    return 0L
}

function Invoke-InstallationStep([string] $Executable, [string] $Step) {
    $result = Start-Process -FilePath $Executable -ArgumentList $Step -WindowStyle Hidden -Wait -PassThru
    if ($result.ExitCode -ne 0) {
        throw "Не удалось выполнить $Step для Wumo. Код $($result.ExitCode)."
    }
}

function Remove-ObsoletePackage([IO.DirectoryInfo] $Package, [string] $Root) {
    $resolved = (Resolve-Path -LiteralPath $Package.FullName).Path
    if ([IO.Path]::GetDirectoryName($resolved) -ne $Root -or
        $Package.Name -notmatch '^[0-9A-F]{24}$' -or
        ($Package.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-ChildItem -LiteralPath $resolved -Recurse -Force |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1)) {
        throw "Нельзя безопасно удалить папку сборки $resolved."
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$source = Join-Path $package 'PixelThumbs.exe'
$revision = Get-BuildRevision $package
if ($revision -le 0 -or -not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw 'Укажите папку опубликованной сборки Wumo.'
}

$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PixelThumbs'
$latest = Join-Path $data 'latest-launch.json'
$root = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'PixelThumbs'
$installLock = [Threading.Mutex]::new($false, 'Global\PixelThumbs.ManagedInstall')
$ownsLock = $false
try {
    try { $ownsLock = $installLock.WaitOne([TimeSpan]::FromSeconds(20)) }
    catch [Threading.AbandonedMutexException] { $ownsLock = $true }
    if (-not $ownsLock) { throw 'Уже идёт установка Wumo.' }

    if (Test-Path -LiteralPath $latest) {
        $known = Get-Content -LiteralPath $latest -Raw | ConvertFrom-Json
        if ($known.Revision -gt $revision) { throw 'Уже используется более новая сборка Wumo.' }
    }

    Invoke-InstallationStep $source '--stage-protected'
    $root = (Resolve-Path -LiteralPath $root).Path
    $target = @(Get-ChildItem -LiteralPath $root -Directory |
        Where-Object { $_.Name -match '^[0-9A-F]{24}$' -and (Get-BuildRevision $_.FullName) -eq $revision })
    if ($target.Count -ne 1) { throw 'Не удалось однозначно определить новую системную сборку Wumo.' }
    $installed = Join-Path $target[0].FullName 'PixelThumbs.exe'

    # Delegate the interactive launch to the desktop shell so Wumo runs at the user's integrity level.
    $desktopHandle = 0
    $shell = New-Object -ComObject Shell.Application
    $desktop = $shell.Windows().FindWindowSW(0, 0, 8, [ref] $desktopHandle, 1)
    if (-not $desktop) { throw 'Не удалось найти рабочий стол Windows для запуска Wumo.' }
    $desktop.Document.Application.ShellExecute($installed, '', $target[0].FullName, 'open', 1)

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $ready = $false
    do {
        $known = if (Test-Path -LiteralPath $latest) { Get-Content -LiteralPath $latest -Raw | ConvertFrom-Json }
        $running = Get-Process PixelThumbs -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $installed -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        $ready = $running -and $known.Directory -eq $target[0].FullName -and $known.Revision -eq $revision
        if (-not $ready) { Start-Sleep -Milliseconds 250 }
    } until ($ready -or [DateTime]::UtcNow -ge $deadline)
    if (-not $ready) { throw 'Новая сборка Wumo не подтвердила запуск. Прежняя системная копия сохранена.' }

    Invoke-InstallationStep $installed '--rebind-protected'

    $menu = [Environment]::GetFolderPath('Programs')
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $menu 'Wumo.lnk'))
    if (-not $shortcut.TargetPath -or [IO.Path]::GetFileName($shortcut.TargetPath) -eq 'PixelThumbs.exe') {
        $shortcut.TargetPath = $installed
        $shortcut.WorkingDirectory = $target[0].FullName
        $shortcut.Save()
    }

    $providers = foreach ($hive in @([Microsoft.Win32.Registry]::LocalMachine, [Microsoft.Win32.Registry]::CurrentUser)) {
        $classes = $hive.OpenSubKey('SOFTWARE\Classes\CLSID')
        if ($classes) {
            try {
                foreach ($id in $classes.GetSubKeyNames().Where({ $_ -like '{CDB49CA7-2710-4EEA-94E0-D9F361B8*' })) {
                    $server = $classes.OpenSubKey($id + '\InprocServer32')
                    if ($server) { try { $server.GetValue('') } finally { $server.Dispose() } }
                }
            }
            finally { $classes.Dispose() }
        }
    }

    $retained = @()
    foreach ($old in Get-ChildItem -LiteralPath $root -Directory) {
        if ($old.FullName -eq $target[0].FullName -or $old.Name -notmatch '^[0-9A-F]{24}$' -or
            (Get-BuildRevision $old.FullName) -gt $revision) { continue }
        $oldExecutable = Join-Path $old.FullName 'PixelThumbs.exe'
        $oldLibrary = Get-Item -LiteralPath (Join-Path $old.FullName 'PixelThumbs.dll') -ErrorAction SilentlyContinue
        if (-not $oldLibrary -or $oldLibrary.VersionInfo.ProductName -ne 'PixelThumbs') { continue }
        if (($providers | Where-Object { $_ -is [string] -and [IO.Path]::GetDirectoryName($_) -eq $old.FullName }) -or
            (Get-Process PixelThumbs -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $oldExecutable })) {
            $retained += $old.FullName
            continue
        }
        try { Remove-ObsoletePackage $old $root }
        catch { $retained += $old.FullName; Write-Warning $_.Exception.Message }
    }
    if ($retained.Count -gt 0) {
        throw ('Остались занятые системные папки Wumo: ' + ($retained -join ', '))
    }
    Get-Item -LiteralPath $installed
}
finally {
    if ($ownsLock) { $installLock.ReleaseMutex() }
    $installLock.Dispose()
}
