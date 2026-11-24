
$ErrorActionPreference = 'Stop'
$env:PSModulePath = Join-Path $PSHOME 'Modules'

if ($tag -cnotmatch '^v\d+\.\d+\.\d+$') { exit 1 }
$version = [version]$tag.Substring(1)
$root = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$target = Join-Path $root 'SuitoRide'
$exe = Join-Path $target 'SuitoRide.exe'
$workspaceName = '.SuitoRide-update-' + [guid]::NewGuid().ToString('N')
$workspace = Join-Path $root $workspaceName
$payload = Join-Path $workspace 'files'
$backup = Join-Path $workspace 'previous'
$lock = $null

function Get-Version($path) {
    [version]([Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion)
}

function Assert-Installation {
    if (([IO.File]::GetAttributes($target) -band [IO.FileAttributes]::ReparsePoint) -or
        ([IO.File]::GetAttributes($exe) -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Unexpected installation path.'
    }
}

function Assert-ReleaseAsset {
    param($release)
    $name = "SuitoRide-$tag-win-x64.zip"
    $url = "https://github.com/Pazout/SuitoRide/releases/download/$tag/$name"
    $assets = @($release.assets | Where-Object {
        $_.name -ceq $name -and $_.state -ceq 'uploaded' -and $_.browser_download_url -ceq $url
    })
    if ($release.tag_name -cne $tag -or $release.draft -or $release.prerelease -or $assets.Count -ne 1) {
        throw 'The stable release asset is missing.'
    }

    $asset = $assets[0]
    if ($asset.size -le 0 -or $asset.size -gt 500MB -or $asset.digest -cnotmatch '^sha256:[a-fA-F0-9]{64}$') {
        throw 'Invalid archive metadata.'
    }
    return $asset
}

function Get-Archive($asset, $path) {
    Add-Type -AssemblyName System.Net.Http
    $client = [Net.Http.HttpClient]::new()
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('SuitoRide-MachineUpdate')
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(10))
    try {
        $response = $client.GetAsync($asset.browser_download_url, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
        try {
            $response.EnsureSuccessStatusCode() | Out-Null
            $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $buffer = [byte[]]::new(128KB)
                $total = 0L
                while (($read = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                    $total += $read
                    if ($total -gt $asset.size) { throw 'The archive exceeds its declared size.' }
                    $output.Write($buffer, 0, $read)
                }
                if ($total -ne $asset.size) { throw 'The archive size does not match.' }
            }
            finally { $output.Dispose() }
        }
        finally { $response.Dispose() }
    }
    finally {
        $deadline.Dispose()
        $client.Dispose()
    }

    $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ("sha256:$digest" -ne $asset.digest) { throw 'The archive hash does not match.' }
}

function Expand-VerifiedArchive($path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $bundle = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        if ($bundle.Entries.Count -gt 10000 -or ($bundle.Entries | Measure-Object -Property Length -Sum).Sum -gt 2GB) {
            throw 'Archive exceeds extraction limit.'
        }
        [IO.Compression.ZipFileExtensions]::ExtractToDirectory($bundle, $payload)
    }
    finally { $bundle.Dispose() }
    $next = Get-Version (Join-Path $payload 'SuitoRide.exe')
    if ($next.ToString(3) -ne $version.ToString(3) -or ![IO.File]::Exists((Join-Path $payload 'map\index.html'))) {
        throw 'The archive does not contain the expected application.'
    }
}

function Wait-ForParent {
    try { $parent = [Diagnostics.Process]::GetProcessById($parentId) }
    catch [ArgumentException] { return $true }
    try {
        if ($parent.MainModule.FileName -ine $exe) { return $true }
        return $parent.WaitForExit(30000)
    }
    catch [InvalidOperationException] { return $true }
    finally { $parent.Dispose() }
}

function Test-InstallationInUse {
    foreach ($process in [Diagnostics.Process]::GetProcessesByName('SuitoRide') +
        [Diagnostics.Process]::GetProcessesByName('SpolecnaJizda.Server')) {
        try {
            if ($process.MainModule.FileName.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
        catch [InvalidOperationException] { }
        finally { $process.Dispose() }
    }
    return $false
}

function Set-Installation {
    [IO.Directory]::Move($target, $backup)
    try { [IO.Directory]::Move($payload, $target) }
    catch {
        [IO.Directory]::Move($backup, $target)
        throw
    }
}

$finished = $false
try {
    Assert-Installation
    try {
        $lock = [IO.File]::Open((Join-Path $root '.SuitoRide-update.lock'), [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] { exit 2 }

    if ([version](Get-Version $exe).ToString(3) -ge $version) { exit 0 }
    if (!(Wait-ForParent) -or (Test-InstallationInUse)) { exit 2 }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $release = Invoke-RestMethod -UseBasicParsing -TimeoutSec 30 -Headers @{
        'User-Agent' = 'SuitoRide-MachineUpdate'
        'Accept' = 'application/vnd.github+json'
    } -Uri "https://api.github.com/repos/Pazout/SuitoRide/releases/tags/$tag"
    $asset = Assert-ReleaseAsset $release
    [IO.Directory]::CreateDirectory($workspace) | Out-Null
    $zip = Join-Path $workspace 'update.zip'
    Get-Archive $asset $zip
    Expand-VerifiedArchive $zip

    if (Test-InstallationInUse) { exit 2 }
    try { Set-Installation }
    catch [IO.IOException] {
        if ([IO.File]::Exists($exe)) { exit 2 }
        throw
    }
    $finished = $true
    exit 0
}
catch { exit 1 }
finally {
    if ($null -ne $lock) { $lock.Dispose() }
    # Preserve the previous installation for recovery if rolling back the swap failed.
    $resolved = [IO.Path]::GetFullPath($workspace)
    if ([IO.Path]::GetDirectoryName($resolved) -eq $root -and [IO.Path]::GetFileName($resolved) -eq $workspaceName -and
        [IO.Directory]::Exists($resolved) -and ($finished -or ![IO.Directory]::Exists($backup))) {
        try { [IO.Directory]::Delete($resolved, $true) }
        catch [IO.IOException] { }
        catch [UnauthorizedAccessException] { }
    }
}
