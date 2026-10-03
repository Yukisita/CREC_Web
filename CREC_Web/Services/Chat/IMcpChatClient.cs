using CREC_Web.Models;

namespace CREC_Web.Services.Chat;

public interface IMcpChatClient
{
    Task<ChatResponse?> ProcessChatAsync(ChatRequest request, CancellationToken cancellationToken);
}
