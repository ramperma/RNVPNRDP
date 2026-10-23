using System.Diagnostics;

namespace WgRdp.Util;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public string Output
    {
        get
        {
            var stdout = StdOut.Trim();
            var stderr = StdErr.Trim();
            if (stdout.Length == 0)
                return stderr;
            if (stderr.Length == 0)
                return stdout;
            return stdout + Environment.NewLine + stderr;
        }
    }
}

/// <summary>Ejecuta procesos externos capturando salida, sin pasar por el shell.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        string? stdin = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", $"No se pudo lanzar '{fileName}': {ex.Message}");
        }

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(effectiveTimeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ya murió */ }
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { /* tubería cerrada */ }
            if (ct.IsCancellationRequested)
                throw;
            return new ProcessResult(-2, "", $"Timeout tras {effectiveTimeout.TotalSeconds:0}s: {fileName}");
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
