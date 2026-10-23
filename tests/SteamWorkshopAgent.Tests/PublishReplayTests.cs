using System.Text.Json;
using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class PublishReplayTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("legacy-unframed")]
    [InlineData("sha256-manifest-v2")]
    public async Task Validation_Refuses_Unsupported_Digest_Format(string? format)
    {
        using var fixture = TestModRepo.CreateDeployed(includePublishedFileId: true);
        var runs = Path.Combine(fixture.Root, "runs");
        var request = await CreateRequestAsync(fixture, runs);
        var path = Path.Combine(runs, "run", "steamworks-request.json");
        File.WriteAllText(path, JsonSerializer.Serialize(
            request with { ContentDigestFormat = format }, ToolJson.Options));
        var store = new WorkshopPublishRequestStore(new ProcessRunner(), runs);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAndValidateAsync(path));
        Assert.Contains("digest format", error.Message);
        Assert.Equal(request.PublishedFileId, (await store.ReadForVerificationAsync(path)).PublishedFileId);
    }

    [Theory]
    [InlineData("submit-intent", false)]
    [InlineData("submitted", true)]
    [InlineData("ambiguous", false)]
    [InlineData("completed", true)]
    [InlineData("submit-intent-exception", false)]
    [InlineData("callback-timeout", true)]
    public async Task Validation_Refuses_Any_Previously_Submitted_Request(string stage, bool started)
    {
        using var fixture = TestModRepo.CreateDeployed(includePublishedFileId: true);
        var runs = Path.Combine(fixture.Root, "runs");
        var request = await CreateRequestAsync(fixture, runs);
        var store = new WorkshopPublishRequestStore(new ProcessRunner(), runs);
        await store.WriteResultAsync(request.ResultPath, Result(request, stage, started));
        var path = Path.Combine(runs, "run", "steamworks-request.json");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAndValidateAsync(path));
        Assert.Contains("read back", exception.Message, StringComparison.OrdinalIgnoreCase);
        var publisher = new SteamworksPublisher(new SteamEnvironment(new ProcessRunner()), new ProcessRunner(), store);
        var receipt = await File.ReadAllTextAsync(request.ResultPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishPreparedAsync(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishInCurrentProcessAsync(path, "must-not-load-native-library"));
        Assert.Equal(receipt, await File.ReadAllTextAsync(request.ResultPath));
        // Verification remains available even though submission is forbidden.
        Assert.Equal(request.PublishedFileId, (await store.ReadForVerificationAsync(path)).PublishedFileId);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task Validation_Fails_Closed_On_Unreadable_Results(string json)
    {
        using var fixture = TestModRepo.CreateDeployed(includePublishedFileId: true);
        var runs = Path.Combine(fixture.Root, "runs");
        var request = await CreateRequestAsync(fixture, runs);
        File.WriteAllText(request.ResultPath, json);
        var store = new WorkshopPublishRequestStore(new ProcessRunner(), runs);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReadAndValidateAsync(Path.Combine(runs, "run", "steamworks-request.json")));
        Assert.Contains("read back", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validation_Allows_First_Attempt_And_Proven_PreSubmit_Failure()
    {
        using var fixture = TestModRepo.CreateDeployed(includePublishedFileId: true);
        var runs = Path.Combine(fixture.Root, "runs");
        var request = await CreateRequestAsync(fixture, runs);
        var store = new WorkshopPublishRequestStore(new ProcessRunner(), runs);
        var path = Path.Combine(runs, "run", "steamworks-request.json");
        Assert.Equal(request, await store.ReadAndValidateAsync(path));
        await store.WriteResultAsync(request.ResultPath, Result(request, "steam-init", false) with
        {
            OutcomeDefinitive = true,
            FallbackAllowed = true
        });
        Assert.Equal(request, await store.ReadAndValidateAsync(path));
    }

    private static async Task<WorkshopPublishRequest> CreateRequestAsync(TestModRepo fixture, string runs)
    {
        var run = Path.Combine(runs, "run");
        // Keep run files outside the fingerprinted content.
        var content = Path.Combine(run, "snapshot", "content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "content.txt"), "Snapshot");
        var preview = Path.Combine(content, "Preview.png");
        File.Copy(fixture.PreviewPath, preview);
        Directory.CreateDirectory(run);
        var request = new WorkshopPublishRequest(
            1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30),
            AgentPaths.RimWorldAppId, 123456789, 42, content, preview, "Test", null,
            false, true, null, "Update", "not-applicable", "not-applicable",
            await WorkshopPublishRequestStore.ComputeContentDigestAsync(content),
            Path.Combine(run, "steamworks-result.json"))
        {
            PreviewDigest = PublishSnapshotPaths.FileDigest(preview),
            ContentDigestFormat = ContentFingerprint.Format
        };
        await File.WriteAllTextAsync(Path.Combine(run, "steamworks-request.json"),
            JsonSerializer.Serialize(request, ToolJson.Options));
        return request;
    }

    private static WorkshopPublishBackendResult Result(WorkshopPublishRequest request, string stage, bool started) =>
        new("steamworks-standalone", stage, false, started, false, false, false, false,
            null, null, request.PublishedFileId, null, false, null, 0, 0, 0,
            Path.Combine(Path.GetDirectoryName(request.ResultPath)!, "steamworks-request.json"),
            request.ResultPath, "", "Test receipt");
}
