using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RadioStation.Data;
using RadioStation.Models;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace RadioStation.Controllers
{
    public class HomeController : Controller
    {
        private readonly ApplicationDbContext _context;
        public HomeController(ApplicationDbContext context)
        {
            _context = context;
        }
        public IActionResult Index(int id = 0)
        {

            if (id != 0)
            {
                var name = _context.Users.Find(id);
                HttpContext.Session.SetInt32("userId", id);
                HttpContext.Session.SetString("userName", name.Name);
            }
            var radioList = _context.Radios.ToList();
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

                    return RedirectToAction("Index", "Home", new { id = tempUser.UserId });
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

        public IActionResult ToggleFavorite(int radioId, bool isFavorite)
        {
            int userId = Convert.ToInt32(HttpContext.Session.GetInt32("userId"));
            if (isFavorite)
            {
                var user = _context.Users.Include(x => x.FavoriteRadios).FirstOrDefault(o => o.UserId == userId);
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
            var favourite = _context.Users.Include(x => x.FavoriteRadios).FirstOrDefault(o => o.UserId == id);
            return View(favourite.FavoriteRadios.ToList());
        }

        public IActionResult RobotsTxt()
        {
            var content = "User-agent: *\n" +
                         "Allow: /\n\n" +
                         "# Block access to admin areas if they exist\n" +
                         "Disallow: /admin/\n" +
                         "Disallow: /account/\n" +
                         "Disallow: /login\n" +
                         "Disallow: /signup\n\n" +
                         "# Allow sitemap\n" +
                         "Sitemap: " + Request.Scheme + "://" + Request.Host + "/sitemap.xml\n\n" +
                         "# Crawl delay (optional - prevents overwhelming the server)\n" +
                         "Crawl-delay: 1";

            return Content(content, "text/plain", Encoding.UTF8);
        }

        public IActionResult SitemapXml()
        {
            var baseUrl = Request.Scheme + "://" + Request.Host;

            var sitemap = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("urlset",
                    new XAttribute("xmlns", "http://www.sitemaps.org/schemas/sitemap/0.9"),

                    // Home page
                    new XElement("url",
                        new XElement("loc", baseUrl + "/"),
                        new XElement("lastmod", DateTime.UtcNow.ToString("yyyy-MM-dd")),
                        new XElement("changefreq", "daily"),
                        new XElement("priority", "1.0")
                    ),

                    // Privacy page
                    new XElement("url",
                        new XElement("loc", baseUrl + "/Home/Privacy"),
                        new XElement("lastmod", DateTime.UtcNow.ToString("yyyy-MM-dd")),
                        new XElement("changefreq", "monthly"),
                        new XElement("priority", "0.3")
                    ),

                    // Favorites page
                    new XElement("url",
                        new XElement("loc", baseUrl + "/Home/Favourite"),
                        new XElement("lastmod", DateTime.UtcNow.ToString("yyyy-MM-dd")),
                        new XElement("changefreq", "weekly"),
                        new XElement("priority", "0.8")
                    )
                )
            );

            return Content(sitemap.ToString(), "application/xml", Encoding.UTF8);
        }
    }
}