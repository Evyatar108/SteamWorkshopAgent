using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class WorkshopDescriptionReaderTests
{
    [Fact]
    public async Task GetDescriptionAsync_Uses_Command_And_Appends_Item_Id()
    {
        var calls = 0;
        var reader = new WorkshopDescriptionReader(
            new WorkshopTargetResolver(new ModInspector(new ProcessRunner())),
            () => """["fake details","an argument with spaces","details"]""",
            (file, arguments, timeout) =>
            {
                calls++;
                Assert.Equal("fake details", file);
                Assert.Equal(["an argument with spaces", "details", "123456789"], arguments);
                Assert.True(timeout >= TimeSpan.FromMinutes(20));
                return Task.FromResult(Result(0,
                    """{"response":{"publishedfiledetails":[{"result":1,"title":"From command","creator":"42"}]}}"""));
            });

        var snapshot = await reader.GetDescriptionAsync("123456789");

        Assert.Equal("From command", snapshot.Title);
        Assert.Equal("42", snapshot.Creator);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetDescriptionAsync_Command_Failure_Is_Not_Retried(bool timedOut)
    {
        var calls = 0;
        var reader = new WorkshopDescriptionReader(
            new WorkshopTargetResolver(new ModInspector(new ProcessRunner())),
            () => """["fake"]""",
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult(Result(17, "", "throttle cooldown", timedOut));
            });

        var error = await Record.ExceptionAsync(() => reader.GetDescriptionAsync("123456789"));

        if (timedOut)
            Assert.IsType<TimeoutException>(error);
        else
            Assert.Contains("throttle cooldown", Assert.IsType<InvalidOperationException>(error).Message);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[\" \"]")]
    [InlineData("[\"tool\", 42]")]
    [InlineData("{}")]
    public void ParseDetailsCommand_Rejects_Invalid_Configuration(string json)
    {
        Assert.Throws<ArgumentException>(() => WorkshopDescriptionReader.ParseDetailsCommand(json));
    }

    private static ProcessResult Result(int exit, string stdout, string stderr = "", bool timedOut = false)
        => new(exit, stdout, stderr, 0, timedOut, false, false, stdout.Length, stderr.Length);

    [Fact]
    public void ParseSnapshot_ReturnsCurrentDescriptionFields()
    {
        var target = new WorkshopDescriptionTarget(928376710, "/tmp/Zombieland", "Zombieland");

        var snapshot = WorkshopDescriptionReader.ParseSnapshot(
            """
            {
              "response": {
                "publishedfiledetails": [
                  {
                    "result": 1,
                    "publishedfileid": "928376710",
                    "creator": "76561197961067423",
                    "consumer_app_id": 294100,
                    "title": "Zombieland",
                    "description": "Current main description",
                    "visibility": 0,
                    "time_created": 1494850000,
                    "time_updated": 1782323598,
                    "preview_url": "https://example.invalid/preview.png",
                    "tags": [
                      { "tag": "Mod", "display_name": "Mod" },
                      { "tag": "1.6", "display_name": "1.6" }
                    ]
                  }
                ]
              }
            }
            """,
            target);

        Assert.Equal(928376710UL, snapshot.PublishedFileId);
        Assert.Equal("/tmp/Zombieland", snapshot.ModPath);
        Assert.Equal("Zombieland", snapshot.ModName);
        Assert.Equal("Zombieland", snapshot.Title);
        Assert.Equal("Current main description", snapshot.Description);
        Assert.Equal(24, snapshot.DescriptionCharacters);
        Assert.Equal(0, snapshot.Visibility);
        Assert.Equal(1782323598, snapshot.TimeUpdated);
        Assert.Equal(294100U, snapshot.ConsumerAppId);
        Assert.Equal(["Mod", "1.6"], snapshot.Tags);
        Assert.DoesNotContain(snapshot.ValidationIssues, issue => issue.Severity == "warning");
    }
}
