using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.NET.HostModel.AppHost;
using Microsoft.NET.HostModel.Bundle;
using Smw2Pc.Core.Rom;
using Smw2Pc.Recompiler;

namespace Smw2Pc.Builder;

internal static class GameBuilder
{
    public static string ArtifactName => OperatingSystem.IsWindows() ? "SMW.exe" : "SMW";

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMW2PC", "build.log");

    public static async Task<string> BuildAsync(string romPath, string outputDirectory,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureSupportedPlatform();
        if (!new[] { ".sfc", ".smc" }.Contains(Path.GetExtension(romPath), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Select a Super Mario World .sfc or .smc ROM.", nameof(romPath));
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        using var logFile = new StreamWriter(LogPath, append: false) { AutoFlush = true };
        var logLock = new object();
        void Log(string message)
        {
            lock (logLock) logFile.WriteLine(message);
            progress?.Invoke(message);
        }

        var workspace = Path.Combine(Path.GetTempPath(), "smw2pc", "builds", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string? replacement = null;
        try
        {
            _ = SnesRom.Load(romPath);
            Directory.CreateDirectory(outputDirectory);
            var writeCheck = Path.Combine(outputDirectory, $".smw2pc-{Guid.NewGuid():N}.tmp");
            using (File.Create(writeCheck)) { }
            File.Delete(writeCheck);
            Log("Preparing build files...");
            using (var resource = typeof(GameBuilder).Assembly.GetManifestResourceStream("Smw2Pc.Builder.Payload.zip")
                ?? throw new InvalidDataException("Builder files are missing. Create the distribution with tools/package-builder.ps1."))
            using (var archive = new ZipArchive(resource, ZipArchiveMode.Read))
                archive.ExtractToDirectory(workspace);

            var generated = Path.Combine(workspace, "generated");
            RomCompilation.Generate(romPath, generated, log: Log, cancellationToken: cancellationToken);
            var template = Path.Combine(workspace, "player");
            var references = Directory.GetFiles(Path.Combine(workspace, "references"), "*.dll")
                .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
            var corePath = Path.Combine(template, "Smw2Pc.Core.dll");
            var coreVersion = AssemblyName.GetAssemblyName(corePath).Version!;
            var playerPath = Path.Combine(template, "SMW.dll");
            var playerVersion = AssemblyName.GetAssemblyName(playerPath).Version!;
            var coreSources = Directory.GetFiles(Path.Combine(workspace, "source", "core"), "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(generated, "*.cs"));
            Log("Compiling game code...");
            Compile("Smw2Pc.Core", coreVersion, coreSources, corePath, references, [], false, cancellationToken);

            Log("Compiling player...");
            var playerReferences = references.Concat(new[]
            {
                MetadataReference.CreateFromFile(corePath),
                MetadataReference.CreateFromFile(Path.Combine(template, "SDL3-CS.dll")),
            });
            var resources = new[]
            {
                Resource("Smw2Pc.Desktop.CompiledRom", romPath),
                Resource("Smw2Pc.Desktop.RecompilationProof", Path.Combine(generated, "proof.json")),
                Resource("Smw2Pc.Desktop.RecompilationManifest", Path.Combine(generated, "manifest.json")),
                Resource("Smw2Pc.Desktop.Font", Path.Combine(template, "assets", "fonts", "Inter-Variable.ttf")),
            };
            Compile("SMW", playerVersion,
                Directory.GetFiles(Path.Combine(workspace, "source", "desktop"), "*.cs", SearchOption.AllDirectories),
                playerPath, playerReferences, resources, true, cancellationToken);

            var readyToRunDirectory = Path.Combine(workspace, "ready-to-run");
            Directory.CreateDirectory(readyToRunDirectory);
            Log($"Precompiling translated game code for {RuntimeIdentifier}...");
            var coreImage = Path.Combine(readyToRunDirectory, "Smw2Pc.Core.dll");
            await CompileReadyToRun(corePath, coreImage, template, workspace, Log, cancellationToken);
            File.Move(coreImage, corePath, overwrite: true);
            var playerImage = Path.Combine(readyToRunDirectory, "SMW.dll");
            await CompileReadyToRun(playerPath, playerImage, template, workspace, Log, cancellationToken);
            File.Move(playerImage, playerPath, overwrite: true);

            Log($"Creating portable {ArtifactName}...");
            var executableName = OperatingSystem.IsWindows() ? "SMW.exe" : "SMW";
            var host = Path.Combine(template, executableName);
            HostWriter.CreateAppHost(Path.Combine(workspace, AppHostName), host, "SMW.dll",
                windowsGraphicalUserInterface: OperatingSystem.IsWindows(), assemblyToCopyResourcesFrom: playerPath);
            var bundleDirectory = Path.Combine(workspace, "bundle");
            Directory.CreateDirectory(bundleDirectory);
            var bundler = new Bundler(executableName, bundleDirectory,
                BundleOptions.BundleAllContent, TargetPlatform, RuntimeInformation.ProcessArchitecture, new Version(10, 0));
            var files = Directory.GetFiles(template, "*", SearchOption.AllDirectories)
                .Select(path => new FileSpec(path, Path.GetRelativePath(template, path))).ToList();
            var executable = bundler.GenerateBundle(files);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            cancellationToken.ThrowIfCancellationRequested();
            Log("Checking player dependencies...");
            await RunCheck(executable, ["--verify-native"], Log, cancellationToken);
            Log("Verifying compiled gameplay...");
            await RunCheck(executable, ["--verify"], Log, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(outputDirectory);
            var destination = Path.Combine(Path.GetFullPath(outputDirectory), ArtifactName);
            replacement = Path.Combine(outputDirectory, $".SMW-{Guid.NewGuid():N}.tmp");
            File.Copy(executable, replacement);
            File.Move(replacement, destination, overwrite: true);
            Log($"Created {destination}");
            return destination;
        }
        catch (Exception exception)
        {
            Log(exception.ToString());
            throw;
        }
        finally
        {
            if (replacement is not null)
            {
                TryDeleteFile(replacement);
                try { if (Directory.Exists(replacement)) Directory.Delete(replacement, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ResourceDescription Resource(string name, string path) => new(name, () => File.OpenRead(path), false);

    private static string RuntimeIdentifier => TargetOs + "-" + TargetArchitectureName;

    private static string TargetArchitectureName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("The ROM builder supports x64 and ARM64 hosts."),
    };

    private static string TargetOs => OperatingSystem.IsWindows() ? "win"
        : OperatingSystem.IsLinux() ? "linux"
        : throw new PlatformNotSupportedException("The ROM builder supports Windows and Linux.");

    private static string AppHostName => OperatingSystem.IsWindows() ? "apphost.exe" : "apphost";

    private static OSPlatform TargetPlatform => OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsLinux() ? OSPlatform.Linux
        : throw new PlatformNotSupportedException("The ROM builder supports Windows and Linux.");

    private static void EnsureSupportedPlatform()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var supported = OperatingSystem.IsWindows() && architecture == Architecture.X64
            || OperatingSystem.IsLinux() && architecture == Architecture.X64;
        if (!supported)
            throw new PlatformNotSupportedException("The ROM builder supports 64-bit Windows and Linux x64.");
    }

    private static void Compile(string name, Version version, IEnumerable<string> sourceFiles, string output,
        IEnumerable<MetadataReference> references, IEnumerable<ResourceDescription> resources,
        bool application, CancellationToken cancellationToken)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp14,
            preprocessorSymbols: ["SMW2PC_RECOMPILED", "NET", "NET10_0", "NET10_0_OR_GREATER"]);
        var trees = sourceFiles.Select(path =>
        {
            using var stream = File.OpenRead(path);
            return CSharpSyntaxTree.ParseText(SourceText.From(stream, Encoding.UTF8), parseOptions, path, cancellationToken);
        }).ToList();
        trees.Add(CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """, parseOptions));
        var informationalVersion = typeof(GameBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        trees.Add(CSharpSyntaxTree.ParseText($$"""
            [assembly: System.Reflection.AssemblyVersion("{{version}}")]
            [assembly: System.Reflection.AssemblyFileVersion("{{version}}")]
            [assembly: System.Reflection.AssemblyInformationalVersion("{{informationalVersion}}")]
            [assembly: System.Reflection.AssemblyProduct("SMW2PC")]
            """, parseOptions));
        var compilation = CSharpCompilation.Create(name, trees, references,
            new CSharpCompilationOptions(application
                    ? OperatingSystem.IsWindows() ? OutputKind.WindowsApplication : OutputKind.ConsoleApplication
                    : OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
        // Release the template DLL before emitting its ROM-specific replacement.
        using var outputStream = File.Create(output);
        using var win32 = application && OperatingSystem.IsWindows()
            ? compilation.CreateDefaultWin32Resources(true, false, null, null)
            : null;
        var result = compilation.Emit(outputStream, manifestResources: resources, win32Resources: win32, cancellationToken: cancellationToken);
        if (!result.Success)
            throw new InvalidDataException("Game compilation failed:\n" + string.Join("\n",
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(12)));
    }

    private static async Task RunCheck(string executable, string[] arguments, Action<string> log, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the compiled player.");
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line) log(line);
        }
        try
        {
            await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync(cancellationToken));
            if (process.ExitCode != 0) throw new InvalidDataException("The compiled player failed verification. See the build log.");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task CompileReadyToRun(string assemblyPath, string outputPath, string template,
        string workspace, Action<string> log, CancellationToken cancellationToken)
    {
        var compiler = Path.Combine(workspace, "crossgen2", OperatingSystem.IsWindows() ? "crossgen2.exe" : "crossgen2");
        if (!File.Exists(compiler))
            throw new FileNotFoundException("The bundled .NET ReadyToRun compiler is missing.", compiler);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(compiler);
            File.SetUnixFileMode(compiler, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        var start = new ProcessStartInfo(compiler)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = template,
        };
        start.ArgumentList.Add("--targetos");
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "windows" : "linux");
        start.ArgumentList.Add("--targetarch");
        start.ArgumentList.Add(TargetArchitectureName);
        start.ArgumentList.Add("--optimize-time");
        start.ArgumentList.Add($"-r:{Path.Combine(template, "*.dll")}");
        start.ArgumentList.Add("--out");
        start.ArgumentList.Add(outputPath);
        start.ArgumentList.Add(assemblyPath);

        using var process = Process.Start(start) ?? throw new IOException("Could not start the ReadyToRun compiler.");
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line) log(line);
        }
        try
        {
            await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync(cancellationToken));
            if (process.ExitCode != 0)
                throw new InvalidDataException("ReadyToRun compilation failed. See the build log.");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
