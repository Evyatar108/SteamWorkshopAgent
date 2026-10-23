using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class WorkshopPublishRequestStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "steam-workshop-agent-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentDigest_Frames_Paths_And_File_Contents(bool twoFileCollision)
    {
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        if (twoFileCollision)
        {
            File.WriteAllText(Path.Combine(left, "a.bin"), "X");
            File.WriteAllText(Path.Combine(left, "b.bin"), "Yb.bin\0Z");
            File.WriteAllText(Path.Combine(right, "a.bin"), "Xb.bin\0Y");
            File.WriteAllText(Path.Combine(right, "b.bin"), "Z");
        }
        else
        {
            File.WriteAllText(Path.Combine(left, "a"), "xb\0y");
            File.WriteAllText(Path.Combine(right, "a"), "x");
            File.WriteAllText(Path.Combine(right, "b"), "y");
        }
        Assert.NotEqual(await WorkshopPublishRequestStore.ComputeContentDigestAsync(left),
            await WorkshopPublishRequestStore.ComputeContentDigestAsync(right));
    }

    [Fact]
    public async Task ContentDigest_Is_Stable_For_Identical_Trees_And_Matches_Manifest_Vector()
    {
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.WriteAllText(Path.Combine(left, "a"), "x");
        File.WriteAllText(Path.Combine(left, "b"), "y");
        File.WriteAllText(Path.Combine(right, "b"), "y");
        File.WriteAllText(Path.Combine(right, "a"), "x");
        var digest = await WorkshopPublishRequestStore.ComputeContentDigestAsync(left);
        Assert.Equal("3a52c679627cde02b8403b7d68a55199f68b5fda065057ec879ff241b0e15c8e", digest);
        Assert.Equal(digest, await WorkshopPublishRequestStore.ComputeContentDigestAsync(right));
    }

    [Fact]
    public async Task ContentDigest_Is_Deterministic_And_Path_Sensitive()
    {
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "two");
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "a.txt"), "one");

        var first = await WorkshopPublishRequestStore.ComputeContentDigestAsync(root);
        var second = await WorkshopPublishRequestStore.ComputeContentDigestAsync(root);

        Assert.Equal(first, second);

        File.Move(Path.Combine(root, "b.txt"), Path.Combine(root, "renamed.txt"));
        var renamed = await WorkshopPublishRequestStore.ComputeContentDigestAsync(root);
        Assert.NotEqual(first, renamed);
    }

    [Fact]
    public async Task ContentDigest_Changes_When_File_Content_Changes()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "content.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var before = await WorkshopPublishRequestStore.ComputeContentDigestAsync(root);

        await File.WriteAllBytesAsync(path, [1, 2, 4]);
        var after = await WorkshopPublishRequestStore.ComputeContentDigestAsync(root);

        Assert.NotEqual(before, after);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
