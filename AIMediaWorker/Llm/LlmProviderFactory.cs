using AIMediaWorker.Llm.Providers;
using AIMediaWorker.Network;

namespace AIMediaWorker.Llm;

public sealed class LlmProviderFactory(ICredentialService credentials)
{
    public ILlmProvider Create(string provider, string? explicitApiKey = null)
    {
        var isUnsloth = provider.Equals("Unsloth Desktop", StringComparison.OrdinalIgnoreCase) || provider.Equals("Unsloth", StringComparison.OrdinalIgnoreCase);
        var key = explicitApiKey;
        if (key is null) key = credentials.Read(CredentialIdentifier.ForLlm(provider))?.Secret;
        if (key is null && isUnsloth && !provider.Equals("Unsloth", StringComparison.OrdinalIgnoreCase))
            key = credentials.Read(CredentialIdentifier.ForLlm("Unsloth"))?.Secret;
        if (!isUnsloth && string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No API key is stored for {provider}.");
        return provider switch
        {
            "Google" => new GoogleProvider(key!),
            "OllamaCloud" => new OllamaCloudProvider(key!),
            "OpenCodeGo" => new OpenCodeGoProvider(key!),
            "OpenCodeZen" => new OpenCodeZenProvider(key!),
            _ => new UnslothProvider(key)
        };
    }
}
