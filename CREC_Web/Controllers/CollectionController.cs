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
        [Route("Collection/{id}")]
        public IActionResult Index(string id)
        {
            // Validate and sanitize the collection ID
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("Collection ID is required");
            }

            // Store the ID in ViewData - it will be properly encoded when rendered
            ViewData["CollectionId"] = id;
            return View();
        }
    }
}
