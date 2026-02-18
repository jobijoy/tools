using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using IdolClick.Services.Mcp;
using System.Text.Json;
using IdolClick.Models;

namespace IdolClick.Services.Api;

// ═══════════════════════════════════════════════════════════════════════════════════
// TOOL ENDPOINTS — REST surface for tool discovery and MCP-compatible manifest.
//
// These endpoints serve the ToolCatalog in formats consumable by:
//   • WebView2 UI (tool palette, capability browser)
//   • External MCP clients (pre-flight discovery before connecting)
//   • Future Copilot Extension (skillset manifest generation)
//   • CI/CD integrations (capability negotiation)
//
// Routes:
//   GET  /api/tools            — List all tools (MCP-compatible format)
//   GET  /api/tools/{name}     — Get single tool descriptor
//   GET  /api/tools/categories — List tool categories with counts
//   GET  /api/mcp/manifest     — Full MCP tools/list response payload
//
// These endpoints are READ-ONLY discovery. Actual tool execution
// routes live in FlowEndpoints, AgentEndpoints, TemplateEndpoints.
// ═══════════════════════════════════════════════════════════════════════════════════

internal static class ToolEndpoints
{
    public static void Map(WebApplication app)
    {
        // ── List all tools ──────────────────────────────────────────────────
        app.MapGet("/api/tools", (string? category) =>
        {
            IReadOnlyList<ToolDescriptor> tools = string.IsNullOrEmpty(category)
                ? ToolCatalog.All
                : ToolCatalog.GetByCategory(category);

            var result = tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.Parameters.Select(p => new
                {
                    name = p.Name,
                    description = p.Description,
                    type = p.JsonType,
                    required = p.Required,
                    @default = p.Default
                }),
                isAsync = t.IsAsync,
                category = t.Category,
                risk = t.Risk
            });

            return Results.Ok(new
            {
                count = tools.Count,
                tools = result
            });
        });

        // ── Get single tool by name ─────────────────────────────────────────
        app.MapGet("/api/tools/{name}", (string name) =>
        {
            var tool = ToolCatalog.GetByName(name);
            if (tool == null)
                return Results.NotFound(new { error = $"Tool '{name}' not found" });

            return Results.Ok(new
            {
                name = tool.Name,
                description = tool.Description,
                inputSchema = tool.ToInputSchema(),
                isAsync = tool.IsAsync,
                category = tool.Category,
                risk = tool.Risk
            });
        });

        // ── List categories ─────────────────────────────────────────────────
        app.MapGet("/api/tools/categories", () =>
        {
            var categories = ToolCatalog.All
                .GroupBy(t => t.Category)
                .Select(g => new
                {
                    category = g.Key,
                    count = g.Count(),
                    tools = g.Select(t => t.Name)
                })
                .OrderBy(c => c.category);

            return Results.Ok(categories);
        });

        // ── MCP-compatible manifest ─────────────────────────────────────────
        // Returns the exact payload shape that an MCP tools/list response uses.
        // When Phase 2 MCP server is implemented, this same data feeds it.
        app.MapGet("/api/mcp/manifest", () =>
        {
            return Results.Ok(ToolCatalog.ToMcpToolsList());
        });

        // ── MCP readiness check ─────────────────────────────────────────────
        // Returns metadata about MCP server readiness and capabilities.
        // Pre-flight endpoint for clients evaluating MCP connectivity.
        app.MapGet("/api/mcp/status", () =>
        {
            return Results.Ok(new
            {
                mcpServerReady = false,     // Phase 2: will become true
                mcpVersion = "2024-11-05",  // Target MCP protocol version
                transports = new
                {
                    stdio = false,          // Phase 2A: local IDE transport
                    httpSse = false          // Phase 2B: remote transport
                },
                toolCount = ToolCatalog.Count,
                categories = ToolCatalog.All.Select(t => t.Category).Distinct().ToList(),
                apiReady = App.IsApiReady,
                note = "MCP server is planned for Phase 2. Tool catalog is available now via /api/tools and /api/mcp/manifest."
            });
        });
    }
}
