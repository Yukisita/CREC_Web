using CREC_Web.Models;

namespace CREC_Web.Services.Chat;

public interface IMcpChatClient
{
    Task<string?> ProcessChatAsync(ChatRequest request, CancellationToken cancellationToken);
}
