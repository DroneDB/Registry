#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.HeavyTasks.NodeOdx;

/// <summary>
/// <see cref="INodeOdxClient"/> backed by <see cref="IHttpClientFactory"/>.
/// Parsing is delegated to internal static helpers so the response shapes can be
/// unit-tested without a live node.
/// </summary>
public sealed class NodeOdxClient : INodeOdxClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _requestTimeout;
    private readonly ILogger<NodeOdxClient> _log;

    public NodeOdxClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AppSettings> appSettings,
        ILogger<NodeOdxClient> log)
    {
        _httpClientFactory = httpClientFactory;
        var seconds = appSettings.Value.ProcessingPlatform?.RemoteNodeRequestTimeoutSeconds ?? 30;
        _requestTimeout = TimeSpan.FromSeconds(Math.Max(5, seconds));
        _log = log;
    }

    private HttpClient NewClient()
    {
        var client = _httpClientFactory.CreateClient("nodeodx");
        client.Timeout = _requestTimeout;
        return client;
    }

    private static string BuildUrl(NodeOdxEndpoint node, string relative, params (string Key, string Value)[] query)
    {
        var baseUrl = node.Url.TrimEnd('/');
        var sb = new StringBuilder($"{baseUrl}/{relative.TrimStart('/')}");
        var sep = '?';
        if (!string.IsNullOrEmpty(node.Token))
        {
            sb.Append(sep).Append("token=").Append(Uri.EscapeDataString(node.Token));
            sep = '&';
        }
        foreach (var (k, v) in query)
        {
            sb.Append(sep).Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
            sep = '&';
        }
        return sb.ToString();
    }

    public async Task<NodeOdxInfo> GetInfoAsync(NodeOdxEndpoint node, CancellationToken ct)
    {
        using var client = NewClient();
        var json = await client.GetStringAsync(BuildUrl(node, "info"), ct);
        return ParseInfo(json);
    }

    public async Task<string> CreateTaskAsync(
        NodeOdxEndpoint node,
        string name,
        IReadOnlyList<string> imageFilePaths,
        string? optionsJson,
        CancellationToken ct)
    {
        if (imageFilePaths.Count == 0)
            throw new InvalidOperationException("At least one image is required to create a NodeODX task.");

        using var client = NewClient();
        using var form = new MultipartFormDataContent();
        var streams = new List<Stream>();
        try
        {
            form.Add(new StringContent(name), "name");
            if (!string.IsNullOrWhiteSpace(optionsJson))
                form.Add(new StringContent(optionsJson, Encoding.UTF8, "application/json"), "options");

            foreach (var path in imageFilePaths)
            {
                var stream = File.OpenRead(path);
                streams.Add(stream);
                var content = new StreamContent(stream);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(content, "images", Path.GetFileName(path));
            }

            using var resp = await client.PostAsync(BuildUrl(node, "task/new"), form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"NodeODX task/new failed ({(int)resp.StatusCode}): {Truncate(body)}");

            return ParseNewTaskUuid(body);
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    public async Task<NodeOdxTaskInfo> GetTaskInfoAsync(NodeOdxEndpoint node, string uuid, CancellationToken ct)
    {
        using var client = NewClient();
        var json = await client.GetStringAsync(BuildUrl(node, $"task/{uuid}/info"), ct);
        return ParseTaskInfo(json, uuid);
    }

    public async Task<IReadOnlyList<string>> GetTaskOutputAsync(
        NodeOdxEndpoint node, string uuid, int sinceLine, CancellationToken ct)
    {
        using var client = NewClient();
        var json = await client.GetStringAsync(
            BuildUrl(node, $"task/{uuid}/output", ("line", sinceLine.ToString())), ct);
        return ParseOutput(json);
    }

    public async Task CancelTaskAsync(NodeOdxEndpoint node, string uuid, CancellationToken ct)
    {
        using var client = NewClient();
        using var content = JsonBody(new { uuid });
        try
        {
            using var resp = await client.PostAsync(BuildUrl(node, "task/cancel"), content, ct);
            // best-effort: cancellation is idempotent
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "NodeODX cancel failed for task {Uuid}", uuid);
        }
    }

    public async Task RemoveTaskAsync(NodeOdxEndpoint node, string uuid, CancellationToken ct)
    {
        using var client = NewClient();
        using var content = JsonBody(new { uuid });
        try
        {
            using var resp = await client.PostAsync(BuildUrl(node, "task/remove"), content, ct);
            // best-effort cleanup
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "NodeODX remove failed for task {Uuid}", uuid);
        }
    }

    public async Task DownloadAssetAsync(
        NodeOdxEndpoint node, string uuid, string asset, string destFilePath, CancellationToken ct)
    {
        using var client = NewClient();
        using var resp = await client.GetAsync(
            BuildUrl(node, $"task/{uuid}/download/{asset}"), HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"NodeODX asset download '{asset}' failed ({(int)resp.StatusCode}): {Truncate(body)}");
        }

        await using var source = await resp.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destFilePath);
        await source.CopyToAsync(dest, ct);
    }

    public async Task<IReadOnlyList<NodeOdxOption>> GetOptionsAsync(NodeOdxEndpoint node, CancellationToken ct)
    {
        using var client = NewClient();
        var json = await client.GetStringAsync(BuildUrl(node, "options"), ct);
        return ParseOptions(json);
    }

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string Truncate(string s) => s.Length <= 512 ? s : s[..512];

    #region Parsing (internal for unit tests)

    internal static NodeOdxInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new NodeOdxInfo(
            Version: GetString(root, "version"),
            TaskQueueCount: GetInt(root, "taskQueueCount") ?? 0,
            MaxParallelTasks: GetInt(root, "maxParallelTasks") ?? 1,
            Engine: GetString(root, "engine"),
            EngineVersion: GetString(root, "engineVersion"));
    }

    internal static string ParseNewTaskUuid(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException($"NodeODX rejected the task: {err.GetString()}");
        var uuid = GetString(root, "uuid");
        if (string.IsNullOrWhiteSpace(uuid))
            throw new InvalidOperationException("NodeODX task/new returned no uuid.");
        return uuid!;
    }

    internal static NodeOdxTaskInfo ParseTaskInfo(string json, string uuid)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var topErr) && topErr.ValueKind == JsonValueKind.String)
            return new NodeOdxTaskInfo(uuid, NodeOdxTaskStatusCode.Failed, topErr.GetString(), 0, 0);

        var code = NodeOdxTaskStatusCode.Queued;
        string? errorMessage = null;
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            var c = GetInt(status, "code");
            if (c is not null && Enum.IsDefined(typeof(NodeOdxTaskStatusCode), c.Value))
                code = (NodeOdxTaskStatusCode)c.Value;
            errorMessage = GetString(status, "errorMessage");
        }

        return new NodeOdxTaskInfo(
            Uuid: GetString(root, "uuid") ?? uuid,
            StatusCode: code,
            ErrorMessage: errorMessage,
            Progress: GetDouble(root, "progress") ?? 0,
            ImagesCount: GetInt(root, "imagesCount") ?? 0);
    }

    internal static IReadOnlyList<string> ParseOutput(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return [];

        var lines = new List<string>(root.GetArrayLength());
        foreach (var el in root.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String)
                lines.Add(el.GetString() ?? string.Empty);
        return lines;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int? GetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)
            ? n
            : null;

    private static double? GetDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)
            ? d
            : null;

    private static IReadOnlyList<NodeOdxOption> ParseOptions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            return [];

        var options = new List<NodeOdxOption>(root.GetArrayLength());
        foreach (var el in root.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;

            var name = GetString(el, "name") ?? string.Empty;
            var type = GetString(el, "type") ?? "string";
            var help = GetString(el, "help");

            // domain: string (unit label) or array (enum choices)
            object? domain = null;
            if (el.TryGetProperty("domain", out var domEl))
            {
                domain = domEl.ValueKind switch
                {
                    JsonValueKind.String => domEl.GetString(),
                    JsonValueKind.Array => (from item in domEl.EnumerateArray()
                        where item.ValueKind == JsonValueKind.String
                        select item.GetString() ?? string.Empty).ToArray(),
                    _ => domain
                };
            }

            // value: default value (can be string, number, bool, null)
            object? value = null;
            if (el.TryGetProperty("value", out var valEl) && valEl.ValueKind != JsonValueKind.Null)
            {
                value = valEl.ValueKind switch
                {
                    JsonValueKind.String => valEl.GetString(),
                    JsonValueKind.Number when valEl.TryGetInt32(out var n) => n,
                    JsonValueKind.Number when valEl.TryGetDouble(out var d) => d,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => valEl.GetRawText()
                };
            }

            options.Add(new NodeOdxOption(name, type, domain, help, value));
        }

        return options;
    }

    #endregion
}
