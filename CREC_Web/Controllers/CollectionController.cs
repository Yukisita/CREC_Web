/*
CREC Web - Collection Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using CREC_Web.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers
{
    public class CollectionController : Controller
    {
        [Route("Collection/{collectionId}")]
        public IActionResult Index(string collectionId)
        {
            // セキュリティ: コレクション ID を検証
            if (!ValidationHelper.IsValidCollectionId(collectionId))
            {
                return BadRequest("Collection ID is required");
            }

            // Store the ID in ViewData - it will be properly encoded when rendered
            ViewData["CollectionId"] = collectionId;
            return View();
        }
    }
}
