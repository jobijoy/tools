using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using IdolClick.Models;

namespace IdolClick.Services.Api;

// ═══════════════════════════════════════════════════════════════════════════════════
// API HOST SERVICE — In-process Kestrel server for the WebView2 bridge.
//
// Responsibilities:
//   • Starts ASP.NET Core Minimal API on localhost:{dynamic-port}
//   • Serves React static files from wwwroot/
//   • Hosts REST endpoints (flows, agent, templates, packs, intent)
//   • Hosts SignalR hub (/hub/execution) for real-time events
//   • Zero dependency injection — wraps App.* statics directly
//
// Architecture:
//   "All intelligence produces plans. Only backends execute plans."
//   The API layer is a thin translation shell — no business logic lives here.
//   Every endpoint delegates to an existing service via App.* statics.
//
// Lifecycle:
//   Created and started during App.OnStartup (after all services initialized).
//   Stopped during App.OnExit. Port is dynamic (OS-assigned).
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Hosts an in-process Kestrel server that serves the React UI and REST/SignalR endpoints.
/// WebView2 connects to <c>http://localhost:{Port}/</c>.
/// </summary>
public sealed class ApiHostService : IDisposable
{
    private WebApplication? _app;
    private readonly LogService _log;

    /// <summary>
    /// The dynamically-assigned port Kestrel is listening on.
    /// Available after <see cref="StartAsync"/> completes.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Base URL for the running API (e.g., "http://localhost:12345").
    /// </summary>
    public string BaseUrl => $"http://localhost:{Port}";

    public ApiHostService(LogService log)
    {
        _log = log;
    }

    /// <summary>
    /// Build and start the Kestrel host. Returns once the server is listening.
    /// </summary>
    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // ── Logging — route ASP.NET Core logs through our LogService ─────────
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

        // ── Kestrel — bind to dynamic port on loopback only ──────────────────
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(System.Net.IPAddress.Loopback, 0); // OS picks a free port
        });

        // ── SignalR ──────────────────────────────────────────────────────────
        builder.Services.AddSignalR()
            .AddJsonProtocol(opts =>
            {
                // Match FlowJson conventions so the React client gets camelCase
                opts.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                opts.PayloadSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
            });

        // ── CORS — allow WebView2 origin (not strictly needed, but safe) ─────
        builder.Services.AddCors(opts =>
        {
            opts.AddDefaultPolicy(p => p
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .SetIsOriginAllowed(_ => true));
        });

        _app = builder.Build();

        // ── Middleware pipeline ──────────────────────────────────────────────
        _app.UseCors();

        // Serve React build from wwwroot/ (if it exists — Phase 1A is backend-only)
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            _app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot)
            });
            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot)
            });
        }

        // ── Map endpoints ───────────────────────────────────────────────────
        FlowEndpoints.Map(_app);
        AgentEndpoints.Map(_app);
        TemplateEndpoints.Map(_app);
        ToolEndpoints.Map(_app);

        // ── Map SignalR hub ─────────────────────────────────────────────────
        _app.MapHub<ExecutionHub>("/hub/execution");

        // ── Health check ────────────────────────────────────────────────────
        _app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = "1.0.0" }));

        // ── Start ───────────────────────────────────────────────────────────
        await _app.StartAsync().ConfigureAwait(false);

        // Resolve the dynamic port
        var addresses = _app.Urls;
        foreach (var addr in addresses)
        {
            if (Uri.TryCreate(addr, UriKind.Absolute, out var uri))
            {
                Port = uri.Port;
                break;
            }
        }

        // Fallback: parse from server features
        if (Port == 0)
        {
            var serverAddresses = _app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features
                .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();

            if (serverAddresses != null)
            {
                foreach (var addr in serverAddresses.Addresses)
                {
                    if (Uri.TryCreate(addr, UriKind.Absolute, out var uri))
                    {
                        Port = uri.Port;
                        break;
                    }
                }
            }
        }

        _log.Info("ApiHost", $"Kestrel listening on {BaseUrl}");
    }

    /// <summary>
    /// Gracefully stop the Kestrel host.
    /// </summary>
    public async Task StopAsync()
    {
        if (_app != null)
        {
            _log.Info("ApiHost", "Stopping Kestrel host");
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }

    public void Dispose()
    {
        try
        {
            // Run on threadpool to avoid sync-over-async deadlock on the WPF UI thread.
            // StopAsync awaits Kestrel shutdown, which can deadlock if .GetResult()
            // blocks the dispatcher thread that Kestrel continuations need.
            var task = Task.Run(() => StopAsync());
            if (!task.Wait(TimeSpan.FromSeconds(3)))
                _log.Debug("ApiHost", "Kestrel shutdown timed out after 3 s — forcing exit");
        }
        catch (Exception ex)
        {
            _log.Debug("ApiHost", $"Kestrel shutdown error (non-fatal): {ex.Message}");
        }
    }
}
