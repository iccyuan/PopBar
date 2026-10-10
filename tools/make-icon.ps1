# Regenerates PopBar/Assets/popbar.ico from the vector logo in PopBar/UI/LogoArt.cs,
# so the exe / taskbar icon always matches the in-app logo.
# Usage: pwsh tools/make-icon.ps1   (then rebuild so the new .ico is embedded)
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$out = Join-Path $root "PopBar\Assets\popbar.ico"
$dll = Get-ChildItem (Join-Path $root "PopBar\bin") -Recurse -Filter PopBar.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { dotnet build (Join-Path $root "PopBar\PopBar.csproj") -c Release; $dll = Get-ChildItem (Join-Path $root "PopBar\bin") -Recurse -Filter PopBar.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
& dotnet $dll.FullName --export-icon $out
Write-Host "wrote $out"
