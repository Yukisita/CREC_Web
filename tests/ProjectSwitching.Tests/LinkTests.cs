using System.Diagnostics;
using CREC_Web.Services;

internal static class LinkTests
{
    public static async Task Run(string fixture, string root, string project)
    {
        async Task MakeDirectoryLink(string link, string target)
        {
            if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
            // Junction creation needs no administrator privileges on Windows.
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''")
                + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception("Failed to create test junction");
        }
        var catalog = new ProjectCatalogService(root);
        var link = Path.Combine(root, "linked-projects");
        await MakeDirectoryLink(link, fixture);
        try
        {
            if (!catalog.List(project).Projects.Any(p => p.Location == "linked-projects" && p.ErrorCode == "projects-link"))
                throw new Exception("Linked candidate directory was not disabled");
            try { catalog.Validate(Path.Combine(link, "external.crec")); throw new Exception("Link path accepted"); }
            catch (ProjectAccessException ex) when (ex.Code == "projects-link") { }
            Console.WriteLine("PASS: linked candidate and junction traversal rejected without following cycle");
        }
        finally { Directory.Delete(link); }

        var dataLink = Path.Combine(root, "a", "linked-data");
        await MakeDirectoryLink(dataLink, fixture);
        try
        {
            try { catalog.Validate(project); throw new Exception("Linked data accepted"); }
            catch (ProjectAccessException ex) when (ex.Code == "projects-link") { }
            Console.WriteLine("PASS: data subtree junction rejected");
        }
        finally { Directory.Delete(dataLink); }

        var rootLink = Path.Combine(fixture, "linked-root");
        await MakeDirectoryLink(rootLink, root);
        try
        {
            if (new ProjectCatalogService(rootLink).List(project).ErrorCode != "projects-link")
                throw new Exception("Linked Projects root accepted");
            Console.WriteLine("PASS: linked Projects root rejected");
        }
        finally { Directory.Delete(rootLink); }
    }
}
