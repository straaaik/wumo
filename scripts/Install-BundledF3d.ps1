$ErrorActionPreference = 'Stop'
$package = 'F3D-4.0.1-Windows-x86_64'
$sha256 = '7C36B7ABC2929DAFB7D93FC132354D69DBC1EA05C6BF858E7C70D75676CA56F8'
$vendor = Join-Path (Split-Path -Parent $PSScriptRoot) 'vendor'
$destination = Join-Path $vendor 'f3d'

if (Test-Path -LiteralPath $destination) {
    throw "The F3D directory already exists at $destination. Move it aside before provisioning a fresh copy."
}

$stage = Join-Path $vendor ('f3d-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $archive = Join-Path $stage "$package.zip"
    Invoke-WebRequest -Uri "https://github.com/f3d-app/f3d/releases/download/v4.0.1/$package.zip" -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sha256) {
        throw 'F3D SHA-256 mismatch.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $stage
    Move-Item -LiteralPath (Join-Path $stage $package) -Destination $destination
} finally {
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    $resolvedVendor = (Resolve-Path -LiteralPath $vendor).Path
    if ([IO.Path]::GetDirectoryName($resolvedStage) -eq $resolvedVendor) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
