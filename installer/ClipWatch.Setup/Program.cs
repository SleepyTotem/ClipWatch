using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace ClipWatch.Setup;

internal static class Program
{
    private const string PayloadResource = "payload.zip";
    private const string InstallResource = "install.ps1";
    private const string UninstallResource = "uninstall.ps1";

    private static int Main(string[] args)
    {
        var silent = HasFlag(args, "/S", "-s", "--silent", "/silent");
        var uninstall = HasFlag(args, "/uninstall", "-uninstall", "--uninstall");
        var allUsers = HasFlag(args, "/allusers", "-allusers", "--allusers");

        Console.Title = "ClipWatch Setup";

        try
        {
            if (uninstall) return Uninstall(silent);

            var existing = FindInstall();
            var asked = false;

            if (existing != null && !silent)
            {
                switch (Ask(existing))
                {
                    case Choice.Install: asked = true; break;
                    case Choice.Uninstall: return UninstallInteractive();
                    default: return 0;
                }
            }

            if (existing is { AllUsers: true }) allUsers = true;

            return Install(silent, allUsers, banner: !asked);
        }
        catch (Exception ex)
        {
            Fail(ex.Message, silent);
            return 1;
        }
    }

    private sealed record InstalledCopy(string Location, string? Version, bool AllUsers);

    private enum Choice { Install, Uninstall, Quit }

