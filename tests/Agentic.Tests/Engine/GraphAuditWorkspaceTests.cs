using Agentic.Orchestrator;

namespace Agentic.Tests.EngineTests;

public class DependencyGraphTests
{
    private static NodeSpec N(string id, params string[] deps) => new() { Id = id, Agent = "a", Deps = deps.ToList() };

    [Fact]
    public void Layers_group_independent_nodes_for_parallel_execution_with_sync_barrier()
    {
        var g = new DependencyGraph(new[] { N("a"), N("b", "a"), N("c", "a"), N("d", "b", "c") });
        g.Validate();
        var layers = g.Layers();
        Assert.Equal(3, layers.Count);
        Assert.Equal(new[] { "b", "c" }, layers[1]);
        Assert.Equal(new[] { "d" }, layers[2]);
    }

    [Fact]
    public void Cycle_is_rejected()
    {
        var g = new DependencyGraph(new[] { N("a", "c"), N("b", "a"), N("c", "b") });
        var ex = Assert.Throws<GraphException>(() => g.Validate());
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_dependency_is_rejected() =>
        Assert.Throws<GraphException>(() => new DependencyGraph(new[] { N("a", "ghost") }).Validate());

    [Fact]
    public void Duplicate_ids_are_rejected() =>
        Assert.Throws<GraphException>(() => new DependencyGraph(new[] { N("a"), N("a") }));

    [Fact]
    public void Transitive_dependents_are_found()
    {
        var g = new DependencyGraph(new[] { N("a"), N("b", "a"), N("c", "b"), N("x") });
        Assert.Equal(new[] { "b", "c" }, g.TransitiveDependents("a").OrderBy(x => x));
    }

    [Fact]
    public void Critical_path_is_the_longest_weighted_path()
    {
        var g = new DependencyGraph(new[] { N("a"), N("b", "a"), N("c", "a"), N("d", "b", "c") });
        var w = new Dictionary<string, double> { ["a"] = 1, ["b"] = 10, ["c"] = 2, ["d"] = 1 };
        var (total, path) = g.CriticalPath(id => w[id]);
        Assert.Equal(12, total);
        Assert.Equal(new[] { "a", "b", "d" }, path);
    }

    [Fact]
    public void Mermaid_contains_nodes_and_edges()
    {
        var m = new DependencyGraph(new[] { N("a"), N("b", "a") }).ToMermaid(_ => "Done");
        Assert.Contains("a --> b", m);
        Assert.Contains("Done", m);
    }
}

public class AuditLogTests
{
    [Fact]
    public void Chain_verifies_and_links_entries()
    {
        var log = new AuditLog(TimeProvider.System);
        log.Append("a", null, "engine", "one");
        log.Append("b", "n1", "agent:x", "two", new { k = 1 });
        var entries = log.Entries;
        Assert.Equal(AuditLog.Genesis, entries[0].PrevHash);
        Assert.Equal(entries[0].Hash, entries[1].PrevHash);
        Assert.True(log.Verify().Valid);
        Assert.Equal(entries[1].Hash, log.HeadHash);
    }

    [Fact]
    public void Tampering_is_detected_at_the_modified_entry()
    {
        var log = new AuditLog(TimeProvider.System);
        for (var i = 0; i < 4; i++) log.Append("t", null, "engine", $"m{i}");
        var tampered = log.Entries.ToList();
        tampered[2] = tampered[2] with { Message = "forged" };
        var (valid, broken) = AuditLog.Verify(tampered);
        Assert.False(valid);
        Assert.Equal(3, broken);
    }

    [Fact]
    public void Deleting_an_entry_breaks_the_chain()
    {
        var log = new AuditLog(TimeProvider.System);
        for (var i = 0; i < 4; i++) log.Append("t", null, "engine", $"m{i}");
        var cut = log.Entries.ToList();
        cut.RemoveAt(1);
        Assert.False(AuditLog.Verify(cut).Valid);
    }

    [Fact]
    public async Task Concurrent_appends_keep_a_valid_chain()
    {
        var log = new AuditLog(TimeProvider.System);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() => { for (var i = 0; i < 50; i++) log.Append("c", null, "x", $"{t}-{i}"); })));
        Assert.Equal(400, log.Entries.Count);
        Assert.True(log.Verify().Valid);
    }
}

