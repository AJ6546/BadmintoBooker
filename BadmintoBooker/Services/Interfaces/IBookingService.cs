using BadmintoBooker.Models;

namespace BadmintoBooker.Services.Interfaces
{
    public interface IBookingService
    {
        public Task LoginAsync(string user, string pass);
        public Task<bool> TryBookAsync(Slot slot, DateOnly date);
        public Task ScreenshotAsync(string dir);
    }
}
