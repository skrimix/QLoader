﻿
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using Serilog;

namespace QSideloader.Utilities;

public static class ZipUtil
{
    public static async Task ExtractArchiveAsync(string archivePath, string? extractPath = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive not found", archivePath);
        if (string.IsNullOrEmpty(extractPath))
            extractPath = Path.GetDirectoryName(archivePath);
        if (!Directory.Exists(extractPath))
            throw new DirectoryNotFoundException("Extract path directory does not exist");
        Log.Debug("Extracting archive: \"{ArchivePath}\" -> \"{ExtractPath}\"",
            archivePath, extractPath);
        using var forcefulCts = new CancellationTokenSource();
        // When the cancellation token is triggered,
        // schedule forceful cancellation as a fallback.
        await using var link = ct.Register(() =>
            // ReSharper disable once AccessToDisposedClosure
            forcefulCts.CancelAfter(TimeSpan.FromSeconds(3))
        );
        await Cli.Wrap(PathHelper.SevenZipPath)
            .WithArguments($"x \"{archivePath}\" -y -aoa -o\"{extractPath}\"")
            .ExecuteBufferedAsync(Console.OutputEncoding, Console.OutputEncoding,
                forcefulCts.Token, ct);
    }

    public static async Task<string> CreateArchiveAsync(string sourcePath, string destinationPath, string archiveName,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException("Invalid source path");
        if (!Directory.Exists(destinationPath))
            throw new DirectoryNotFoundException("Invalid destination path");
        var archivePath = Path.Combine(destinationPath, archiveName);
        Log.Debug("Creating archive: \"{SourcePath}\" -> \"{ArchivePath}\"",
            sourcePath, archivePath);
        if (File.Exists(archivePath))
            File.Delete(archivePath);
        sourcePath = Path.GetFullPath(sourcePath) + Path.DirectorySeparatorChar;
        using var forcefulCts = new CancellationTokenSource();
        // When the cancellation token is triggered,
        // schedule forceful cancellation as a fallback.
        await using var link = ct.Register(() =>
            // ReSharper disable once AccessToDisposedClosure
            forcefulCts.CancelAfter(TimeSpan.FromSeconds(3))
        );
        if (File.Exists(archivePath))
            File.Delete(archivePath);
        sourcePath = Path.GetFullPath(sourcePath) + Path.DirectorySeparatorChar;
        await Cli.Wrap(PathHelper.SevenZipPath)
            .WithArguments($"a -mx1 -aoa \"{archivePath}\" \"{sourcePath}*\"")
            .ExecuteBufferedAsync(Console.OutputEncoding, Console.OutputEncoding,
                forcefulCts.Token, ct);
        return archivePath;
    }
}