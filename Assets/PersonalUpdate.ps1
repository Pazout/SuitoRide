$ErrorActionPreference = 'Stop'
$env:PSModulePath = Join-Path $PSHOME 'Modules'
$exe = Join-Path $target 'SuitoRide.exe'
$payload = Join-Path $workspace 'files'
$backup = Join-Path $workspace 'previous'
$lock = $null
$parent = $null
$parentExited = $false
$script:keepBackup = $false
$failed = $false
$cleanWorkspace = $false

function Assert-RegularPath([string]$path) {
    if ((Test-Path -LiteralPath $path) -and
        ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Unexpected redirection in the installation.'
    }
}

function Test-InstallationInUse([switch]$ApplicationOnly) {
    $processes = [Diagnostics.Process]::GetProcessesByName('SuitoRide')
    if (!$ApplicationOnly) { $processes += [Diagnostics.Process]::GetProcessesByName('SpolecnaJizda.Server') }
    foreach ($process in $processes) {
        try {
            $image = $process.MainModule.FileName
            if ($image -and $image.StartsWith($target.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
        catch [InvalidOperationException] { }
        catch [ComponentModel.Win32Exception] { }
        finally { $process.Dispose() }
    }
    return $false
}

function Set-Installation {
    $operations = [Collections.Generic.List[object]]::new()
    $directories = [Collections.Generic.List[string]]::new()
    try {
        foreach ($file in ([IO.Directory]::GetFiles($payload, '*', [IO.SearchOption]::AllDirectories) | Sort-Object)) {
            $relative = $file.Substring($payload.Length + 1)
            $destination = Join-Path $target $relative
            $previous = Join-Path $backup $relative
            $folder = $target
            $parts = $relative.Split('\')
            for ($i = 0; $i -lt $parts.Length - 1; $i++) {
                $folder = Join-Path $folder $parts[$i]
                Assert-RegularPath $folder
                if (![IO.Directory]::Exists($folder)) {
                    [IO.Directory]::CreateDirectory($folder) | Out-Null
                    $directories.Add($folder)
                }
            }
            Assert-RegularPath $destination
            $operation = [pscustomobject]@{ Path=$destination; Backup=$previous; HadOld=$false; Installed=$false }
            $operations.Add($operation)
            if ([IO.File]::Exists($destination)) {
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($previous)) | Out-Null
                [IO.File]::Move($destination, $previous)
                $operation.HadOld = $true
            }
            [IO.File]::Move($file, $destination)
            $operation.Installed = $true
        }
    }
    catch {
        for ($i = $operations.Count - 1; $i -ge 0; $i--) {
            $operation = $operations[$i]
            try {
                if ($operation.Installed) { [IO.File]::Delete($operation.Path) }
                if ($operation.HadOld) { [IO.File]::Move($operation.Backup, $operation.Path) }
            }
            catch { $script:keepBackup = $true }
        }
        for ($i = $directories.Count - 1; $i -ge 0; $i--) {
            try { [IO.Directory]::Delete($directories[$i], $false) } catch [IO.IOException], [UnauthorizedAccessException] { }
        }
        throw
    }
}

try {
    if ([IO.Path]::GetDirectoryName($workspace) -ine $target -or
        [IO.Path]::GetFileName($workspace) -cnotmatch '^\.suitoride-update-[a-f0-9]{32}$') { exit 1 }
    Assert-RegularPath $target
    Assert-RegularPath $workspace
    Assert-RegularPath $payload
    Assert-RegularPath $exe
    $cleanWorkspace = $true
    $lockPath = Join-Path $target '.suitoride-update.lock'
    Assert-RegularPath $lockPath
    $lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
    $parent = [Diagnostics.Process]::GetProcessById($parentId)
    $null = $parent.Handle
    if ($parent.MainModule.FileName -ine $exe) { exit 1 }

    [Console]::WriteLine('ready')
    [Console]::Out.Flush()
    if (!$parent.WaitForExit(30000)) { exit 1 }
    $parentExited = $true
    if (Test-InstallationInUse) { throw 'Installation in use.' }

    $current = [version]([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion)
    if ([version]$current.ToString(3) -lt [version]$version) {
        Set-Installation
    }
}
catch { $failed = $true }
finally {
    if ($null -ne $parent) { $parent.Dispose() }
    if ($null -ne $lock) {
        $lock.Dispose()
    }
    if (!$script:keepBackup -and $cleanWorkspace) {
        try { [IO.Directory]::Delete($workspace, $true) } catch [IO.IOException] { } catch [UnauthorizedAccessException] { }
    }
}
if (!$parentExited) { exit 1 }
if ($failed) {
    Add-Type -AssemblyName PresentationFramework
    if ($script:keepBackup) { $failureMessage += "`n$backup" }
    [System.Windows.MessageBox]::Show($failureMessage, 'Suito Ride', 'OK', 'Warning') | Out-Null
}
if (!$script:keepBackup -and [IO.File]::Exists($exe) -and !(Test-InstallationInUse -ApplicationOnly)) {
    Start-Process -FilePath $exe -WorkingDirectory $target
}
if ($failed) { exit 1 }
