/*
CREC Web - Chat Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using CREC_Web.Models;
using CREC_Web.Services.Chat;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController(IMcpChatClient chatClient, ILogger<ChatController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "Message is required." });

        try
        {
            var text = await chatClient.ProcessChatAsync(request, cancellationToken);
            return text == null ? Ok(new { error = "empty_response" }) : Ok(new { text });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "MCP chat request timed out");
            return StatusCode(504, new { error = "timeout" });
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to connect to MCP server");
            return StatusCode(503, new { error = "server_unavailable" });
        }
        catch (McpException ex)
        {
            logger.LogWarning(ex, "Invalid MCP chat response");
            return StatusCode(502, new { error = "invalid_response" });
        }
    }
}
