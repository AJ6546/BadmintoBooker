using BadmintoBooker.Models;

namespace BadmintoBooker.Config
{
    public class AppConfig
    {
        public int MaxBookingsPerRun { get; set; }
        public int BookingWindowDays { get; set; }
        public int NavTimeoutMs { get; set; }
        public bool ReallyPay { get; set; }
        public bool Headless { get; set; }
        public bool PauseOnCheckout { get; set; }
        public string BaseUrl { get; set; }
        public List<SlotJson> Slots { get; set; }
    }
}
