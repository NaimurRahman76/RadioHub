using System.ComponentModel.DataAnnotations;

namespace RadioStation.Models
{
	public class SignUp
	{
		
		[Required]
		public string Name { get; set; }
		[Required]
		public string Email { get; set; }
		[Required]
		[DataType(DataType.Password)]
		public string Password { get; set; }

		[Required]
		[Compare(nameof(Password), ErrorMessage = "The password and confirmation password do not match.")]
		[DataType(DataType.Password)]
		public string ConfirmPassword { get; set; }
	}
}
