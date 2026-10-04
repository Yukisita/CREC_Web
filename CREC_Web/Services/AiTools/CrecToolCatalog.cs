using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CREC_Web.Services.AiTools;

/// <summary>公式SDKが生成するツール定義をWebMCPにも渡し、同じメソッドを呼び出す。</summary>
public sealed class CrecToolCatalog
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();
    private readonly Dictionary<string, (Tool Definition, AIFunction Function)> tools;

    public CrecToolCatalog(CrecMcpTools target)
    {
        tools = typeof(CrecMcpTools).GetMethods()
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>()?.ReadOnly == true)
            .Select(method => (Definition: McpServerTool.Create(method, target, new() { SerializerOptions = JsonOptions }).ProtocolTool,
                Function: AIFunctionFactory.Create(method, target, new AIFunctionFactoryOptions { SerializerOptions = JsonOptions })))
            .ToDictionary(entry => entry.Definition.Name);
    }

    public IEnumerable<Tool> Definitions => tools.Values.Select(entry => entry.Definition);

    public async Task<object?> InvokeAsync(string name, Dictionary<string, JsonElement> arguments,
        string revision, CancellationToken token)
    {
        if (!tools.TryGetValue(name, out var tool)) throw new CrecReadException("tool-not-found");
        var properties = tool.Definition.InputSchema.GetProperty("properties");
        if (arguments.Keys.Any(key => !properties.TryGetProperty(key, out _))) throw new CrecReadException("invalid-arguments");
        if (properties.TryGetProperty("projectRevision", out _)) arguments["projectRevision"] = JsonSerializer.SerializeToElement(revision);
        return await tool.Function.InvokeAsync(new AIFunctionArguments(arguments.ToDictionary(p => p.Key, p => (object?)p.Value)), token);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        options.Converters.Insert(0, new JsonStringEnumConverter(allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
}
