/*
CREC Web - Home Controller
Copyright (c) [2025] [S.Yukisita]
This software is released under the MIT License.
*/

using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
