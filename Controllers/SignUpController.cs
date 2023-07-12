using Microsoft.AspNetCore.Mvc;
using RadioStation.Data;
using RadioStation.Models;
using RadioStation.Repository;

namespace RadioStation.Controllers
{
	public class SignUpController : Controller
	{
        public SignUpController(ISignUpRepository services,ApplicationDbContext context)
        {
            _services=services;
			_context=context;
        }
        private int _userCode;
        private readonly ISignUpRepository _services;
		private readonly ApplicationDbContext _context;

        public IActionResult Index()
		{
			return View();
		}
		[HttpPost]
		[AutoValidateAntiforgeryToken]
		public IActionResult Index(SignUp user)
		{

			if (!ModelState.IsValid)
			{
				// check if the error is related to the ConfirmPassword field
				var confirmPassError = ModelState["ConfirmPassword"].Errors.FirstOrDefault();

				if (confirmPassError != null)
				{
					// add a custom error message for the ConfirmPassword field
					ModelState.AddModelError("ConfirmPassword", "The password and confirmation password do not match.");
				}

				// return the view with the updated model state
				return View(user);
			}
			else
			{
				  var check=_context.Users.FirstOrDefault(x=>x.Email == user.Email);
				if(check != null)
				{
                    ModelState.AddModelError("user", "The email is already used, please login");
                    return View(user);
				}
				
					var TempUser = new User
					{
						Name = user.Name,
						Email = user.Email,
						Password = user.Password,
						EmailConfirmed = new Random().Next(1000,9999),
						
						
					};
					var res = new Mailer().Send(user.Email.ToString(), TempUser.EmailConfirmed);
					_services.Add(TempUser);
					_services.Save();
					_userCode = TempUser.EmailConfirmed;
					return RedirectToAction("Index", "Check", new {id=TempUser.UserId});
				
			}
		}
 
	}
}
