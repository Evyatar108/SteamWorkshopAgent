using System.Text.Json;
using System.Runtime.InteropServices;
using SteamWorkshopAgent;
using Xunit.Abstractions;

namespace SteamWorkshopAgent.Tests;

public sealed class WorkshopMediaTests(ITestOutputHelper output)
{
    private static readonly AdditionalPreview[] Existing =
    [
        new(0, "01-diet.png", "https://example.invalid/old", 0),
        new(1, "", "youtube-id", 1),
        new(2, "obsolete.png", "https://example.invalid/remove", 0),
        new(3, "older.png", "https://example.invalid/remove-too", 0)
    ];

    private sealed class Fake : IWorkshopMediaClient
    {
        internal IReadOnlyList<AdditionalPreview> Previews = Existing;
        internal int CurrentVisibility = 2;
        internal ulong Creator = 456;
        internal ulong Item = 123;
        internal int Queries;
        internal List<string> Calls = [];
        internal bool Reject;
        internal bool Interrupt;
        public Task<MediaState> QueryAsync(ulong item)
        {
            Queries++;
            return Task.FromResult(new MediaState(Item, Creator, CurrentVisibility, Previews));
        }
        public ulong Start(ulong item) { Calls.Add("start"); return 42; }
        public bool Add(ulong h, string p) { Calls.Add("add:" + Path.GetFileName(p)); return !Reject; }
        public bool Replace(ulong h, uint i, string p) { Calls.Add("replace:" + i); return !Reject; }
        public bool Remove(ulong h, uint i) { Calls.Add("remove:" + i); return !Reject; }
        public bool Visibility(ulong h, int v) { Calls.Add("visibility:" + v); return !Reject; }
        public Task SubmitAsync(ulong h)
        {
            Calls.Add("submit");
            if (Interrupt) throw new TimeoutException("fake interruption after submit");
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root = Path.Combine(Path.GetTempPath(), "swa-media-" + Guid.NewGuid().ToString("N"));
        internal string RequestPath => Path.Combine(Root, "run", "steamworks-request.json");
        internal WorkshopPublishRequestStore Store;
        internal MediaPlan Plan;
        internal Fixture()
        {
            var folder = Path.Combine(Root, "run", "snapshot");
            Directory.CreateDirectory(folder);
            // Only signature checks are owned by uploader; full image decoding is workshop_media.py check.
            File.WriteAllBytes(Path.Combine(folder, "01-diet.png"), [137,80,78,71,13,10,26,10,0]);
            File.WriteAllBytes(Path.Combine(folder, "02-colony.jpg"), [255,216,255,0]);
            Store = new WorkshopPublishRequestStore(new ProcessRunner(), Root);
            Plan = WorkshopMediaPlan.Create(new(123, 456, 2, Existing), WorkshopMediaPlan.ReadFolder(folder));
        }
        internal Task Save(MediaPlan? plan = null, DateTimeOffset? expires = null) =>
            WorkshopPublishRequestStore.WriteOwnerOnlyJsonAsync(RequestPath,
                new MediaRequest(plan ?? Plan, expires ?? DateTimeOffset.UtcNow.AddMinutes(30)));
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }

