param([string] $DataDirectory)

$ErrorActionPreference = 'Stop'
if (-not $DataDirectory) {
    $DataDirectory = $env:PIXELTHUMBS_DATA_DIR
}
$isolatedProfile = [bool] $DataDirectory
if (-not $DataDirectory) {
    $DataDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PixelThumbs'
}

$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PixelThumbs.App/PixelThumbs.App.csproj'
$updates = Join-Path ([IO.Path]::GetFullPath($DataDirectory)) 'updates'
New-Item -ItemType Directory -Path $updates -Force | Out-Null
$updates = (Resolve-Path -LiteralPath $updates).Path
$revision = [DateTime]::UtcNow.Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
$stage = Join-Path $updates ('.incoming-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $updates $revision

try {
    dotnet publish $project -c Release -o $stage "-p:PixelThumbsBuildId=$revision"
    if ($LASTEXITCODE -ne 0) {
        throw 'Не удалось собрать обновление Wumo.'
    }
    if ($isolatedProfile) {
        # Publish the version directory only after the entire package has been written.
        [IO.Directory]::Move($stage, $destination)
        Get-Item -LiteralPath $destination
    }
    else {
        $installer = Join-Path $PSScriptRoot 'Install-WumoRelease.ps1'
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            & $installer -PackageDirectory $stage
        }
        else {
            $engine = (Get-Process -Id $PID).Path
            $arguments = '-NoProfile -File "{0}" -PackageDirectory "{1}"' -f $installer, $stage
            $installation = Start-Process -FilePath $engine -Verb RunAs -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
            if ($installation.ExitCode -ne 0) {
                throw "Не удалось завершить установку Wumo. Код $($installation.ExitCode)."
            }
        }
        dotnet clean $project -c Release --nologo
        if ($LASTEXITCODE -ne 0) {
            throw 'Wumo обновлён, но не удалось очистить временные выходные файлы сборки.'
        }
    }
}
finally {
    if (Test-Path -LiteralPath $stage -PathType Container) {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if ($resolvedStage -eq $stage -and [IO.Path]::GetDirectoryName($resolvedStage) -eq $updates) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}
