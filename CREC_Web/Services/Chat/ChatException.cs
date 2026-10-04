namespace CREC_Web.Services.Chat;

public sealed class ChatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
