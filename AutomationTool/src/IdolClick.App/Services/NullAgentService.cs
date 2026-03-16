using IdolClick.Models;

namespace IdolClick.Services;

internal sealed class NullAgentService : IAgentService
{
    public bool IsConfigured => false;

    public string StatusText => "Capture-only host";

    public event Action<AgentProgress>? OnProgress;

    public Task<AgentResponse> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentResponse { IsError = true, Text = "Agent is not available in capture-only mode." });

    public void ClearHistory()
    {
    }

    public void Reconfigure()
    {
    }

    public Task<string> CompletionAsync(string prompt, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);
}