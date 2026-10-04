return args is ["--integration"] ? await IntegrationTest.RunAsync() : await ChatTests.RunAsync();
