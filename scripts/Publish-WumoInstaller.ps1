param(
    [string] $CompilerPath,
    [string] $DesktopRuntimePath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/PixelThumbs.App/PixelThumbs.App.csproj'
$toolsDirectory = Join-Path $root 'artifacts/installer-tools'
$output = Join-Path $root 'dist'
$runtimeName = 'windowsdesktop-runtime-10.0.15-win-x64.exe'
$runtimeHash = 'F838960962172F1A769C49FA600E6F60F56F8D08A28E3CB878890FFC2E3A7A2E1B65697588E23EFA767B8B8E8523EC01F55E875120507DDE16D9A0869102DAB3'

if (-not $CompilerPath) {
    $compiler = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue
    if (-not $compiler) { throw 'Install Inno Setup 7.1 or later and pass the path to ISCC.exe with -CompilerPath.' }
    $CompilerPath = $compiler.Source
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path

if (-not $DesktopRuntimePath) {
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    $DesktopRuntimePath = Join-Path $toolsDirectory $runtimeName
    if (-not (Test-Path -LiteralPath $DesktopRuntimePath -PathType Leaf)) {
        Invoke-WebRequest -UseBasicParsing -TimeoutSec 300 -OutFile $DesktopRuntimePath -Uri (
            "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.15/$runtimeName")
    }
}
$DesktopRuntimePath = (Resolve-Path -LiteralPath $DesktopRuntimePath).Path
if ((Get-FileHash -LiteralPath $DesktopRuntimePath -Algorithm SHA512).Hash -ne $runtimeHash) {
    throw 'The .NET Desktop Runtime SHA-512 does not match the official Microsoft release.'
}

$builds = (New-Item -ItemType Directory -Path (Join-Path $root 'artifacts/installer-build') -Force).FullName
$package = Join-Path $builds ([Guid]::NewGuid().ToString('N'))
try {
    dotnet publish $project -c Release -o $package --nologo -warnaserror "-p:PathMap=$root=/_/Wumo"
    if ($LASTEXITCODE -ne 0) { throw 'Wumo publish failed.' }

    $binary = Get-Item -LiteralPath (Join-Path $package 'PixelThumbs.dll')
    $version = '{0}.{1}.{2}' -f $binary.VersionInfo.FileMajorPart, $binary.VersionInfo.FileMinorPart, $binary.VersionInfo.FileBuildPart
    & $CompilerPath '/Qp' "/DPackageDir=$package" "/DDesktopRuntime=$DesktopRuntimePath" "/DAppVersion=$version" "/O$output" (
        Join-Path $PSScriptRoot 'Wumo.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Wumo installer compilation failed.' }

    Get-Item -LiteralPath (Join-Path $output "Wumo-$version-Setup-x64.exe")
}
finally {
    if (Test-Path -LiteralPath $package -PathType Container) {
        $resolved = (Resolve-Path -LiteralPath $package).Path
        if ($resolved -eq $package -and [IO.Path]::GetDirectoryName($resolved) -eq $builds) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
