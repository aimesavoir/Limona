param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectFile = Join-Path $projectRoot 'src\Limona\Limona.csproj'
$buildArtifactsDirectory = Join-Path $env:LOCALAPPDATA 'Limona\BuildArtifacts'
$publishDirectory = Join-Path $buildArtifactsDirectory 'publish'
$installerScript = Join-Path $PSScriptRoot 'Limona.iss'
$project = [xml](Get-Content -LiteralPath $projectFile -Raw)
$appVersion = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts\setup'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
    --artifacts-path $buildArtifactsDirectory `
    -p:Platform=x64 `
    -p:EmitCompilerGeneratedFiles=false `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($compiler) {
    $compilerPath = $compiler.Source
} else {
    $compilerCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $compilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if (-not $compilerPath) {
    throw 'Inno Setup 6 compiler (ISCC.exe) was not found. Install it from https://jrsoftware.org/isinfo.php and retry.'
}

& $compilerPath "/DAppVersion=$appVersion" "/DPublishDir=$publishDirectory" "/O$OutputDirectory" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $OutputDirectory "Limona-Setup-$appVersion.exe"
Write-Host "Installer created: $installerPath"
