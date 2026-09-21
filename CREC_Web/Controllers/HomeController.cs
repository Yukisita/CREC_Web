/*
CREC Web - Home Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index([FromServices] CREC_Web.Services.ProjectRuntime runtime)
        {
            return runtime.Current.HasProject ? View() : View("SelectProject");
        }
    }
}
