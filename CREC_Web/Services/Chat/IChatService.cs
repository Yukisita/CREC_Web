using CREC_Web.Models;

namespace CREC_Web.Services.Chat;

public interface IChatService
{
    Task<ChatResponse?> ProcessChatAsync(ChatRequest request, CancellationToken cancellationToken);
}
