using System.ComponentModel.DataAnnotations;

namespace RadioStation.Models
{
	public class User
	{
		[Key]
		public int UserId { get; set; }
		[Required]
		public string Name { get; set; } = string.Empty;
		[Required]
		public string Email { get; set; } = string.Empty;
		[Required]
		public string Password { get; set; } = string.Empty;
		public int EmailConfirmed { get; set; }
		public ICollection<Radio> FavoriteRadios { get; set; } = new List<Radio>();
	}
}
