/*
CREC Web - Project Edit Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers
{
    public class ProjectEditController : Controller
    {
        [Route("ProjectEdit")]
        public IActionResult Index()
        {
            return View();
        }
    }
}
