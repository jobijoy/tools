using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using NAudio.Wave;

namespace IdolClick.Services;

// ═══════════════════════════════════════════════════════════════════════════════════
// VOICE INPUT SERVICE — Push-to-talk voice capture + Azure Whisper transcription.
//
// Architecture:
//   NAudio WaveInEvent → MemoryStream (16kHz/16bit/mono WAV) → Azure OpenAI Whisper
//   → transcribed text → OnTranscriptionReady event → UI inserts into ChatInputBox
//
// Design decisions:
//   • Push-to-talk only — user explicitly starts/stops recording
//   • Silence detection via RMS threshold — hints UI when user stops speaking
//   • Text goes into input box for review, NOT auto-sent — safety over speed
//   • Uses the same Azure endpoint already configured for the LLM
//
// Whisper API:
//   POST {endpoint}/openai/deployments/{whisper}/audio/transcriptions?api-version=2024-06-01
//   Content-Type: multipart/form-data, file: WAV bytes
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Captures audio from the default microphone and transcribes it via Azure OpenAI Whisper.
/// </summary>
public sealed class VoiceInputService : IDisposable
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private WaveInEvent? _waveIn;
    private MemoryStream? _audioStream;
    private WaveFileWriter? _waveWriter;
    private volatile bool _isRecording;
    private DateTime _lastSoundTime;
    private readonly HttpClient _httpClient = new();

    /// <summary>Audio format: 16kHz, 16-bit, mono — Whisper's preferred input.</summary>
    private static readonly WaveFormat AudioFormat = new(16000, 16, 1);

    /// <summary>RMS threshold below which audio is considered silence (0.0–1.0 scale).</summary>
    private const float SilenceThreshold = 0.01f;

    /// <summary>Seconds of continuous silence before raising <see cref="OnSilenceDetected"/>.</summary>
    private const double SilenceTimeoutSeconds = 1.5;

    /// <summary>True while recording audio from the microphone.</summary>
    public bool IsRecording => _isRecording;

    /// <summary>Fired when transcription completes. Payload is the transcribed text.</summary>
    public event Action<string>? OnTranscriptionReady;

    /// <summary>Fired when silence is detected for <see cref="SilenceTimeoutSeconds"/>, hinting the UI.</summary>
    public event Action? OnSilenceDetected;

    /// <summary>Fired when an error occurs during recording or transcription.</summary>
    public event Action<string>? OnError;

    /// <summary>Fired when recording starts.</summary>
    public event Action? OnRecordingStarted;

    /// <summary>Fired when recording stops (before transcription begins).</summary>
    public event Action? OnRecordingStopped;

    public VoiceInputService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Returns true if the voice input feature is configured.
    /// Uses dedicated Whisper endpoint/key if set, otherwise falls back to main agent endpoint/key.
    /// </summary>
    public bool IsConfigured
    {
        get
        {
            var s = _config.GetConfig().AgentSettings;
            if (!s.VoiceInputEnabled) return false;
            if (string.IsNullOrWhiteSpace(s.WhisperDeploymentId)) return false;

            // Whisper-specific endpoint/key take priority; fall back to main agent settings
            var endpoint = !string.IsNullOrWhiteSpace(s.WhisperEndpoint) ? s.WhisperEndpoint : s.Endpoint;
            var apiKey = !string.IsNullOrWhiteSpace(s.WhisperApiKey) ? s.WhisperApiKey : s.ApiKey;

            return !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(apiKey);
        }
    }

    /// <summary>
    /// Resolves the effective Whisper endpoint (dedicated or main agent fallback).
    /// Extracts just the base URL (scheme + host) from full deployment URLs.
    /// </summary>
    private (string endpoint, string apiKey) ResolveWhisperCredentials()
    {
        var s = _config.GetConfig().AgentSettings;
        var rawEndpoint = !string.IsNullOrWhiteSpace(s.WhisperEndpoint) ? s.WhisperEndpoint : s.Endpoint;
        var apiKey = !string.IsNullOrWhiteSpace(s.WhisperApiKey) ? s.WhisperApiKey : s.ApiKey;

        // Extract base URL — users often paste the full chat completions URL
        // e.g. "https://myresource.cognitiveservices.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=..."
        // We need just: "https://myresource.cognitiveservices.azure.com"
        var endpoint = rawEndpoint.TrimEnd('/');
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            endpoint = $"{uri.Scheme}://{uri.Host}";
        }

        return (endpoint, apiKey);
    }

    /// <summary>
    /// Begin capturing audio from the default microphone.
    /// </summary>
    public void StartRecording()
    {
        if (_isRecording) return;

        try
        {
            _audioStream = new MemoryStream();
            _waveWriter = new WaveFileWriter(_audioStream, AudioFormat);

            _waveIn = new WaveInEvent
            {
                WaveFormat = AudioFormat,
                BufferMilliseconds = 100
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStoppedInternal;

            _lastSoundTime = DateTime.UtcNow;
            _isRecording = true;
            _waveIn.StartRecording();

            _log.Info("VoiceInput", "Recording started");
            OnRecordingStarted?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error("VoiceInput", $"Failed to start recording: {ex.Message}");
            OnError?.Invoke($"Microphone error: {ex.Message}");
            CleanupRecording();
        }
    }

    /// <summary>
    /// Stop recording and send the audio to Whisper for transcription.
    /// The result is delivered via <see cref="OnTranscriptionReady"/>.
    /// </summary>
    public async Task StopRecordingAndTranscribeAsync()
    {
        if (!_isRecording) return;
        _isRecording = false;

        try
        {
            _waveIn?.StopRecording();
            OnRecordingStopped?.Invoke();

            // Flush and capture WAV bytes
            if (_waveWriter != null)
            {
                await _waveWriter.FlushAsync().ConfigureAwait(false);
                // WaveFileWriter needs to finalize the header
                _waveWriter.Dispose();
                _waveWriter = null;
            }

            var audioBytes = _audioStream?.ToArray();
            _audioStream?.Dispose();
            _audioStream = null;

            if (audioBytes == null || audioBytes.Length < 1000)
            {
                _log.Warn("VoiceInput", "Recording too short, skipping transcription");
                OnError?.Invoke("Recording too short — please try again");
                return;
            }

            _log.Info("VoiceInput", $"Sending {audioBytes.Length / 1024}KB audio to Whisper");

            var text = await TranscribeAsync(audioBytes).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _log.Info("VoiceInput", $"Transcription: \"{text}\"");
                OnTranscriptionReady?.Invoke(text.Trim());
            }
            else
            {
                _log.Warn("VoiceInput", "Empty transcription result");
                OnError?.Invoke("Could not understand audio — please try again");
            }
        }
        catch (Exception ex)
        {
            _log.Error("VoiceInput", $"Transcription failed: {ex.Message}");
            OnError?.Invoke($"Transcription failed: {ex.Message}");
        }
        finally
        {
            CleanupRecording();
        }
    }

    /// <summary>
    /// Cancel an in-progress recording without transcribing.
    /// </summary>
    public void CancelRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;
        _waveIn?.StopRecording();
        CleanupRecording();
        _log.Info("VoiceInput", "Recording cancelled");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Write audio data to WAV stream
        try
        {
            _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        }
        catch { /* Stream may be disposed during stop */ }

        // Silence detection via RMS
        var rms = CalculateRms(e.Buffer, e.BytesRecorded);
        if (rms > SilenceThreshold)
        {
            _lastSoundTime = DateTime.UtcNow;
        }
        else if ((DateTime.UtcNow - _lastSoundTime).TotalSeconds >= SilenceTimeoutSeconds)
        {
            OnSilenceDetected?.Invoke();
            _lastSoundTime = DateTime.UtcNow; // Reset to avoid repeated firing
        }
    }

    private void OnRecordingStoppedInternal(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _log.Error("VoiceInput", $"Recording device error: {e.Exception.Message}");
            OnError?.Invoke($"Microphone error: {e.Exception.Message}");
        }
    }

    /// <summary>
    /// Send WAV audio bytes to Azure OpenAI Whisper for transcription.
    /// </summary>
    private async Task<string> TranscribeAsync(byte[] wavBytes)
    {
        var settings = _config.GetConfig().AgentSettings;
        var (endpoint, apiKey) = ResolveWhisperCredentials();
        var deployment = settings.WhisperDeploymentId;
        var language = string.IsNullOrWhiteSpace(settings.VoiceLanguage) ? null : NormalizeLanguage(settings.VoiceLanguage);

        // Build the Whisper transcription URL
        // Azure OpenAI: {endpoint}/openai/deployments/{deployment}/audio/transcriptions?api-version=2024-06-01
        // Generic OpenAI: {endpoint}/v1/audio/transcriptions
        string url;
        bool isAzure = endpoint.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                     || endpoint.Contains(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)
                     || endpoint.Contains(".ai.azure.com", StringComparison.OrdinalIgnoreCase);
        if (isAzure)
            url = $"{endpoint}/openai/deployments/{deployment}/audio/transcriptions?api-version=2024-06-01";
        else
            url = $"{endpoint}/v1/audio/transcriptions";

        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "recording.wav");
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent("text"), "response_format");
        if (language != null)
            content.Add(new StringContent(language), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = content;

        // Auth header
        if (isAzure)
            request.Headers.Add("api-key", apiKey);
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Parse Azure error for a user-friendly message
            var friendlyError = ParseApiError(responseText, response.StatusCode, deployment, isAzure);
            _log.Error("VoiceInput", $"Whisper API error {response.StatusCode}: {responseText}");
            throw new HttpRequestException(friendlyError);
        }

        return responseText;
    }

    /// <summary>
    /// Parse the API error response and return a user-friendly message with guidance.
    /// </summary>
    private static string ParseApiError(string responseBody, System.Net.HttpStatusCode statusCode, string deployment, bool isAzure)
    {
        // Try to extract the error code from JSON: {"error":{"code":"...","message":"..."}}
        string? errorCode = null;
        string? errorMessage = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var errorObj))
            {
                errorObj.TryGetProperty("code", out var codeProp);
                errorCode = codeProp.GetString();
                errorObj.TryGetProperty("message", out var msgProp);
                errorMessage = msgProp.GetString();
            }
        }
        catch { /* Not JSON or unexpected shape — fall through */ }

        return errorCode switch
        {
            "DeploymentNotFound" => isAzure
                ? $"Whisper deployment '{deployment}' not found on your Azure OpenAI resource. "
                  + "Go to Azure Portal → your OpenAI resource → Model Deployments → deploy a Whisper model, "
                  + "then enter that deployment name in Settings → Voice Input → Whisper Deployment ID."
                : $"Deployment '{deployment}' not found. Check your Whisper deployment name in Settings.",

            "model_not_found" =>
                $"Model '{deployment}' not found. Ensure you have a Whisper model deployed and the deployment name matches.",

            "Unauthorized" or "401" =>
                "Authentication failed. Check your API Key in Settings → Agent (AI).",

            "RateLimitExceeded" or "429" =>
                "Rate limit exceeded. Wait a moment and try again.",

            "InvalidRequest" =>
                $"Invalid request: {errorMessage ?? responseBody}",

            _ => $"Whisper API error ({statusCode}): {errorMessage ?? errorCode ?? responseBody}"
        };
    }

    /// <summary>
    /// Calculate Root Mean Square of 16-bit PCM audio buffer for silence detection.
    /// </summary>
    private static float CalculateRms(byte[] buffer, int bytesRecorded)
    {
        long sumOfSquares = 0;
        int sampleCount = bytesRecorded / 2; // 16-bit = 2 bytes per sample
        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumOfSquares += (long)sample * sample;
        }
        if (sampleCount == 0) return 0;
        var rms = Math.Sqrt(sumOfSquares / (double)sampleCount);
        return (float)(rms / short.MaxValue); // Normalize to 0.0–1.0
    }

    private void CleanupRecording()
    {
        _waveIn?.Dispose();
        _waveIn = null;
        _waveWriter?.Dispose();
        _waveWriter = null;
        _audioStream?.Dispose();
        _audioStream = null;
    }

    /// <summary>
    /// Normalize common language names to ISO-639-1 codes that Whisper expects.
    /// If already a 2-letter code, passes through unchanged.
    /// </summary>
    private static string NormalizeLanguage(string input)
    {
        var trimmed = input.Trim().ToLowerInvariant();

        // Already ISO-639-1 (2 chars)
        if (trimmed.Length == 2) return trimmed;

        return trimmed switch
        {
            "english" => "en",
            "spanish" or "español" => "es",
            "french" or "français" => "fr",
            "german" or "deutsch" => "de",
            "italian" or "italiano" => "it",
            "portuguese" or "português" => "pt",
            "dutch" or "nederlands" => "nl",
            "russian" or "русский" => "ru",
            "chinese" or "mandarin" or "中文" => "zh",
            "japanese" or "日本語" => "ja",
            "korean" or "한국어" => "ko",
            "arabic" or "العربية" => "ar",
            "hindi" or "हिन्दी" => "hi",
            "turkish" or "türkçe" => "tr",
            "polish" or "polski" => "pl",
            "swedish" or "svenska" => "sv",
            "danish" or "dansk" => "da",
            "norwegian" or "norsk" => "no",
            "finnish" or "suomi" => "fi",
            "czech" or "čeština" => "cs",
            "greek" or "ελληνικά" => "el",
            "hebrew" or "עברית" => "he",
            "thai" or "ไทย" => "th",
            "vietnamese" or "tiếng việt" => "vi",
            "indonesian" or "bahasa indonesia" => "id",
            "malay" or "bahasa melayu" => "ms",
            "ukrainian" or "українська" => "uk",
            "romanian" or "română" => "ro",
            "hungarian" or "magyar" => "hu",
            _ => trimmed // Pass through as-is (may be a valid code we don't map)
        };
    }

    public void Dispose()
    {
        CancelRecording();
        _httpClient.Dispose();
    }
}
