using System.Text.Json;
using CREC_Web.Models;

namespace CREC_Web.Services.Chat;

/// <summary>The same operation definitions constrain model output and validate it before execution.</summary>
public static class ChatActionPolicy
{
    public const int MaxActions = 32;
    private const double MaxSafeNumber = 9007199254740991;
    private static readonly Dictionary<string, string[]> ActionFields = new()
    {
        ["search"] = ["text"], ["showCollectionPanel"] = ["id"],
        ["openCollectionByName"] = ["name"], ["navigateToCollectionByName"] = ["name"],
        ["showAdminPanel"] = [], ["createNewCollection"] = [], ["navigateHome"] = [],
        ["navigate"] = ["path"], ["clickButton"] = ["id"],
        ["fillInput"] = ["id", "value"], ["switchLanguage"] = ["lang"]
    };
    private static readonly HashSet<string> ButtonIds =
    [
        "addNewCollectionBtn", "editProjectBtn", "adminPanelToggle", "searchButton", "clearFiltersButton",
        "inventoryOperationBtn", "inventoryManagementSettingsBtn", "inventoryOperationSave", "inventoryOperationCancel",
        "inventoryManagementSettingsSave", "inventoryManagementSettingsCancel", "editIndexBtn", "projectEditSaveBtn",
        "saveIndexEdit", "toggleAdvancedFiltersButton", "gridViewBtn", "tableViewBtn", "openProjectBtn",
        "refreshProjectsBtn", "cancelProjectSelectionBtn"
    ];
    private static readonly HashSet<string> InputIds =
    [
        "operationType", "operationQuantity", "operationComment", "safetyStock", "reorderPoint", "maximumLevel",
        "searchText", "searchField", "searchMethod", "inventoryStatusFilter", "editName", "editManagementCode",
        "editRegistrationDate", "editCategory", "editFirstTag", "editSecondTag", "editThirdTag", "editLocation",
        "editProjectName", "editCollectionNameLabel", "editUUIDLabel", "editManagementCodeLabel",
        "editCategoryLabel", "editTag1Label", "editTag2Label", "editTag3Label"
    ];
    private static readonly string[] Languages = ["ja", "en", "de"];

    public static object ResponseFormat { get; } = new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "crec_chat_response", strict = true,
            schema = new
            {
                type = "object", additionalProperties = false, required = new[] { "text", "actions" },
                properties = new
                {
                    text = new { type = "string" },
                    actions = new { type = "array", maxItems = MaxActions,
                        items = new { anyOf = ActionFields.Select(pair => ActionSchema(pair.Key, pair.Value)).ToArray() } }
                }
            }
        }
    };

    private static object ActionSchema(string type, string[] fields)
    {
        var properties = new Dictionary<string, object> { ["type"] = new { type = "string", @enum = new[] { type } } };
        foreach (var field in fields)
            properties[field] = field switch
            {
                "id" when type == "clickButton" => new { type = "string", @enum = ButtonIds.Order().ToArray() },
                "id" when type == "fillInput" => new { type = "string", @enum = InputIds.Order().ToArray() },
                "lang" => new { type = "string", @enum = Languages },
                "value" => new { anyOf = new object[] { new { type = "string" },
                    new { type = "number", minimum = -MaxSafeNumber, maximum = MaxSafeNumber } } },
                _ => new { type = "string" }
            };
        return new { type = "object", additionalProperties = false, required = new[] { "type" }.Concat(fields).ToArray(), properties };
    }

    public static ChatResponse? ParseResponse(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (!HasFields(root, ["text", "actions"]) || root.GetProperty("text").ValueKind != JsonValueKind.String ||
                root.GetProperty("actions").ValueKind != JsonValueKind.Array)
                throw new ChatException("The model returned an invalid response envelope.");
            var actions = root.GetProperty("actions").EnumerateArray().ToArray();
            // Reject a whole plan, so a bad input can never leave its save operation executable.
            if (actions.Any(IsDeletion)) return Rejected("deletion_blocked");
            if (actions.Length > MaxActions || actions.Any(action => !IsAllowed(action))) return Rejected("invalid_actions");
            var text = root.GetProperty("text").GetString()!.Trim();
            return text.Length == 0 && actions.Length == 0 ? null : new ChatResponse
            {
                Text = text, Actions = actions.Select(action => action.Clone()).ToArray()
            };
        }
        catch (JsonException ex)
        {
            throw new ChatException("The model returned invalid JSON.", ex);
        }
    }

    private static ChatResponse Rejected(string warning) => new() { Text = "", Actions = [], Warning = warning };

    private static bool IsDeletion(JsonElement action) => action.ValueKind == JsonValueKind.Object &&
        action.EnumerateObject().Any(property => property.Name == "type" && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "clickButton") &&
        action.EnumerateObject().Any(property => property.Name == "id" && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "deleteCollectionBtn");

    public static bool IsAllowed(JsonElement action)
    {
        if (action.ValueKind != JsonValueKind.Object || !action.TryGetProperty("type", out var typeValue) ||
            typeValue.ValueKind != JsonValueKind.String || !ActionFields.TryGetValue(typeValue.GetString()!, out var fields) ||
            !HasFields(action, ["type", .. fields])) return false;
        var type = typeValue.GetString();
        foreach (var field in fields)
        {
            var value = action.GetProperty(field);
            if (field == "value" && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
                double.IsFinite(number) && Math.Abs(number) <= MaxSafeNumber) continue;
            if (value.ValueKind != JsonValueKind.String || (field is not ("text" or "value") && string.IsNullOrWhiteSpace(value.GetString())))
                return false;
        }
        return type switch
        {
            "clickButton" => ButtonIds.Contains(action.GetProperty("id").GetString()!),
            "fillInput" => InputIds.Contains(action.GetProperty("id").GetString()!),
            "switchLanguage" => Languages.Contains(action.GetProperty("lang").GetString()),
            "navigate" => IsLocalPath(action.GetProperty("path").GetString()!),
            _ => true
        };
    }

    private static bool HasFields(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == fields.Length && names.Distinct(StringComparer.Ordinal).Count() == fields.Length &&
            fields.All(field => names.Contains(field, StringComparer.Ordinal));
    }

    private static bool IsLocalPath(string path) => path.StartsWith('/') && !path.StartsWith("//") &&
        !path.Contains('\\') && !path.Any(character => character < 32 || character == 127);
}