    [Fact]
    public async Task Sample_Gallery_Plan_And_Exact_Fake_Submission()
    {
        using var f = new Fixture();
        await f.Save();
        var fake = new Fake();
        var cli = new WorkshopMediaCli(f.Store, id => Task.FromResult(f.Plan.Before),
            path => throw new InvalidOperationException("Planning must not submit"));
        var rendered = ToolJson.Ok(await cli.RunAsync(123, Path.GetDirectoryName(f.Plan.Files[0].Path), null, false, null));
        output.WriteLine(rendered);
        Assert.Equal(["replace", "add", "remove", "remove"], f.Plan.Actions.Select(a => a.Kind));
        await new WorkshopMediaService(f.Store).ExecuteAsync(f.RequestPath, fake, 456);
        Assert.Equal(["start", "replace:0", "remove:3", "remove:2", "add:02-colony.jpg", "submit"], fake.Calls);
        Assert.DoesNotContain("remove:1", fake.Calls); // Videos survive gallery reconciliation.
        Assert.True(File.Exists(PublishRequestClaim.ClaimPath(f.RequestPath)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkshopMediaService(f.Store).ExecuteAsync(f.RequestPath, fake, 456));
        Assert.Single(fake.Calls, c => c == "submit");
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("remote")]
    [InlineData("creator")]
    [InlineData("expiry")]
    [InlineData("digest")]
    [InlineData("path")]
    [InlineData("actions")]
    public async Task Changed_State_Refuses_Before_Start(string change)
    {
        using var f = new Fixture();
        var fake = new Fake();
        var plan = f.Plan;
        if (change == "bytes") File.AppendAllText(plan.Files[0].Path, "changed");
        if (change == "remote") fake.Previews = [];
        if (change == "digest") plan = plan with { Digest = "wrong" };
        if (change == "actions") plan = plan with { Actions = [new MediaAction("remove", "youtube", 1)] };
        if (change == "path") plan = plan with { Files = [plan.Files[0] with { Path = f.Root }] };
        await f.Save(plan, change == "expiry" ? DateTimeOffset.UtcNow.AddDays(-1) : null);
        await Assert.ThrowsAnyAsync<Exception>(() => new WorkshopMediaService(f.Store).ExecuteAsync(
            f.RequestPath, fake, change == "creator" ? 789ul : 456ul));
        Assert.Empty(fake.Calls);
        Assert.Equal(change == "remote" ? 1 : 0, fake.Queries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_Or_Rejected_Submission_Keeps_Claim(bool reject)
    {
        using var f = new Fixture();
        await f.Save();
        var fake = new Fake { Reject = reject, Interrupt = !reject };
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new WorkshopMediaService(f.Store).ExecuteAsync(f.RequestPath, fake, 456));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkshopMediaService(f.Store).ExecuteAsync(f.RequestPath, fake, 456));
        Assert.Equal(reject ? 0 : 1, fake.Calls.Count(c => c == "submit"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Visibility_Preserves_All_Previews_And_Content(int visibility)
    {
        using var f = new Fixture();
        var plan = WorkshopMediaPlan.Create(f.Plan.Before, [], visibility);
        await f.Save(plan);
        var fake = new Fake();
        await new WorkshopMediaService(f.Store).ExecuteAsync(f.RequestPath, fake, 456);
        Assert.Equal(["start", "visibility:" + visibility, "submit"], fake.Calls);
    }

    [Fact]
    public void Digest_Binds_Target_Visibility_Remote_Identity_And_Bytes_But_Not_Local_Paths()
    {
        using var f = new Fixture();
        MediaPlan Make(MediaState state, IReadOnlyList<MediaFile> files) => WorkshopMediaPlan.Create(state, files);
        Assert.Equal(f.Plan.Digest, Make(f.Plan.Before,
            f.Plan.Files.Select(p => p with { Path = "another-root/" + p.Name }).ToArray()).Digest);
        Assert.NotEqual(f.Plan.Digest, Make(f.Plan.Before with { ItemId = 999 }, f.Plan.Files).Digest);
        Assert.NotEqual(f.Plan.Digest, Make(f.Plan.Before with { Visibility = 0 }, f.Plan.Files).Digest);
        Assert.NotEqual(f.Plan.Digest, Make(f.Plan.Before, [f.Plan.Files[0] with { Sha256 = "different" }]).Digest);
    }

    [Fact]
    public void Duplicate_Remote_Names_And_Indexes_Refuse()
    {
        using var f = new Fixture();
        Assert.Throws<ArgumentException>(() => WorkshopMediaPlan.Create(f.Plan.Before with
            { Previews = [Existing[0], Existing[0] with { Index = 4 }] }, f.Plan.Files));
        Assert.Throws<ArgumentException>(() => WorkshopMediaPlan.Create(f.Plan.Before with
            { Previews = [Existing[0], Existing[2] with { Index = 0 }] }, f.Plan.Files));
    }

    [Theory]
    [InlineData("bad name.png")]
    [InlineData("UPPER.png")]
    [InlineData("fake.gif")]
    [InlineData("directory")]
    public void Folder_Rejects_Unexpected_Entries(string name)
    {
        using var f = new Fixture();
        var folder = Path.GetDirectoryName(f.Plan.Files[0].Path)!;
        File.WriteAllText(Path.Combine(folder, name), "invalid");
        Assert.Throws<ArgumentException>(() => WorkshopMediaPlan.ReadFolder(folder));
    }

    [Fact]
    public void Output_Key_Snapshot_And_Overhead_Bounds()
    {
        using var f = new Fixture();
        var json = ToolJson.Ok(new WorkshopMediaOutput(f.Plan.Digest, f.Plan.Actions, "Plan only"));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(["status", "data"], root.EnumerateObject().Select(p => p.Name));
        var data = root.GetProperty("data");
        Assert.Equal(["digest", "actions", "status"], data.EnumerateObject().Select(p => p.Name));
        foreach (var action in data.GetProperty("actions").EnumerateArray())
            Assert.Equal(action.GetProperty("kind").GetString() == "add"
                ? ["kind", "name"] : new[] { "kind", "name", "index" },
                action.EnumerateObject().Select(p => p.Name));
        Assert.True(json.Length < 700, json);
        // Next-step check: the output alone supplies the expected digest.
        Assert.Equal(f.Plan.Digest, data.GetProperty("digest").GetString());
        Assert.DoesNotContain("https://", json);
        Assert.DoesNotContain(f.Root.Replace("\\", "\\\\"), json);
    }

    [Fact]
    public async Task Cli_Confirm_Uses_Immutable_Snapshot_And_Refuses_Replay()
    {
        using var f = new Fixture();
        var fake = new Fake();
        var cli = new WorkshopMediaCli(f.Store, id => Task.FromResult(f.Plan.Before), async path =>
        {
            var request = JsonSerializer.Deserialize<MediaRequest>(await File.ReadAllTextAsync(path), ToolJson.Options)!;
            // Edit live sources after preparation: submission must still use the snapshot.
            File.AppendAllText(f.Plan.Files[0].Path, "edited live source");
            await new WorkshopMediaService(f.Store).ExecuteAsync(path, fake, 456);
            return new(request.Plan.Digest, request.Plan.Actions, "Submitted");
        });
        var folder = Path.GetDirectoryName(f.Plan.Files[0].Path)!;
        var plan = await cli.RunAsync(123, folder, null, false, null);
        await Assert.ThrowsAsync<ArgumentException>(() => cli.RunAsync(123, folder, null, true, "wrong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cli.RunAsync(123, folder, null, true, new string('0', 64)));
        Assert.Empty(fake.Calls);
        await cli.RunAsync(123, folder, null, true, plan.Digest);
        Assert.Single(fake.Calls, c => c == "submit");
        // Restore source identity so replay attempts use exactly the same plan.
        File.WriteAllBytes(f.Plan.Files[0].Path, [137,80,78,71,13,10,26,10,0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cli.RunAsync(123, folder, null, true, plan.Digest));
        Assert.Single(fake.Calls, c => c == "submit");
    }

    [Fact]
    public async Task Cli_NoOp_Visibility_Does_Not_Submit_And_Empty_Output_Keys_Are_Stable()
    {
        using var f = new Fixture();
        var cli = new WorkshopMediaCli(f.Store, id => Task.FromResult(f.Plan.Before),
            path => throw new InvalidOperationException("No-op must not submit"));
        var plan = await cli.RunAsync(123, null, 2, false, null);
        var result = await cli.RunAsync(123, null, 2, true, plan.Digest);
        Assert.Empty(result.Actions);
        using var json = JsonDocument.Parse(ToolJson.Ok(result));
        Assert.Equal(["digest", "actions", "status"],
            json.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name));
        Assert.True(json.RootElement.GetRawText().Length < 240);
        using var refusal = JsonDocument.Parse(ToolJson.Error(new InvalidOperationException("Read back before retry")));
        Assert.Equal(["status", "message", "details"], refusal.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.True(refusal.RootElement.GetRawText().Length < 160);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    public async Task Cli_Missing_Approval_Refuses_Before_Any_Lookup(string? digest)
    {
        using var f = new Fixture();
        var cli = new WorkshopMediaCli(f.Store,
            id => throw new InvalidOperationException("Lookup must not happen"),
            path => throw new InvalidOperationException("Submission must not happen"));
        await Assert.ThrowsAsync<ArgumentException>(() => cli.RunAsync(123, null, 0, true, digest));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 1)]
    public async Task Visibility_Changed_With_Identical_Previews_Refuses_Before_Update(int? desired, int current)
    {
        using var f = new Fixture();
        var plan = desired is null ? f.Plan : WorkshopMediaPlan.Create(f.Plan.Before, [], desired);
        await f.Save(plan);
        var fake = new Fake { CurrentVisibility = current };
        await Assert.ThrowsAsync<MediaRefusalException>(() =>
            new WorkshopMediaService(f.Store).ExecuteAsync(f.RequestPath, fake, 456));
        Assert.Equal(1, fake.Queries);
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("item")]
    [InlineData("creator")]
    public async Task Fresh_Item_And_Creator_Are_Also_Digest_Bound(string changed)
    {
        using var f = new Fixture();
        await f.Save();
        var fake = new Fake();
        if (changed == "item") fake.Item = 999;
        else fake.Creator = 999;
        await Assert.ThrowsAsync<MediaRefusalException>(() =>
            new WorkshopMediaService(f.Store).ExecuteAsync(f.RequestPath, fake, 456));
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_Details_Prefix_Reads_Fresh_State_With_Platform_Packing(bool windows)
    {
        var buffer = Marshal.AllocHGlobal(16384);
        try
        {
            Marshal.Copy(new byte[16384], 0, buffer, 16384);
            Marshal.WriteInt64(buffer, 123);
            Marshal.WriteInt32(buffer, 8, 1);
            Marshal.WriteInt32(buffer, 20, 294100);
            var ownerOffset = windows ? 8160 : 8156;
            Marshal.WriteInt64(buffer, ownerOffset, 456);
            Marshal.WriteInt32(buffer, ownerOffset + 20, 1);
            var state = SteamworksNativeClient.ReadMediaDetails(buffer, 123, Existing, windows);
            Assert.Equal(new MediaState(123, 456, 1, Existing), state);
            Marshal.WriteInt32(buffer, 20, 999);
            Assert.Throws<MediaRefusalException>(() =>
                SteamworksNativeClient.ReadMediaDetails(buffer, 123, Existing, windows));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Fact]
    public async Task Native_Helper_PreRequest_Refusal_Uses_Distinct_Exit_Code()
    {
        var result = await new ProcessRunner().RunAsync("dotnet",
            [typeof(WorkshopMediaOutput).Assembly.Location, "media-query-internal", "123",
                Path.Combine(Path.GetTempPath(), "nonexistent-media-" + Guid.NewGuid().ToString("N"))]);
        Assert.Equal(SteamRequestGate.RefusedExitCode, result.ExitCode);
        Assert.Contains("\"status\":\"error\"", result.Stderr);
        Assert.DoesNotContain(SteamRequestGate.Request, result.Stderr);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failed_Steam_Item_Result_Is_Not_An_Approval_Refusal(bool windows)
    {
        var buffer = Marshal.AllocHGlobal(16384);
        try
        {
            Marshal.Copy(new byte[16384], 0, buffer, 16384);
            Marshal.WriteInt64(buffer, 123);
            Marshal.WriteInt32(buffer, 8, 15); // AccessDenied from Steam, not a local state mismatch.
            var error = Assert.Throws<SteamNativeFailureException>(() =>
                SteamworksNativeClient.ReadMediaDetails(buffer, 123, Existing, windows));
            Assert.False(SteamRequestGate.IsLocalRefusal(error, 1));
            Assert.False(SteamRequestGate.IsLocalRefusal(error, 0));
            Marshal.WriteInt32(buffer, 8, 1); // An OK response with wrong app is a local refusal.
            var refusal = Assert.Throws<MediaRefusalException>(() =>
                SteamworksNativeClient.ReadMediaDetails(buffer, 123, Existing, windows));
            Assert.True(SteamRequestGate.IsLocalRefusal(refusal, 1));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
