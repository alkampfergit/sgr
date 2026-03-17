namespace Alkampfer.Sgr.Runtime;

/// <summary>
/// Minimal Azure OpenAI settings shared by direct API-based components.
/// </summary>
public sealed record AzureOpenAiConfiguration(
    string Endpoint,
    string ApiKey,
    string DeploymentId);
