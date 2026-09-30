namespace CREC_Web.Services.Chat;

public sealed class McpException(string message, Exception? innerException = null)
    : Exception(message, innerException);
