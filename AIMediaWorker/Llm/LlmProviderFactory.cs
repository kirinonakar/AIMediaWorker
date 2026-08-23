using AIMediaWorker.Llm.Providers;
using AIMediaWorker.Network;

namespace AIMediaWorker.Llm;

public sealed class LlmProviderFactory(ICredentialService credentials)
{
    public ILlmProvider Create(string provider, string? explicitApiKey = null)
    {
        var key = explicitApiKey;
        if (key is null) key = credentials.Read(CredentialIdentifier.ForLlm(provider))?.Secret;
        if (!provider.Equals("Unsloth", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(key))
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
