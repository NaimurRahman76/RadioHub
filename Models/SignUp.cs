using System.ComponentModel.DataAnnotations;

namespace RadioStation.Models
{
	public class SignUp
	{
		
		[Required]
		public string Name { get; set; } = string.Empty;
		[Required]
		public string Email { get; set; } = string.Empty;
		[Required]
		[DataType(DataType.Password)]
		public string Password { get; set; } = string.Empty;

		[Required]
		[Compare(nameof(Password), ErrorMessage = "The password and confirmation password do not match.")]
		[DataType(DataType.Password)]
		public string ConfirmPassword { get; set; } = string.Empty;
	}
}
