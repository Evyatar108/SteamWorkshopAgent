using System.Net.Http;
using System.Text.Json;

namespace SteamWorkshopAgent;

public sealed class WorkshopDescriptionReader
{
    private const string DetailsApi = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    internal static readonly TimeSpan DetailsCommandTimeout = TimeSpan.FromMinutes(25);
    private readonly WorkshopTargetResolver targetResolver;
    private readonly Func<string?> getDetailsCommand;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, Task<ProcessResult>> runCommand;

    public WorkshopDescriptionReader(WorkshopTargetResolver targetResolver)
        : this(targetResolver,
            () => Environment.GetEnvironmentVariable("STEAM_WORKSHOP_AGENT_DETAILS_COMMAND"),
            (file, args, timeout) => new ProcessRunner().RunAsync(
                file, args, timeout: timeout, maxOutputChars: 16 * 1024 * 1024))
    {
    }

    internal WorkshopDescriptionReader(
        WorkshopTargetResolver targetResolver,
        Func<string?> getDetailsCommand,
        Func<string, IReadOnlyList<string>, TimeSpan, Task<ProcessResult>> runCommand)
    {
        this.targetResolver = targetResolver;
        this.getDetailsCommand = getDetailsCommand;
        this.runCommand = runCommand;
    }

    public async Task<WorkshopDescriptionSnapshot> GetDescriptionAsync(string modPathOrPublishedFileId)
    {
        var target = await targetResolver.ResolveDescriptionTargetAsync(modPathOrPublishedFileId);
        var commandJson = getDetailsCommand();
        if (!string.IsNullOrWhiteSpace(commandJson))
        {
            var command = ParseDetailsCommand(commandJson);
            var result = await runCommand(command[0],
                command.Skip(1).Append(target.PublishedFileId.ToString()).ToArray(), DetailsCommandTimeout);
            if (result.TimedOut)
                throw new TimeoutException($"Workshop details command timed out after {DetailsCommandTimeout.TotalMinutes} minutes. It was not retried.");
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Workshop details command failed with exit code {result.ExitCode}: {result.Stderr}");
            if (result.StdoutTruncated)
                throw new InvalidOperationException("Workshop details command returned too much output; refusing truncated JSON.");
            return ParseSnapshot(result.Stdout, target);
        }

        using var httpClient = new HttpClient();
        using var response = await httpClient.PostAsync(
            DetailsApi,
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("itemcount", "1"),
                new KeyValuePair<string, string>("publishedfileids[0]", target.PublishedFileId.ToString())
            ]));

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return ParseSnapshot(json, target);
    }

    internal static string[] ParseDetailsCommand(string json)
    {
        try
        {
            var command = JsonSerializer.Deserialize<string[]>(json);
            if (command is not { Length: > 0 } || string.IsNullOrWhiteSpace(command[0])
                || command.Any(argument => argument is null))
                throw new JsonException();
            return command;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "STEAM_WORKSHOP_AGENT_DETAILS_COMMAND must be a JSON array of strings containing an executable and optional arguments.",
                exception);
        }
    }

    public static WorkshopDescriptionSnapshot ParseSnapshot(string json, WorkshopDescriptionTarget target)
    {
        using var document = JsonDocument.Parse(json);
        var details = document.RootElement
            .GetProperty("response")
            .GetProperty("publishedfiledetails");

        if (details.GetArrayLength() == 0)
            throw new InvalidOperationException($"Steam returned no details for Workshop item {target.PublishedFileId}.");

        var item = details[0];
        var result = GetInt(item, "result") ?? 0;
        var description = GetString(item, "description") ?? "";
        var issues = new List<ValidationIssue>();
        if (result != 1)
            issues.Add(new ValidationIssue(
                "steam_result_not_ok",
                $"Steam returned result {result} for Workshop item {target.PublishedFileId}.",
                "warning"));

        return new WorkshopDescriptionSnapshot(
            target.PublishedFileId,
            target.ModPath,
            target.ModName,
            $"https://steamcommunity.com/sharedfiles/filedetails/?id={target.PublishedFileId}",
            result,
            GetString(item, "title") ?? "",
            description,
            description.Length,
            GetInt(item, "visibility"),
            GetLong(item, "time_created"),
            GetLong(item, "time_updated"),
            GetUInt(item, "consumer_app_id"),
            GetString(item, "creator"),
            GetString(item, "preview_url"),
            GetTags(item),
            issues);
    }

    private static IReadOnlyList<string> GetTags(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return [];

        return tags.EnumerateArray()
            .Select(tag => GetString(tag, "display_name") ?? GetString(tag, "tag"))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .ToList();
    }

    private static string? GetString(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? GetInt(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static uint? GetUInt(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static long? GetLong(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)
            ? number
            : null;
    }
}
