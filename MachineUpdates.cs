using System.Diagnostics;
using System.IO;
using System.Text;

namespace SuitoRide;

internal static class MachineUpdates
{
    static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public static string InstallFolder => Path.Combine(ProgramFiles, "SuitoRide");
    static string Executable => Path.Combine(InstallFolder, "SuitoRide.exe");

    internal static bool IsInstalledExecutable(string? path) =>
        path != null && string.Equals(path, Executable, StringComparison.OrdinalIgnoreCase);

    public static bool IsSharedInstallation => IsInstalledExecutable(Environment.ProcessPath);

    public static async Task StartAsync(Version version)
    {
        if (!IsSharedInstallation)
            throw new InvalidOperationException();

        using var script = typeof(MachineUpdates).Assembly.GetManifestResourceStream("SuitoRide.Assets.MachineUpdate.ps1")
            ?? throw new InvalidOperationException();
        using var reader = new StreamReader(script);
        string updateScript = await reader.ReadToEndAsync();

        // Only version and process ID are sent to the elevated process. It obtains the ZIP
        // and the expected hash itself and works inside the protected Program Files folder.
        string tag = $"v{version.ToString(3)}";
        int processId = Environment.ProcessId;
        string arguments = Encode($"$tag = '{tag}'; $parentId = {processId};\n" + updateScript);
        string powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string coordinator = $$"""
            $ErrorActionPreference = 'Stop'
            $parent = [Diagnostics.Process]::GetProcessById({{processId}})
            $null = $parent.Handle
            $powershell = {{Quote(powerShell)}}
            try {
                $worker = Start-Process -FilePath $powershell -WorkingDirectory ([Environment]::SystemDirectory) -ArgumentList @(
                    '-NoProfile', '-NonInteractive', '-EncodedCommand', '{{arguments}}'
                ) -Verb RunAs -WindowStyle Hidden -PassThru
            }
            catch {
                exit 1
            }

            [Console]::WriteLine('ready')
            [Console]::Out.Flush()
            $worker.WaitForExit()
            if (!$parent.HasExited) { exit 1 }

            if ($worker.ExitCode -ne 0) {
                Add-Type -AssemblyName PresentationFramework
                $message = if ($worker.ExitCode -eq 2) {
                    {{Quote(UiText.Get("Zavři Suito Ride i server společné jízdy ve všech účtech a spusť aktualizaci znovu."))}}
                } else {
                    {{Quote(UiText.Get("Společnou instalaci se nepodařilo aktualizovat."))}}
                }
                [System.Windows.MessageBox]::Show($message, 'Suito Ride', 'OK', 'Warning') | Out-Null
            }

            $exe = {{Quote(Executable)}}
            if ([IO.File]::Exists($exe)) {
                Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe))
            }
            """;

        await StartCoordinatorAsync(coordinator, UiText.Get("Instalaci pro všechny uživatele se nepodařilo spustit."));
    }

    internal static async Task StartCoordinatorAsync(string coordinator, string failureMessage)
    {
        string powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powerShell)
        {
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "& ([ScriptBlock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String([Console]::In.ReadToEnd()))))" })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(failureMessage);
        await process.StandardInput.WriteAsync(Encode(coordinator));
        process.StandardInput.Close();
        // The helper confirms it can finish the update after this process exits.
        if (await process.StandardOutput.ReadLineAsync() != "ready")
            throw new InvalidOperationException(failureMessage);
    }

    static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    internal static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
}
