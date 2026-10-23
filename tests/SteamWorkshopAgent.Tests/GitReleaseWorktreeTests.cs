using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class GitReleaseWorktreeTests
{
    [Fact]
    public async Task CreateAsync_Checks_Out_Tag_And_Removes_Worktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "steam-workshop-agent-tests", Guid.NewGuid().ToString("N"));
        var repoPath = Path.Combine(root, "repo");
        var runDirectory = Path.Combine(root, "run");
        Directory.CreateDirectory(repoPath);

        try
        {
            await RunGitAsync(repoPath, "init");
            await RunGitAsync(repoPath, "config", "user.email", "test@example.com");
            await RunGitAsync(repoPath, "config", "user.name", "Test User");
            File.WriteAllText(Path.Combine(repoPath, "file.txt"), "tagged");
            await RunGitAsync(repoPath, "add", "file.txt");
            await RunGitAsync(repoPath, "commit", "-m", "initial");
            await RunGitAsync(repoPath, "tag", "v1.0.0");
            File.WriteAllText(Path.Combine(repoPath, "file.txt"), "working copy edit");

            var worktree = new GitReleaseWorktree(new ProcessRunner());
            var handle = await worktree.CreateAsync(repoPath, "v1.0.0", runDirectory);
            await using (handle)
            {
                var sourceFile = Path.Combine(handle.RepoPath, "file.txt");
                Assert.Equal("tagged", File.ReadAllText(sourceFile));

                File.WriteAllText(sourceFile, "build output");

                Assert.Equal("working copy edit", File.ReadAllText(Path.Combine(repoPath, "file.txt")));
            }

            Assert.False(Directory.Exists(Path.Combine(runDirectory, "source")));
            var list = await RunGitCaptureAsync(repoPath, "worktree", "list", "--porcelain");
            Assert.DoesNotContain(Path.Combine(runDirectory, "source"), list.Stdout);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                // Git object files are read-only on Windows.
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task RunGitAsync(string repoPath, params string[] args)
    {
        var result = await RunGitCaptureAsync(repoPath, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");
    }

    private static async Task<ProcessResult> RunGitCaptureAsync(string repoPath, params string[] args)
    {
        return await new ProcessRunner().RunAsync(
            "git",
            args,
            workingDirectory: repoPath,
            timeout: TimeSpan.FromSeconds(30));
    }
}
