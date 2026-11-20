$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = Join-Path $root 'app'
$exe = [IO.Path]::GetFullPath((Join-Path $output 'SuitoRide.exe'))
$running = Get-Process -Name 'SuitoRide' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [string]::Equals([IO.Path]::GetFullPath($_.Path), $exe, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw 'Nejprve zavři Suito Ride ve složce app a poté sestavení zopakuj.' }
Push-Location $root
try {
    dotnet build SuitoRide.csproj -c Release -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Sestavení aplikace selhalo.' }
    dotnet publish SpolecnaJizda.Server\SpolecnaJizda.Server.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $output 'server')
    if ($LASTEXITCODE -ne 0) { throw 'Sestavení serveru společné jízdy selhalo.' }
    Write-Host "Aplikace je připravena: $exe"
}
finally { Pop-Location }
