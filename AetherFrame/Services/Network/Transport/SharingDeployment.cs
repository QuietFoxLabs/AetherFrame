using AetherFrame.Protocol.Requests;

namespace AetherFrame.Services.Network.Transport;

/// <summary>
/// The one sharing server the plugin talks to (decision R2): a DNS name fixed here, never
/// entered by the player and never taken from an answer. It is the owner's domain, deployed by
/// N2-8's workflow (docs/networking/Runbook.md).
/// </summary>
internal static class SharingDeployment
{
    internal static readonly DeploymentName Name = DeploymentName.Parse("plates.aetherframe.dev");
}
