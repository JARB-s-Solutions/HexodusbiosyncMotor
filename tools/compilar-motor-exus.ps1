param(
    [string]$DotnetPath,
    [string]$InnoCompiler,
    [string]$DPUruNetDll,
    [string]$SignToolPath,
    [string]$ArtifactSigningDlib,
    [string]$ArtifactSigningMetadata
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$engineDll = Join-Path $root 'MotorLocalExus/Engine/DPUruNet.dll'

if (-not $IsWindows -and $env:OS -ne 'Windows_NT') {
    throw 'Este instalador solo se compila en Windows x64.'
}

if ($DPUruNetDll) {
    if (-not (Test-Path -LiteralPath $DPUruNetDll -PathType Leaf)) { throw "No existe la DLL del SDK: $DPUruNetDll" }
    $sourceDll = (Resolve-Path -LiteralPath $DPUruNetDll).Path
    if ($sourceDll -ne (Join-Path $root 'MotorLocalExus/Engine/DPUruNet.dll')) {
        Copy-Item -LiteralPath $sourceDll -Destination $engineDll -Force
    }
}
if (-not (Test-Path -LiteralPath $engineDll -PathType Leaf)) {
    throw 'Falta DPUruNet.dll. Obtén el SDK autorizado de HID y usa -DPUruNetDll <ruta>.'
}

if (-not $DotnetPath) {
    $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($dotnetCommand) { $DotnetPath = $dotnetCommand.Source }
    else { $DotnetPath = Join-Path $root '.tool-cache/dotnet/dotnet.exe' }
}
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { throw 'Falta el SDK .NET 8. Instálalo o usa -DotnetPath.' }

if (-not $InnoCompiler) {
    $innoCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($innoCommand) { $InnoCompiler = $innoCommand.Source }
    else {
        $innoCandidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'),
            (Join-Path $root '.tool-cache/inno/ISCC.exe')
        )
        $InnoCompiler = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Falta Inno Setup 6. Instálalo o usa -InnoCompiler <ruta a ISCC.exe>.'
}

$signingArgs = @($SignToolPath, $ArtifactSigningDlib, $ArtifactSigningMetadata) | Where-Object { $_ }
if ($signingArgs.Count -ne 0 -and $signingArgs.Count -ne 3) {
    throw 'Para firmar indica juntos -SignToolPath, -ArtifactSigningDlib y -ArtifactSigningMetadata.'
}
$signingEnabled = $signingArgs.Count -eq 3
if ($signingEnabled) {
    foreach ($path in $signingArgs) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "No existe el recurso de firma: $path" }
    }
}

function Invoke-ArtifactSigning([string]$filePath) {
    if (-not $signingEnabled) { return }
    & $SignToolPath sign /v /fd SHA256 /tr 'http://timestamp.acs.microsoft.com/' /td SHA256 /dlib $ArtifactSigningDlib /dmdf $ArtifactSigningMetadata $filePath
    if ($LASTEXITCODE -ne 0) { throw "No se pudo firmar: $filePath" }
}

$payload = Join-Path $root ('.build/payload-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $payload, $output -Force | Out-Null
Push-Location $root
try {
    foreach ($project in @(
        @{ Name = 'Service'; File = 'MotorLocalExus/Engine/MotorLocalExus.Service.csproj'; Single = $false },
        @{ Name = 'Reader'; File = 'MotorLocalExus/Reader/MotorLocalExus.Reader.csproj'; Single = $true },
        @{ Name = 'Desktop'; File = 'MotorLocalExus/Desktop/MotorLocalExus.csproj'; Single = $true }
    )) {
        $arguments = @('publish', $project.File, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $payload $project.Name), '-p:DebugType=None', '-p:DebugSymbols=false')
        if ($project.Single) { $arguments += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
        & $DotnetPath @arguments
        if ($LASTEXITCODE -ne 0) { throw "Falló la publicación de $($project.Name)." }
    }

    if ($signingEnabled) {
        Get-ChildItem -LiteralPath $payload -Recurse -File -Filter '*.exe' |
            Sort-Object FullName | ForEach-Object { Invoke-ArtifactSigning $_.FullName }
    }

    & $InnoCompiler /Qp ('/DPayload=' + $payload) ('/DOutputFolder=' + $output) 'MotorLocalExus/Installer/MotorLocalExus.iss'
    if ($LASTEXITCODE -ne 0) { throw 'Falló la creación del instalador.' }

    $installer = Join-Path $output 'HEXODUS-BioSync-Setup.exe'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Inno Setup no generó el instalador esperado.' }
    Invoke-ArtifactSigning $installer
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $output 'HEXODUS-BioSync-Setup.sha256') -Value "$hash *HEXODUS-BioSync-Setup.exe" -Encoding ascii
    Write-Host "Instalador: $installer"
    Write-Host "SHA-256: $hash"
} finally {
    Pop-Location
}
