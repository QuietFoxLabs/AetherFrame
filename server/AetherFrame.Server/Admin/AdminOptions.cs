using System;

namespace AetherFrame.Server.Admin;

internal sealed class AdminOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public long OwnerGitHubId { get; set; }

    public void Validate()
    {
        if (Enabled && (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret) || OwnerGitHubId <= 0))
            throw new InvalidOperationException("The Community Desk requires GitHub OAuth configuration and a numeric owner ID.");
    }
}
