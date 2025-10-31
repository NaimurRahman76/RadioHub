using RadioStation.Data;
using RadioStation.Models;

namespace RadioStation.Repository
{
    public class SignUpRepository : ISignUpRepository
    {
        private readonly ApplicationDbContext _db;

        public SignUpRepository(ApplicationDbContext db)
        {
            _db=db;
        }
        public void Add(User user)
        {
            _db.Users.Add(user);
        }

        public void Save()
        {
            _db.SaveChanges();
        }

        public bool GetCodeResult(int id,int code) 
        {
            var res = _db.Users.Find(id);
            return res?.EmailConfirmed == code; 
        }
    }
}
