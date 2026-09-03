namespace BadmintoBooker.Models
{
    public class Slot
    {
        public string ActivityId { get; set; }
        public string ActivityGroupId { get; set; }
        public string LocationId { get; set; }
        public string SiteId { get; set; }
        public TimeOnly LocalStart { get; set; }
        public TimeOnly LocalEnd { get; set; }
    }
}
