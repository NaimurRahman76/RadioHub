using Microsoft.AspNetCore.Mvc;
using RadioStation.Repository;

namespace RadioStation.Controllers
{
	public class CheckController : Controller
	{
        private readonly ISignUpRepository _services;

        public CheckController(ISignUpRepository services)
        {
            _services=services;
        }
        public IActionResult Index(int id)
		{
            ViewBag.Id = id;
			return View();
		}
        [HttpPost]
        [AutoValidateAntiforgeryToken]
        public IActionResult Index(int code,int id)
        {
            var result = _services.GetCodeResult(id, code);
            if ( result==true)
            {
                return RedirectToAction("Index", "Home", new {id=id});
            }
             ViewBag.message = "wrong code!!!!!";
             return View("Index" ,id);
        }

    }
}