    private static InstalledCopy? FindInstall()
    {
        var candidates = new (string Path, bool AllUsers)[]
        {
            (Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "ClipWatch"), false),
            (Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "ClipWatch"), true)
        };

        foreach (var (folder, allUsers) in candidates)
        {
            var exe = Path.Combine(folder, "ClipWatch.exe");
            if (!File.Exists(exe)) continue;

            string? version = null;
            try
            {
                version = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0];
            }
            catch { }

            return new InstalledCopy(folder, version, allUsers);
        }

        return null;
    }

    private static Choice Ask(InstalledCopy existing)
    {
        Banner();

        var installed = existing.Version ?? "an unknown version";
        Console.WriteLine($"  ClipWatch {installed} is already installed.");
        Console.WriteLine($"  Location: {existing.Location}");
        if (existing.AllUsers) Console.WriteLine("  Installed for all users.");
        Console.WriteLine();

        Console.WriteLine($"    [1] {PrimaryAction(existing.Version)}");
        Console.WriteLine("    [2] Uninstall");
        Console.WriteLine("    [3] Quit");
        Console.WriteLine();

        if (Console.IsInputRedirected)
        {
            Console.WriteLine("  No input available - quitting. Use /S to reinstall or /uninstall to remove.");
            return Choice.Quit;
        }

        try { while (Console.KeyAvailable) Console.ReadKey(intercept: true); }
        catch (InvalidOperationException) { return Choice.Quit; }

        Console.Write("  Choose 1-3: ");

        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { Console.WriteLine(); return Choice.Quit; }

            Choice? choice = key.KeyChar switch
            {
                '1' => Choice.Install,
                '2' => Choice.Uninstall,
                '3' => Choice.Quit,
                _ => key.Key is ConsoleKey.Escape or ConsoleKey.Q ? Choice.Quit : null
            };

            if (choice == null) continue;

            Console.WriteLine(choice switch
            {
                Choice.Install => "1",
                Choice.Uninstall => "2",
                _ => "3"
            });
            Console.WriteLine();
            return choice.Value;
        }
    }

    private static string PrimaryAction(string? installedVersion)
    {
        var mine = Version();

        if (installedVersion == null) return $"Reinstall (version {mine})";
        if (installedVersion == mine) return $"Repair - reinstall {mine}";

        if (System.Version.TryParse(installedVersion, out var theirs) &&
            System.Version.TryParse(mine, out var ours))
        {
            if (ours > theirs) return $"Upgrade to {mine} (currently {installedVersion})";
            if (ours < theirs) return $"Downgrade to {mine} (currently {installedVersion})";
        }

        return $"Reinstall {mine} (currently {installedVersion})";
    }

    private static int UninstallInteractive()
    {
        var exit = Uninstall(silent: false, banner: false);

        Console.WriteLine();
        if (exit == 0) Ok("ClipWatch has been removed.");
        else Colour(ConsoleColor.Red, $"  Uninstall failed (exit code {exit}).");
        Console.WriteLine();
        Pause();

        return exit;
    }

    private static int Install(bool silent, bool allUsers, bool banner = true)
    {
        if (!silent && banner) Banner();

        var staging = NewStagingFolder();
        try
        {
            Write("Unpacking...", silent);

            var payload = Read(PayloadResource)
                ?? throw new InvalidOperationException(
                    "This installer was built without an app payload. Re-run build.ps1 -Installer.");

            var script = Read(InstallResource)
                ?? throw new InvalidOperationException("This installer is missing its install script.");

            var appDir = Path.Combine(staging, "app");
            Directory.CreateDirectory(appDir);

            using (var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read))
                zip.ExtractToDirectory(appDir, overwriteFiles: true);

            if (Read(UninstallResource) is { } uninstaller)
                File.WriteAllBytes(Path.Combine(appDir, "uninstall.ps1"), uninstaller);

            var scriptPath = Path.Combine(staging, "install.ps1");
            File.WriteAllBytes(scriptPath, script);
            File.WriteAllText(Path.Combine(staging, "version.txt"), Version());

            var scriptArgs = new List<string>();
            if (silent) scriptArgs.Add("-Quiet");
            if (allUsers) scriptArgs.Add("-AllUsers");

            var exit = RunPowerShell(scriptPath, scriptArgs);

            if (exit != 0)
            {
                Fail($"Install failed (exit code {exit}).", silent);
                return exit;
            }

            if (!silent)
            {
                Console.WriteLine();
                Ok("ClipWatch is installed.");
                Console.WriteLine("  Launch it from the Start Menu.");
                Console.WriteLine();
                Pause();
            }

            return 0;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static int Uninstall(bool silent, bool banner = true)
    {
        if (!silent && banner) Banner();

        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "ClipWatch", "uninstall.ps1");

        if (File.Exists(installed))
        {
            return RunPowerShell(installed,
                silent ? new List<string> { "-Quiet" } : new List<string>(),
                Path.GetTempPath());
        }

        var script = Read(UninstallResource);
        if (script == null)
        {
            Fail("ClipWatch doesn't appear to be installed.", silent);
            return 1;
        }

        var staging = NewStagingFolder();
        try
        {
            var path = Path.Combine(staging, "uninstall.ps1");
            File.WriteAllBytes(path, script);
            return RunPowerShell(path, silent ? new List<string> { "-Quiet" } : new List<string>());
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static int RunPowerShell(string scriptPath, IReadOnlyList<string> scriptArgs,
                                     string? workingDirectory = null)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(scriptPath)!
        };

        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(scriptPath);
        foreach (var arg in scriptArgs) info.ArgumentList.Add(arg);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Couldn't start PowerShell.");

        process.WaitForExit();
        return process.ExitCode;
    }

    private static byte[]? Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream == null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string NewStagingFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "ClipWatch-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch { }
    }

    private static string Version() =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.0.0";

    private static bool HasFlag(string[] args, params string[] names) =>
        args.Any(a => names.Any(n => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)));

    private static void Banner()
    {
        Console.WriteLine();
        Colour(ConsoleColor.Cyan, "  ClipWatch Setup " + Version());
        Console.WriteLine("  ----------------------");
        Console.WriteLine();
    }

    private static void Write(string message, bool silent)
    {
        if (!silent) Console.WriteLine("  " + message);
    }

    private static void Ok(string message) => Colour(ConsoleColor.Green, "  " + message);

    private static void Fail(string message, bool silent)
    {
        if (silent)
        {
            Console.Error.WriteLine(message);
            return;
        }

        Console.WriteLine();
        Colour(ConsoleColor.Red, "  " + message);
        Console.WriteLine();
        Pause();
    }

    private static void Colour(ConsoleColor colour, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    private static void Pause()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected) return;

        Console.Write("  Press any key to close...");
        try { Console.ReadKey(intercept: true); }
        catch (InvalidOperationException) { }
        Console.WriteLine();
    }
}
