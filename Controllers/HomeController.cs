using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RadioStation.Data;
using RadioStation.Models;
using System.Diagnostics;

namespace RadioStation.Controllers
{
	public class HomeController : Controller
	{
		private readonly ApplicationDbContext _context;
        public HomeController(ApplicationDbContext context)
        {
			_context = context;
        }
        public IActionResult Index(int id=0)
		{
			
			if(id != 0)
			{
				var name = _context.Users.Find(id);
				HttpContext.Session.SetInt32("userId", id);
				HttpContext.Session.SetString("userName", name.Name);
			}
			var radioList=_context.Radios.ToList();
			return View(radioList);
		}

		public IActionResult Add()
        {
            return View();
        }
		[HttpPost]

		public IActionResult Add(Radio radio)
		{

			_context.Radios.Add(radio);
			_context.SaveChanges();
			return RedirectToAction("Index", "Home");
		}
		public IActionResult Update(int id)
		{
			var radio = _context.Radios.FirstOrDefault(x => x.RadioId == id);
			return View(radio);
		}
		[HttpPost]

        public IActionResult Update(Radio radio)
        {

            _context.Update(radio);
			_context.SaveChanges();
			return RedirectToAction("Index", "Home");
        }
		public IActionResult Delete(int id)
		{
			var radio = _context.Radios.Find(id);
			_context.Remove(radio);
			_context.SaveChanges();
			return RedirectToAction("Index", "Home");
		}
		public IActionResult Login()
		{
			return View();
		}
		[HttpPost]

        public IActionResult Login(User user)
        {
            var tempUser = _context.Users.FirstOrDefault(x => x.Email == user.Email);
            if (tempUser != null)
            {
                if (user.Password == tempUser.Password)
                {

                    return RedirectToAction("Index","Home", new {id=tempUser.UserId});
                }
            }
            return RedirectToAction("Login");
        }

		public IActionResult Logout()
		{
			HttpContext.Session.Clear();
			return RedirectToAction("Index", "Home");
		}

		[HttpPost]

		public IActionResult ToggleFavorite(int radioId,bool isFavorite)
		{
            int userId = Convert.ToInt32(HttpContext.Session.GetInt32("userId"));
			if (isFavorite)
			{
				var user=_context.Users.Include(x => x.FavoriteRadios).FirstOrDefault(o => o.UserId == userId);
				var radio = _context.Radios.Find(radioId);
				user.FavoriteRadios.Add(radio);
				_context.SaveChanges();
			}
			else
			{
                var user = _context.Users.Include(x => x.FavoriteRadios).FirstOrDefault(o => o.UserId == userId);
                var radio = _context.Radios.Find(radioId);
                user.FavoriteRadios.Remove(radio);
            }
            return Json(new { success = true });
        }

		public IActionResult Favourite(int id)
		{
			var favourite=_context.Users.Include(x=>x.FavoriteRadios).FirstOrDefault(o=>o.UserId==id);
			return View(favourite.FavoriteRadios.ToList());
		}

		public IActionResult Remove(int id)
		{
			var userId=HttpContext.Session.GetInt32("userId");
			var user=_context.Users.Include(x => x.FavoriteRadios).FirstOrDefault(o => o.UserId == userId);
			var radio = _context.Radios.Find(id);
			user.FavoriteRadios.Remove(radio);
			_context.SaveChanges();
			return RedirectToAction("Favourite", "Home", new {id=userId});
		}

    }
}