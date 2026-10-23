using System.Diagnostics;
using System.Text.Json;
using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class SubmissionClaimTests
{
    [Fact]
    public async Task Two_Processes_Competing_For_One_Request_Submit_Exactly_Once()
    {
        using var fixture = TestModRepo.CreateDeployed();
        var path = await CreateRequestAsync(fixture.Root);
        var runner = new ProcessRunner();
        Task<ProcessResult> Worker(string id) => runner.RunAsync("dotnet",
            ["vstest", typeof(SubmissionClaimTests).Assembly.Location,
                "/TestCaseFilter:FullyQualifiedName=SteamWorkshopAgent.Tests.SubmissionClaimTests.Claim_Worker_Process",
                $"/Logger:trx;LogFileName=worker-{id}.trx", $"/ResultsDirectory:{fixture.Root}"],
            workingDirectory: fixture.Root,
            environment: new Dictionary<string, string>
            {
                ["SWA_CLAIM_WORKER_ROOT"] = fixture.Root,
                ["SWA_CLAIM_WORKER_ID"] = id,
                ["STEAM_WORKSHOP_AGENT_DATA_ROOT"] = fixture.Root
            }, timeout: TimeSpan.FromSeconds(45));
        var results = await Task.WhenAll(Worker("1"), Worker("2"));
        Assert.All(results, result => Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr));
        Assert.Equal(["refused", "submitted"],
            new[] { File.ReadAllText(Path.Combine(fixture.Root, "1.out")),
                    File.ReadAllText(Path.Combine(fixture.Root, "2.out")) }.Order().ToArray());
        Assert.Equal("one fake submission", File.ReadAllText(Path.Combine(fixture.Root, "submitted")));
        Assert.Contains("pid=", File.ReadAllText(PublishRequestClaim.ClaimPath(path)));
        Assert.Contains("createdAtUtc=", File.ReadAllText(PublishRequestClaim.ClaimPath(path)));
    }

    // Selected only in the two child test processes above; no product fake mode.
    [Fact]
    public async Task Claim_Worker_Process()
    {
        var root = Environment.GetEnvironmentVariable("SWA_CLAIM_WORKER_ROOT");
        if (root == null)
            return;
        var id = Environment.GetEnvironmentVariable("SWA_CLAIM_WORKER_ID")!;
        File.WriteAllText(Path.Combine(root, id + ".ready"), "");
        var stopwatch = Stopwatch.StartNew();
        while (Directory.GetFiles(root, "*.ready").Length < 2)
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), "Other worker did not reach the barrier.");
            await Task.Delay(10);
        }
        var runner = new ProcessRunner();
        var store = new WorkshopPublishRequestStore(runner, Path.Combine(root, "runs"));
        var publisher = new SteamworksPublisher(new SteamEnvironment(runner), runner, store);
        var requestPath = Path.Combine(root, "runs", "run", "steamworks-request.json");
        try
        {
            await publisher.PublishClaimedAsync(requestPath, async request =>
            {
                using (var marker = new FileStream(Path.Combine(root, "submitted"),
                           FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(marker))
                    writer.Write("one fake submission");
                await Task.Delay(100);
                var result = new WorkshopPublishBackendResult(
                    "fake", "completed", true, true, true, false, false, false,
                    null, null, request.PublishedFileId, "OK", false, null, 0, 0, 0,
                    requestPath, request.ResultPath, "", "Fake submission only.");
                await store.WriteResultAsync(request.ResultPath, result);
                return result;
            });
            File.WriteAllText(Path.Combine(root, id + ".out"), "submitted");
        }
        catch (InvalidOperationException error)
        {
            Assert.Contains("Read back", error.Message);
            File.WriteAllText(Path.Combine(root, id + ".out"), "refused");
        }
    }

    [Fact]
    public async Task Tampered_Snapshot_Is_Refused_By_Real_Helper_Before_Native_Load()
    {
        using var fixture = TestModRepo.CreateDeployed();
        var path = await CreateRequestAsync(fixture.Root);
        File.WriteAllText(Path.Combine(fixture.Root, "runs", "run", "snapshot", "content", "content.xml"),
            "<Defs><Tampered /></Defs>");
        var runner = new ProcessRunner();
        var publisher = new SteamworksPublisher(new SteamEnvironment(runner), runner,
            new WorkshopPublishRequestStore(runner, Path.Combine(fixture.Root, "runs")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishInCurrentProcessAsync(path, "must-not-load"));
        Assert.Contains("content changed", error.Message);
        Assert.True(File.Exists(PublishRequestClaim.ClaimPath(path)));
    }

    [Fact]
    public async Task Leftover_Claim_Refuses_Dispatcher_And_Helper_Before_Validation()
    {
        using var fixture = TestModRepo.CreateDeployed();
        var path = await CreateRequestAsync(fixture.Root);
        var runs = Path.Combine(fixture.Root, "runs");
        using (PublishRequestClaim.Acquire(path, runs)) { }
        File.WriteAllText(path, "invalid JSON: must not be read before the claim check");
        var runner = new ProcessRunner();
        var publisher = new SteamworksPublisher(new SteamEnvironment(runner), runner,
            new WorkshopPublishRequestStore(runner, runs));
        var parentError = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishPreparedAsync(path));
        var helperError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishInCurrentProcessAsync(path, "must-not-load"));
        Assert.Contains("Read back", parentError.Message);
        Assert.Contains("Read back", helperError.Message);
        Assert.True(File.Exists(PublishRequestClaim.ClaimPath(path)));
    }

    private static async Task<string> CreateRequestAsync(string root)
    {
        var run = Path.Combine(root, "runs", "run");
        var content = Path.Combine(run, "snapshot", "content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "content.xml"), "<Defs />");
        var preview = Path.Combine(content, "Preview.png");
        File.WriteAllBytes(preview, new byte[128]);
        var request = new WorkshopPublishRequest(1, Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), AgentPaths.RimWorldAppId,
            123456789, 42, content, preview, "Test", null, false, true, null,
            "Fake submission", "not-applicable", "not-applicable",
            await WorkshopPublishRequestStore.ComputeContentDigestAsync(content),
            Path.Combine(run, "steamworks-result.json"))
        {
            PreviewDigest = PublishSnapshotPaths.FileDigest(preview),
            ContentDigestFormat = ContentFingerprint.Format
        };
        var path = Path.Combine(run, "steamworks-request.json");
        File.WriteAllText(path, JsonSerializer.Serialize(request, ToolJson.Options));
        return path;
    }
}
