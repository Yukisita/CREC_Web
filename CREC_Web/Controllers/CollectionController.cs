/*
CREC Web - Collection Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers
{
    public class CollectionController : Controller
    {
        public IActionResult Index(string id)
        {
            ViewData["CollectionId"] = id;
            return View();
        }
    }
}
