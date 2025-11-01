using Microsoft.EntityFrameworkCore;
using RadioStation.Models;

namespace RadioStation.Data
{
	public class ApplicationDbContext:DbContext
	{
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext>options):base(options)
        {
            
        }
       public DbSet<Radio>Radios { get; set; }
		public DbSet<User> Users { get; set; }
		public DbSet<Song> Songs { get; set; }
		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			modelBuilder.Entity<User>()
		.HasMany<Radio>(u => u.FavoriteRadios)
		.WithMany()
		.UsingEntity(j => j.ToTable("UserFavoriteRadios"));
            modelBuilder.Entity<Radio>();
            base.OnModelCreating(modelBuilder);
        }

    }
}
