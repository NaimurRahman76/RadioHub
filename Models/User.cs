using System.ComponentModel.DataAnnotations;

namespace RadioStation.Models
{
	public class User
	{
		[Key]
		public int UserId { get; set; }
		[Required]
		public string Name { get; set; }
		[Required]
		public string Email { get; set; }
		[Required]
		public string Password { get; set; }
		public int EmailConfirmed { get; set; }
		public ICollection<Radio>FavoriteRadios { get; set; }
	}
}
