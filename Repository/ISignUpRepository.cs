using RadioStation.Models;

namespace RadioStation.Repository
{
    public interface ISignUpRepository
    {
        void Add(User user);
        bool GetCodeResult(int id,int code);
        void Save();
    }
}
