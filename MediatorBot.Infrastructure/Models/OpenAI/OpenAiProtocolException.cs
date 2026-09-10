namespace MediatorBot.Infrastructure;

public enum OpenAiProtocolFailureReason
{
    Unknown,
    InvalidJson,
    NoChoices,
    OutputTokenLimit,
    ContentFilter,
    MissingAssistantMessage,
    InvalidAssistantContent,
    MissingToolCall,
    UnexpectedAssistantText,
    MultipleToolCalls,
    InvalidToolCall
}

public class OpenAiProtocolException : Exception
{
    public OpenAiProtocolException(string message)
        : this(OpenAiProtocolFailureReason.Unknown, message)
    {
    }

    public OpenAiProtocolException(string message, Exception innerException)
        : this(OpenAiProtocolFailureReason.Unknown, message, innerException)
    {
    }

    public OpenAiProtocolException(
        OpenAiProtocolFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public OpenAiProtocolFailureReason Reason { get; }
}