public class WorkspaceManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentic-ws-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceManager _m;
    public WorkspaceManagerTests() => _m = new WorkspaceManager(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void Write_outside_the_autonomy_boundary_is_blocked()
    {
        var ws = _m.ForNode("n", new AutonomyBoundary(new[] { "docs/**" }));
        Assert.Throws<AutonomyViolationException>(() => ws.WriteText("src/x.cs", "x"));
        ws.WriteText("docs/a.md", "ok");
        Assert.True(ws.Exists("docs/a.md"));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("src/../../evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("/etc/evil")]
    public void Path_traversal_and_absolute_paths_are_blocked(string path)
    {
        var ws = _m.ForNode("n", new AutonomyBoundary(new[] { "**" }));
        Assert.Throws<AutonomyViolationException>(() => ws.WriteText(path, "x"));
    }

    [Fact]
    public void Rollback_restores_modified_and_removes_created_files()
    {
        var setup = _m.ForNode("seed", new AutonomyBoundary(new[] { "**" }));
        setup.WriteText("src/old.txt", "original");
        _m.Commit("seed");

        var ws = _m.ForNode("n", new AutonomyBoundary(new[] { "src/**" }));
        var created = ws.WriteText("src/new.txt", "new");
        var modified = ws.WriteText("src/old.txt", "changed");
        Assert.Equal("create", created.Op);
        Assert.Equal("modify", modified.Op);

        Assert.Equal(2, _m.Rollback("n"));
        Assert.False(File.Exists(Path.Combine(_root, "src/new.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(_root, "src/old.txt")));
    }

    [Fact]
    public void Writing_identical_content_is_reported_unchanged()
    {
        var ws = _m.ForNode("n", new AutonomyBoundary(new[] { "**" }));
        ws.WriteText("a.txt", "same");
        _m.Commit("n");
        var ws2 = _m.ForNode("m", new AutonomyBoundary(new[] { "**" }));
        Assert.Equal("unchanged", ws2.WriteText("a.txt", "same").Op);
    }

    [Fact]
    public void Concurrent_writes_to_the_same_file_by_different_nodes_conflict_until_commit()
    {
        var a = _m.ForNode("a", new AutonomyBoundary(new[] { "**" }));
        var b = _m.ForNode("b", new AutonomyBoundary(new[] { "**" }));
        a.WriteText("shared.txt", "a");
        Assert.Throws<WorkspaceConflictException>(() => b.WriteText("shared.txt", "b"));
        _m.Commit("a");
        b.WriteText("shared.txt", "b");
        Assert.Equal("b", File.ReadAllText(Path.Combine(_root, "shared.txt")));
    }

    [Fact]
    public void List_supports_globs_and_hides_build_output()
    {
        var ws = _m.ForNode("n", new AutonomyBoundary(new[] { "**" }));
        ws.WriteText("src/a.cs", "x"); ws.WriteText("src/sub/b.cs", "x"); ws.WriteText("src/obj/c.cs", "x"); ws.WriteText("docs/d.md", "x");
        Assert.Equal(new[] { "src/a.cs", "src/sub/b.cs" }, ws.List("src/**/*.cs"));
        Assert.DoesNotContain("src/obj/c.cs", ws.List());
    }

    [Fact]
    public void Remove_is_journaled_and_rolled_back()
    {
        var seed = _m.ForNode("seed", new AutonomyBoundary(new[] { "**" }));
        seed.WriteText("keep.txt", "data");
        _m.Commit("seed");
        var ws = _m.ForNode("n", new AutonomyBoundary(new[] { "**" }));
        ws.Remove("keep.txt");
        Assert.False(File.Exists(Path.Combine(_root, "keep.txt")));
        _m.Rollback("n");
        Assert.Equal("data", File.ReadAllText(Path.Combine(_root, "keep.txt")));
    }
}

public class PolicyEngineTests
{
    [Theory]
    [InlineData("src/UrlShortener.Api/Core/X.cs", "var k = \"AKIAIOSFODNN7EXAMPLE\";", "SEC-001", "critical")]
    [InlineData("src/UrlShortener.Api/Core/X.cs", "var r = new Random();", "SEC-004", "block")]
    [InlineData("src/UrlShortener.Api/Core/X.cs", "var c = new HttpClient();", "SEC-003", "block")]
    [InlineData("src/UrlShortener.Api/Features/Analytics/A.cs", "var ip = ctx.Connection.RemoteIpAddress;", "COMP-001", "block")]
    [InlineData("src/UrlShortener.Api/Features/Analytics/A.cs", "string ip = \"x\";", "COMP-002", "block")]
    [InlineData("src/UrlShortener.Api/Core/X.cs", "try { } catch { }", "QUAL-001", "warn")]
    public void Rules_detect_violations(string path, string content, string rule, string severity)
    {
        var f = PolicyEngine.ScanFile(path, content);
        Assert.Contains(f, x => x.Rule == rule && x.Severity == severity);
    }

    [Fact]
    public void Raw_ip_access_is_allowed_only_in_ClientKey()
    {
        Assert.DoesNotContain(PolicyEngine.ScanFile("src/UrlShortener.Api/Core/ClientKey.cs", "ctx.Connection.RemoteIpAddress"), x => x.Rule == "COMP-001");
    }

    [Fact]
    public void Clean_code_has_no_findings() =>
        Assert.Empty(PolicyEngine.ScanFile("src/UrlShortener.Api/Core/X.cs", "public sealed class X { public int Y => RandomNumberGenerator.GetInt32(5); }"));

    [Fact]
    public void Sensitive_path_detection() =>
        Assert.True(ChangeControl.IsSensitivePath("src/UrlShortener.Api/Core/UrlValidator.cs") && !ChangeControl.IsSensitivePath("docs/a.md"));
}
