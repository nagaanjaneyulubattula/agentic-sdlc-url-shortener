using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentic.Orchestrator;

/// <summary>Runs <c>dotnet test</c> as a child process confined to the workspace directory, with a hard timeout (process tree is killed).</summary>
public sealed partial class DotnetTestRunner : ITestRunner
{
    public async Task<TestReport> RunAsync(string workspaceRoot, string testProjectRelPath, string? filter, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var args = new StringBuilder($"test \"{testProjectRelPath}\" --nologo -v minimal -p:UseSharedCompilation=false");
        if (!string.IsNullOrWhiteSpace(filter)) args.Append($" --filter \"{filter}\"");
        var psi = new ProcessStartInfo("dotnet", args.ToString())
        {
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        var output = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (!timedOut) throw;
        }
        sw.Stop();

        string text;
        lock (output) text = output.ToString();
        var summary = SummaryRegex().Match(text);
        int passed = 0, failed = 0, skipped = 0, total = 0;
        if (summary.Success)
        {
            failed = int.Parse(summary.Groups["f"].Value);
            passed = int.Parse(summary.Groups["p"].Value);
            skipped = int.Parse(summary.Groups["s"].Value);
            total = int.Parse(summary.Groups["t"].Value);
        }
        var failures = FailedRegex().Matches(text).Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
        var tail = string.Join('\n', text.Split('\n').TakeLast(40));
        if (timedOut) tail = "TIMEOUT after " + timeout + "\n" + tail;
        return new TestReport(passed, failed, skipped, total, sw.ElapsedMilliseconds, timedOut ? -1 : proc.ExitCode, failures, tail);
    }

    [GeneratedRegex(@"(?:Passed|Failed)!\s+-\s+Failed:\s+(?<f>\d+),\s+Passed:\s+(?<p>\d+),\s+Skipped:\s+(?<s>\d+),\s+Total:\s+(?<t>\d+)")]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"^\s*Failed\s+([\w\.\(\)\,\s<>\[\]""'-]+?)(?:\s+\[[\d\.]+\s*\w+\])?\s*$", RegexOptions.Multiline)]
    private static partial Regex FailedRegex();
}
