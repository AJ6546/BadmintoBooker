using BadmintoBooker.Services.Interfaces;

namespace BadmintoBooker.Services
{
    public class LogService: ILogService
    {
        private readonly string path;

        public LogService(string path)
        {
            this.path = path;

            try { File.WriteAllText(path, string.Empty); }
            catch { }
        }

        public void Write(string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
            Console.WriteLine(line);

            try { File.AppendAllText(path, line + Environment.NewLine); }
            catch { }
        }
    }
}
