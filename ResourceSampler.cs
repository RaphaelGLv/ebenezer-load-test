using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using EbenezerBackend.LoadTests.Reporting;

namespace EbenezerBackend.LoadTests;

/// <summary>
/// Amostra CPU% e memória (MB) dos containers Docker informados, em segundo plano,
/// gravando um CSV simples. Complementa o relatório HTML do NBomber (latência/throughput)
/// com o custo de recursos de cada banco.
/// </summary>
public sealed class ResourceSampler : IAsyncDisposable
{
    private readonly DockerClient _client;
    private readonly string[] _containers;
    private readonly TimeSpan _interval;
    private readonly string _csvPath;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public ResourceSampler(IEnumerable<string> containers, int intervalSeconds, string csvPath)
    {
        _client = new DockerClientConfiguration().CreateClient();
        _containers = containers.ToArray();
        _interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 1, 60));
        _csvPath = csvPath;
    }

    public void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_csvPath)!);
        var header = CsvWriter.WriteRecord(new[] { "timestamp", "container", "cpu_percent", "memory_mb" });
        // Strip CRLF and use LF to preserve compatibility with existing consumers of resource-usage.csv
        File.WriteAllText(_csvPath, header.Replace("\r\n", "\n"));
        _loop = Task.Run(() => SampleLoopAsync(_cts.Token));
    }

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                foreach (var container in _containers)
                    await SampleOnceAsync(container, ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal ao parar a coleta.
        }
    }

    private async Task SampleOnceAsync(string container, CancellationToken ct)
    {
        try
        {
            ContainerStatsResponse? stats = null;
            await _client.Containers.GetContainerStatsAsync(
                container,
                new ContainerStatsParameters { Stream = false },
                new Progress<ContainerStatsResponse>(s => stats ??= s),
                ct).ConfigureAwait(false);

            if (stats is null) return;

            // Ensure the directory exists before writing (guards against races where
            // the report folder is created after the sampler starts).
            Directory.CreateDirectory(Path.GetDirectoryName(_csvPath)!);

            var line = CsvWriter.WriteRecord(new string?[]
            {
                DateTimeOffset.Now.ToString("O"),
                container,
                ComputeCpuPercent(stats).ToString("F2", CultureInfo.InvariantCulture),
                ComputeMemoryMb(stats).ToString("F2", CultureInfo.InvariantCulture)
            }).Replace("\r\n", "\n");
            await File.AppendAllTextAsync(_csvPath, line, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Falha de amostragem de um container não interrompe a coleta dos demais.
            // Guard against the case where the CSV file/directory doesn't exist yet
            // (e.g. first sample fires before Start() completes its directory creation).
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_csvPath)!);
                var errorLine = CsvWriter.WriteRecord(new string?[]
                {
                    DateTimeOffset.Now.ToString("O"),
                    container,
                    "ERROR",
                    ex.GetType().Name
                }).Replace("\r\n", "\n");
                await File.AppendAllTextAsync(_csvPath, errorLine, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Swallow: logging failures must never crash the sampler loop.
            }
        }
    }

    private static double ComputeCpuPercent(ContainerStatsResponse s)
    {
        var cpuDelta = (double)s.CPUStats.CPUUsage.TotalUsage - s.PreCPUStats.CPUUsage.TotalUsage;
        var systemDelta = (double)s.CPUStats.SystemUsage - s.PreCPUStats.SystemUsage;

        var cpus = s.CPUStats.OnlineCPUs;
        if (cpus == 0)
            cpus = (uint)(s.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);
        if (cpus == 0) cpus = 1;

        if (systemDelta <= 0 || cpuDelta <= 0) return 0;
        return cpuDelta / systemDelta * cpus * 100.0;
    }

    private static double ComputeMemoryMb(ContainerStatsResponse s)
        => s.MemoryStats.Usage / (1024.0 * 1024.0);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _client.Dispose();
    }
}
