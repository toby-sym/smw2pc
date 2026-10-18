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
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMW2PC", "build.log");

    public static async Task<string> BuildAsync(string romPath, string outputDirectory,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The ROM builder requires 64-bit Windows.");
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

            Log("Creating portable SMW.exe...");
            var host = Path.Combine(template, "SMW.exe");
            HostWriter.CreateAppHost(Path.Combine(workspace, "apphost.exe"), host, "SMW.dll",
                windowsGraphicalUserInterface: true, assemblyToCopyResourcesFrom: playerPath);
            var bundleDirectory = Path.Combine(workspace, "bundle");
            Directory.CreateDirectory(bundleDirectory);
            var bundler = new Bundler("SMW.exe", bundleDirectory,
                BundleOptions.BundleAllContent, OSPlatform.Windows, Architecture.X64, new Version(10, 0));
            var files = Directory.GetFiles(template, "*", SearchOption.AllDirectories)
                .Select(path => new FileSpec(path, Path.GetRelativePath(template, path))).ToList();
            var executable = bundler.GenerateBundle(files);
            cancellationToken.ThrowIfCancellationRequested();
            Log("Checking player dependencies...");
            await RunCheck(executable, ["--verify-native"], Log, cancellationToken);
            Log("Verifying compiled gameplay...");
            await RunCheck(executable, ["--verify"], Log, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(outputDirectory);
            var destination = Path.Combine(Path.GetFullPath(outputDirectory), "SMW.exe");
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
            if (replacement is not null) TryDeleteFile(replacement);
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
        var compilation = CSharpCompilation.Create(name, trees, references,
            new CSharpCompilationOptions(application ? OutputKind.WindowsApplication : OutputKind.DynamicallyLinkedLibrary,
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

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
