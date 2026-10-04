return args switch
{
    ["--integration"] => await IntegrationTest.RunAsync(),
    ["--ai-tools"] => await AiToolsIntegrationTest.RunAsync(),
    _ => await ChatTests.RunAsync()
};
