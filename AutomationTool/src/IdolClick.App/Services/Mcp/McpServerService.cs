using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace IdolClick.Services.Mcp;

// ═══════════════════════════════════════════════════════════════════════════════════
// MCP SERVER SERVICE — stdio transport entry point for IDE integration.
//
// Invoked via: IdolClick.exe --mcp [--config <path>]
//
// Architecture (latest MCP C# SDK v0.8.0-preview.1 pattern):
//   • Uses Microsoft.Extensions.Hosting (Host.CreateApplicationBuilder)
//   • Registers MCP server with .AddMcpServer().WithStdioServerTransport()
//   • Discovers tools via .WithTools<McpDiscoveryTools>() etc. ([McpServerToolType])
//   • ServiceHost (WPF-free core) is registered as singleton for DI injection
//   • All logging goes to stderr (stdout is reserved for MCP JSON-RPC)
//
// MCP protocol:
//   • stdin  ← JSON-RPC 2.0 requests from IDE/client
//   • stdout → JSON-RPC 2.0 responses to IDE/client
//   • stderr → logs (visible in IDE's MCP server output panel)
//
// The server stays alive until the client disconnects (closes stdin).
// ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Bootstraps IdolClick as an MCP server with stdio transport.
/// Used when the application is launched with --mcp CLI flag.
/// </summary>
public static class McpServerService
{
    /// <summary>
    /// Starts the MCP server with stdio transport. Blocks until the client disconnects.
    /// </summary>
    /// <param name="configPath">Path to config.json for ServiceHost initialization.</param>
    /// <param name="args">Original CLI args (passed to Host builder for config binding).</param>
    public static async Task RunAsync(string configPath, string[] args)
    {
        // ── Boot WPF-free core services ──────────────────────────────────
        var host = ServiceHost.Create(configPath);

        Console.Error.WriteLine($"IdolClick MCP Server v{ServiceHost.Version}");
        Console.Error.WriteLine($"Config: {configPath}");
        Console.Error.WriteLine($"Agent configured: {host.Agent.IsConfigured}");

        // ── Build the MCP host using latest SDK pattern ──────────────────
        var builder = Host.CreateApplicationBuilder(args);

        // Route ALL logging to stderr — stdout is exclusively for MCP JSON-RPC
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);

        // Register ServiceHost as singleton so MCP tools can inject it
        builder.Services.AddSingleton(host);

        // Register TestSpecRunner as singleton (holds last report state)
        builder.Services.AddSingleton<TestSpecRunner>();

        // Register the MCP server with stdio transport and tool types
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "idolclick",
                    Version = ServiceHost.Version
                };
            })
            .WithStdioServerTransport()
            .WithTools<McpDiscoveryTools>()
            .WithTools<McpExecutionTools>()
            .WithTools<McpTestSpecTools>();

        // ── Run ──────────────────────────────────────────────────────────
        Console.Error.WriteLine("MCP server starting on stdio transport...");
        Console.Error.WriteLine($"Tools registered: discovery (6) + execution (3) + testspec (3) = 12");

        var app = builder.Build();

        try
        {
            await app.RunAsync();
        }
        finally
        {
            host.Dispose();
            Console.Error.WriteLine("MCP server stopped.");
        }
    }
}
