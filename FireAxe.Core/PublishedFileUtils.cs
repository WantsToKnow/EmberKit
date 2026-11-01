// Modified for EmberKit, based on ktxiaok/FireAxe 0.7.3; see MODIFICATIONS.md.
using System;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FireAxe;

public static class PublishedFileUtils
{
    private static readonly Regex s_publishedFileIdLinkRegex = new(@"(?:^|&)id=(\d+)(?:&|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static async Task<GetPublishedFileDetailsResult> GetPublishedFileDetailsAsync(ulong publishedFileId, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var results = await GetPublishedFileDetailsBatchAsync([publishedFileId], httpClient, cancellationToken).ConfigureAwait(false);
        return results[publishedFileId];
    }

    public static async Task<IReadOnlyDictionary<ulong, GetPublishedFileDetailsResult>> GetPublishedFileDetailsBatchAsync(
        IEnumerable<ulong> publishedFileIds, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var ids = publishedFileIds.Distinct().ToArray();
        var results = ids.ToDictionary(id => id, _ => new GetPublishedFileDetailsResult(null, GetPublishedFileDetailsResultStatus.Failed));
        foreach (var chunk in ids.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var fields = chunk.Select((id, index) => new KeyValuePair<string, string>($"publishedfileids[{index}]", id.ToString()))
                    .Prepend(new("itemcount", chunk.Length.ToString()));
                using var content = new FormUrlEncodedContent(fields);
                using var response = await httpClient.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", content, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = JObject.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                if (json["response"]?["publishedfiledetails"] is not JArray details) continue;
                foreach (var element in details)
                {
                    if (!ulong.TryParse(element["publishedfileid"]?.ToString(), out var id) || !results.ContainsKey(id)) continue;
                    var result = (int?)element["result"];
                    if (result == 1 && (int?)element["consumer_app_id"] == 550 && element.ToObject<PublishedFileDetails>() is { } file)
                        results[id] = new(file, GetPublishedFileDetailsResultStatus.Succeeded);
                    else if (result is 1 or 9 or 15)
                        results[id] = new(null, GetPublishedFileDetailsResultStatus.InvalidPublishedFileId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Warning(ex, "Workshop metadata request failed."); }
        }
        return results;
    }

    public static Uri? GetDownloadUri(PublishedFileDetails details)
    {
        if (!details.FileName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(details.FileUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var host = uri.Host;
        if (!(host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".steamcontent.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase))) return null;
        return uri.Scheme == "https" ? uri : new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
    }

    public static bool TryParsePublishedFileId(string input, out ulong id)
    {
        ArgumentNullException.ThrowIfNull(input);

        input = input.Trim();
        if (ulong.TryParse(input, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0)
        {
            return true;
        }

        if (TryParsePublishedFileIdLink(input, out id))
        {
            return true;
        }

        return false;
    }

    public static bool TryParsePublishedFileIdLink(string input, out ulong id)
    {
        ArgumentNullException.ThrowIfNull(input);

        id = 0;

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            !(uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase)) ||
            uri.AbsolutePath.TrimEnd('/') is not ("/sharedfiles/filedetails" or "/workshop/filedetails")) return false;
        var match = s_publishedFileIdLinkRegex.Match(uri.Query.TrimStart('?'));
        if (match.Success)
        {
            if (ulong.TryParse(match.Groups[1].ValueSpan, out id) && id > 0)
            {
                return true;
            }
        }

        return false;
    }
}

public class GetPublishedFileDetailsResult
{
    private PublishedFileDetails? _content;
    private GetPublishedFileDetailsResultStatus _status;

    internal GetPublishedFileDetailsResult(PublishedFileDetails? content, GetPublishedFileDetailsResultStatus status)
    {
        if (status == GetPublishedFileDetailsResultStatus.Succeeded && content == null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        _content = content;
        _status = status;
    }

    public PublishedFileDetails Content
    {
        get
        {
            if (!IsSucceeded)
            {
                throw new InvalidOperationException("The status isn't succeeded.");
            }
            return _content!;
        }
    }

    public GetPublishedFileDetailsResultStatus Status => _status;

    public bool IsSucceeded => _status == GetPublishedFileDetailsResultStatus.Succeeded;
}

public enum GetPublishedFileDetailsResultStatus
{
    Succeeded,
    Failed,
    InvalidPublishedFileId,
}
