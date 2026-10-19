using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMW2PC", "build.log");

    public static async Task<string> BuildAsync(string romPath, string outputDirectory,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var target = GetTarget();
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
            if (OperatingSystem.IsMacOS())
            {
                var crossgenCompiler = Path.Combine(workspace, "crossgen2", "crossgen2");
                File.SetUnixFileMode(crossgenCompiler,
                    File.GetUnixFileMode(crossgenCompiler) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

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
            Log($"Precompiling translated game code for {target.DisplayName}...");
            var coreImage = Path.Combine(readyToRunDirectory, "Smw2Pc.Core.dll");
            await CompileReadyToRun(corePath, coreImage, template, workspace, target, Log, cancellationToken);
            File.Move(coreImage, corePath, overwrite: true);
            var playerImage = Path.Combine(readyToRunDirectory, "SMW.dll");
            await CompileReadyToRun(playerPath, playerImage, template, workspace, target, Log, cancellationToken);
            File.Move(playerImage, playerPath, overwrite: true);

            Log($"Creating portable {target.OutputName}...");
            var appHostName = target.IsWindows ? "apphost.exe" : "apphost";
            var host = Path.Combine(template, target.ExecutableName);
            HostWriter.CreateAppHost(Path.Combine(workspace, appHostName), host, "SMW.dll",
                windowsGraphicalUserInterface: target.IsWindows, assemblyToCopyResourcesFrom: playerPath);
            if (OperatingSystem.IsMacOS())
                File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var bundleDirectory = Path.Combine(workspace, "bundle");
            Directory.CreateDirectory(bundleDirectory);
            var bundler = new Bundler(target.ExecutableName, bundleDirectory,
                BundleOptions.BundleAllContent, target.BundleOS, target.Architecture, target.MinimumOSVersion);
            var files = Directory.GetFiles(template, "*", SearchOption.AllDirectories)
                .Select(path => new FileSpec(path, Path.GetRelativePath(template, path))).ToList();
            var executable = bundler.GenerateBundle(files);
            if (target.IsMacOS && OperatingSystem.IsMacOS())
            {
                var appBundle = Path.Combine(workspace, "SMW.app");
                CreateMacAppBundle(executable, appBundle, "SMW", "SMW2PC", "com.smw2pc.game", playerVersion);
                executable = Path.Combine(appBundle, "Contents", "MacOS", "SMW");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Log("Checking player dependencies...");
            await RunCheck(executable, ["--verify-native"], Log, cancellationToken);
            Log("Verifying compiled gameplay...");
            await RunCheck(executable, ["--verify"], Log, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(outputDirectory);
            var destination = Path.Combine(Path.GetFullPath(outputDirectory), target.OutputName);
            if (target.IsMacOS && OperatingSystem.IsMacOS())
            {
                replacement = Path.Combine(Path.GetFullPath(outputDirectory), $".SMW.app-{Guid.NewGuid():N}.tmp");
                Directory.CreateDirectory(replacement);
                CopyDirectory(Path.Combine(workspace, "SMW.app"), replacement);
                ReplaceDirectory(replacement, destination);
                replacement = null;
            }
            else
            {
                replacement = Path.Combine(outputDirectory, $".SMW-{Guid.NewGuid():N}.tmp");
                File.Copy(executable, replacement);
                File.Move(replacement, destination, overwrite: true);
                replacement = null;
            }
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
            if (replacement is not null) TryDelete(replacement);
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ResourceDescription Resource(string name, string path) => new(name, () => File.OpenRead(path), false);

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
        var outputKind = application
            ? OperatingSystem.IsWindows() ? OutputKind.WindowsApplication : OutputKind.ConsoleApplication
            : OutputKind.DynamicallyLinkedLibrary;
        var compilation = CSharpCompilation.Create(name, trees, references,
            new CSharpCompilationOptions(outputKind,
                optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
        // Release the template DLL before emitting its ROM-specific replacement.
        using var outputStream = File.Create(output);
        using var win32 = application ? compilation.CreateDefaultWin32Resources(true, false, null, null) : null;
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
        string workspace, BuildTarget target, Action<string> log, CancellationToken cancellationToken)
    {
        var compiler = Path.Combine(workspace, "crossgen2", OperatingSystem.IsWindows() ? "crossgen2.exe" : "crossgen2");
        if (!File.Exists(compiler))
            throw new FileNotFoundException("The bundled .NET ReadyToRun compiler is missing.", compiler);

        var start = new ProcessStartInfo(compiler)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = template,
        };
        start.ArgumentList.Add("--targetos");
        start.ArgumentList.Add(target.CrossgenOS);
        start.ArgumentList.Add("--targetarch");
        start.ArgumentList.Add(target.CrossgenArchitecture);
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

    private sealed record BuildTarget(string RuntimeIdentifier, string CrossgenOS, string CrossgenArchitecture,
        Architecture Architecture, OSPlatform BundleOS, Version MinimumOSVersion, bool IsWindows, bool IsMacOS,
        string ExecutableName, string OutputName, string DisplayName);

    private static BuildTarget GetTarget()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return new("win-x64", "windows", "x64", Architecture.X64, OSPlatform.Windows,
                new Version(10, 0), true, false, "SMW.exe", "SMW.exe", "Windows x64");
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
        {
            var architecture = RuntimeInformation.ProcessArchitecture;
            return new($"osx-{(architecture == Architecture.Arm64 ? "arm64" : "x64")}", "osx",
                architecture == Architecture.Arm64 ? "arm64" : "x64", architecture, OSPlatform.OSX,
                new Version(13, 0), false, true, "SMW", "SMW.app", $"macOS {architecture}");
        }
        throw new PlatformNotSupportedException("The ROM builder supports Windows x64 and macOS x64/arm64.");
    }

    [SupportedOSPlatform("macos")]
    private static void CreateMacAppBundle(string executable, string appBundle, string executableName,
        string bundleName, string bundleIdentifier, Version version)
    {
        var macOSDirectory = Path.Combine(appBundle, "Contents", "MacOS");
        Directory.CreateDirectory(macOSDirectory);
        var appExecutable = Path.Combine(macOSDirectory, executableName);
        File.Copy(executable, appExecutable);
        File.SetUnixFileMode(appExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var bundleVersion = version.ToString(3);
        var plist = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleExecutable</key><string>{{executableName}}</string>
              <key>CFBundleIdentifier</key><string>{{bundleIdentifier}}</string>
              <key>CFBundleName</key><string>{{bundleName}}</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleShortVersionString</key><string>{{bundleVersion}}</string>
              <key>CFBundleVersion</key><string>{{bundleVersion}}</string>
              <key>NSHighResolutionCapable</key><true/>
            </dict>
            </plist>
            """;
        File.WriteAllText(Path.Combine(appBundle, "Contents", "Info.plist"), plist, new UTF8Encoding(false));
    }

    [SupportedOSPlatform("macos")]
    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }

    private static void ReplaceDirectory(string replacement, string destination)
    {
        var backup = destination + $".{Guid.NewGuid():N}.bak";
        var hadPrevious = Directory.Exists(destination);
        if (hadPrevious) Directory.Move(destination, backup);
        try { Directory.Move(replacement, destination); }
        catch
        {
            if (hadPrevious && !Directory.Exists(destination)) Directory.Move(backup, destination);
            throw;
        }
        if (hadPrevious) Directory.Delete(backup, recursive: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
