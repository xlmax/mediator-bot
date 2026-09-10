namespace MediatorBot.Infrastructure;

public sealed class OpenAiProviderException : Exception
{
    public OpenAiProviderException(
        int? providerCode,
        string? providerErrorType = null,
        string? providerParameter = null,
        string? providerName = null,
        Exception? innerException = null)
        : base("The provider returned an error instead of a chat completion.", innerException)
    {
        ProviderCode = providerCode;
        ProviderErrorType = providerErrorType;
        ProviderParameter = providerParameter;
        ProviderName = providerName;
    }

    public int? ProviderCode { get; }

    public string? ProviderErrorType { get; }

    public string? ProviderParameter { get; }

    public string? ProviderName { get; }
}
