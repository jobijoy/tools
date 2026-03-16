using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IdolClick.Models;

namespace IdolClick.Services;

public sealed class CaptureSyncService : IDisposable
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly SnapCaptureService _snapCapture;
    private readonly CaptureAnnotationService _annotations;
    private readonly Channel<CaptureSyncEnvelope> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly List<ICaptureSyncAdapter> _adapters;

    public CaptureSyncService(
        ConfigService config,
        LogService log,
        ReportService reports,
        SnapCaptureService snapCapture,
        CaptureAnnotationService annotations)
    {
        _config = config;
        _log = log;
        _snapCapture = snapCapture;
        _annotations = annotations;
        _adapters = CreateAdapters(reports);
        _queue = Channel.CreateUnbounded<CaptureSyncEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _snapCapture.CaptureCompleted += OnCaptureCompleted;
        _annotations.AnnotationAdded += OnAnnotationAdded;
        _worker = Task.Run(ProcessQueueAsync);
    }

    private List<ICaptureSyncAdapter> CreateAdapters(ReportService reports)
    {
        var capture = _config.GetConfig().Capture;
        var adapters = new List<ICaptureSyncAdapter>();
        if (capture.SyncFileExportEnabled)
            adapters.Add(new FileCaptureSyncAdapter(capture, reports));
        if (capture.SyncWebhookEnabled && !string.IsNullOrWhiteSpace(capture.SyncWebhookUrl))
            adapters.Add(new WebhookCaptureSyncAdapter(capture));
        return adapters;
    }

    private void OnCaptureCompleted(CaptureEventResult result)
    {
        Enqueue(new CaptureSyncEnvelope
        {
            EnvelopeType = "capture",
            CreatedAt = DateTime.UtcNow,
            CaptureEventId = result.EventId,
            Payload = JsonSerializer.SerializeToElement(result, SyncJsonOptions)
        });
    }

    private void OnAnnotationAdded(CaptureAnnotationEntry entry)
    {
        Enqueue(new CaptureSyncEnvelope
        {
            EnvelopeType = "annotation",
            CreatedAt = DateTime.UtcNow,
            AnnotationId = entry.AnnotationId,
            CaptureEventId = entry.RelatedCaptureEventIds.FirstOrDefault() ?? string.Empty,
            Payload = JsonSerializer.SerializeToElement(entry, SyncJsonOptions)
        });
    }

    private void Enqueue(CaptureSyncEnvelope envelope)
    {
        if (_adapters.Count == 0)
            return;

        _queue.Writer.TryWrite(envelope);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var envelope in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                var timeline = _annotations.ListMergedTimeline(50);
                foreach (var adapter in _adapters)
                {
                    try
                    {
                        await adapter.PublishAsync(envelope, timeline, _cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("CaptureSync", $"{adapter.Name} failed: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _snapCapture.CaptureCompleted -= OnCaptureCompleted;
        _annotations.AnnotationAdded -= OnAnnotationAdded;
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        foreach (var adapter in _adapters)
            adapter.Dispose();
        _cts.Dispose();
    }

    private static readonly JsonSerializerOptions SyncJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}

public class CaptureSyncEnvelope
{
    public string EnvelopeType { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string CaptureEventId { get; set; } = string.Empty;
    public string AnnotationId { get; set; } = string.Empty;
    public JsonElement Payload { get; set; }
}

internal interface ICaptureSyncAdapter : IDisposable
{
    string Name { get; }
    Task PublishAsync(CaptureSyncEnvelope envelope, IReadOnlyList<CaptureTimelineItem> mergedTimeline, CancellationToken cancellationToken);
}

internal sealed class FileCaptureSyncAdapter : ICaptureSyncAdapter
{
    private readonly string _outputDirectory;

    public FileCaptureSyncAdapter(CaptureWorkspaceSettings settings, ReportService reports)
    {
        _outputDirectory = string.IsNullOrWhiteSpace(settings.SyncExportDirectory)
            ? Path.Combine(reports.ReportsDirectory, "_captures", "_sync_outbox")
            : settings.SyncExportDirectory;
    }

    public string Name => "file-export";

    public Task PublishAsync(CaptureSyncEnvelope envelope, IReadOnlyList<CaptureTimelineItem> mergedTimeline, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_outputDirectory);
        var payload = new
        {
            envelope.EnvelopeType,
            envelope.CreatedAt,
            envelope.CaptureEventId,
            envelope.AnnotationId,
            payload = envelope.Payload,
            mergedTimeline
        };
        var fileName = $"{envelope.CreatedAt:yyyyMMdd_HHmmss_fff}_{envelope.EnvelopeType}.json";
        File.WriteAllText(Path.Combine(_outputDirectory, fileName), JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

internal sealed class WebhookCaptureSyncAdapter : ICaptureSyncAdapter
{
    private readonly string _url;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient = new();

    public WebhookCaptureSyncAdapter(CaptureWorkspaceSettings settings)
    {
        _url = settings.SyncWebhookUrl;
        _apiKey = settings.SyncWebhookApiKey;
    }

    public string Name => "webhook-sync";

    public async Task PublishAsync(CaptureSyncEnvelope envelope, IReadOnlyList<CaptureTimelineItem> mergedTimeline, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                envelope.EnvelopeType,
                envelope.CreatedAt,
                envelope.CaptureEventId,
                envelope.AnnotationId,
                payload = envelope.Payload,
                mergedTimeline
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            }), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _httpClient.Dispose();
}