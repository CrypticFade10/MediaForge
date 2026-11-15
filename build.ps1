[CmdletBinding()]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$buildTools = Join-Path $projectRoot 'build-tools'
$sdkDirectory = Join-Path $buildTools 'dotnet'
$vendorDirectory = Join-Path $projectRoot 'vendor'
$env:DOTNET_CLI_HOME = Join-Path $buildTools 'cli-home'
$env:NUGET_PACKAGES = Join-Path $buildTools 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
New-Item -ItemType Directory -Force -Path $buildTools,$vendorDirectory | Out-Null
$dotnet = Join-Path $sdkDirectory 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $sdk = ($metadata.releases | Where-Object { $_.sdk.version -eq '10.0.401' } | Select-Object -First 1).sdk.files |
        Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
    if (-not $sdk) { throw 'Unable to locate pinned .NET SDK 10.0.401.' }
    $archive = Join-Path $buildTools 'sdk.zip'
    Invoke-WebRequest -Uri $sdk.url -OutFile $archive -UseBasicParsing
    if ((Get-FileHash -Algorithm SHA512 -LiteralPath $archive).Hash -ne $sdk.hash) { throw 'SDK checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $sdkDirectory -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $vendorDirectory 'ffmpeg.exe')) -or -not (Test-Path -LiteralPath (Join-Path $vendorDirectory 'ffprobe.exe'))) {
    $archive = Join-Path $buildTools 'ffmpeg.zip'
    $url = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip'
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    $expected = (Invoke-WebRequest ($url + '.sha256') -UseBasicParsing).Content.Trim().Split(' ')[0]
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash -ne $expected) { throw 'FFmpeg checksum mismatch.' }
    $directory = Join-Path $buildTools 'ffmpeg'
    Expand-Archive -LiteralPath $archive -DestinationPath $directory -Force
    Get-ChildItem -LiteralPath $directory -Recurse -Filter '*.exe' | Where-Object { $_.Name -in @('ffmpeg.exe','ffprobe.exe') } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $vendorDirectory }
    Get-ChildItem -LiteralPath $directory -Recurse -Filter 'LICENSE*' | Select-Object -First 1 |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $vendorDirectory 'FFmpeg-LICENSE.txt') }
}
$checksums = @{}
foreach ($name in @('ffmpeg.exe','ffprobe.exe')) { $checksums[$name] = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $vendorDirectory $name)).Hash }
$checksums | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $vendorDirectory 'tool-checksums.json') -Encoding UTF8
if (-not $SkipTests) {
    & $dotnet build (Join-Path $projectRoot 'Tests\SimilarVideoFinder.Tests.csproj') -m:1 -nr:false -p:UseSharedCompilation=false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $dotnet run --project (Join-Path $projectRoot 'Tests\SimilarVideoFinder.Tests.csproj') --no-build -- $projectRoot
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    & $dotnet run --project (Join-Path $projectRoot 'Tests\SimilarVideoFinder.Tests.csproj') --no-build -- $projectRoot --v3
    if ($LASTEXITCODE -ne 0) { throw 'V3 feature tests failed.' }
    & $dotnet run --project (Join-Path $projectRoot 'Tests\SimilarVideoFinder.Tests.csproj') --no-build -- $projectRoot --integrity
    if ($LASTEXITCODE -ne 0) { throw 'Integrity checker tests failed.' }
}
# Build PE intermediates with a neutral suffix to avoid executable locking while
# native host resources are updated. Only the final Windows EXE is distributed.
& $dotnet publish (Join-Path $projectRoot 'App\SimilarVideoFinder.App.csproj') -c Release -r win-x64 --self-contained true -m:1 -nr:false -p:UseSharedCompilation=false -p:_NativeExecutableExtension=.bin -o (Join-Path $projectRoot 'dist') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
$executable = Join-Path $projectRoot 'dist\MediaForge.exe'
Move-Item -LiteralPath (Join-Path $projectRoot 'dist\MediaForge.bin') -Destination $executable -Force
if (-not (Test-Path -LiteralPath $executable)) { throw 'Single executable was not produced.' }
$shortcutShell = New-Object -ComObject WScript.Shell
$shortcut = $shortcutShell.CreateShortcut((Join-Path $projectRoot 'MediaForge.lnk'))
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $projectRoot
$shortcut.Description = 'MediaForge - Modular Media Management Suite'
$shortcut.Save()
Get-Item -LiteralPath $executable | Select-Object FullName,Length
Get-FileHash -LiteralPath $executable -Algorithm SHA256
