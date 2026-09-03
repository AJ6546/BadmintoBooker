using BadmintoBooker.Config;
using BadmintoBooker.Models;
using BadmintoBooker.Services;
using BadmintoBooker.Services.Interfaces;
using Microsoft.Playwright;
using System.Text.Json;

namespace BadmintoBooker;

internal static class Program
{
    private static async Task<int> Main()
    {
        var dir = ProjectDir();
        ILogService log = new LogService(Path.Combine(dir, "booking.log"));

        try
        {
            var config = LoadConfig(dir);
            var slots = ParseSlots(config);

            log.Write($"Loaded {slots.Length} slot(s). " +
                      $"ReallyPay={config.ReallyPay}, Max={config.MaxBookingsPerRun}");

            var (email, password) = ReadCredentials();

            return await RunAsync(slots, config, email, password, dir, log);
        }
        catch (Exception ex)
        {
            log.Write($"STARTUP FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.WriteLine("\nPress Enter to close...");
            Console.ReadLine();
        }
    }

    private static async Task<int> RunAsync(
        (DayOfWeek Day, Slot Slot)[] slots,
        AppConfig config,
        string email,
        string password,
        string dir,
        ILogService log)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = config.Headless });
        await using var context = await browser.NewContextAsync();

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(config.NavTimeoutMs);

        IBookingService booking = new BookingService(page, log, config);

        try
        {
            await booking.LoginAsync(email, password);

            // Furthest-out session first, so the newly-released slot is taken
            // before earlier ones. Earlier days still get a try, which lets a
            // missed run catch up.
            var candidates = slots
                .Select(s => (s.Slot, Date: FurthestBookable(s.Day, config)))
                .OrderByDescending(x => x.Date);

            var booked = 0;

            foreach (var (slot, date) in candidates)
            {
                if (booked >= config.MaxBookingsPerRun)
                {
                    log.Write($"Reached limit of {config.MaxBookingsPerRun} booking(s) this run.");
                    break;
                }

                log.Write($"--- {date:dddd dd MMM yyyy} ---");

                try
                {
                    if (await booking.TryBookAsync(slot, date))
                        booked++;
                }
                catch (Exception ex)
                {
                    log.Write($"FAILED: {ex.GetType().Name}: {ex.Message}");
                    await booking.ScreenshotAsync(dir);
                    log.Write("Check the basket manually — an item may still be held.");
                }
            }

            if (booked == 0)
            {
                log.Write("Nothing booked this run.");
                return 1;
            }

            log.Write($"Done — {booked} booking(s) made.");
            return 0;
        }
        catch (Exception ex)
        {
            log.Write($"FATAL: {ex.GetType().Name}: {ex.Message}");
            await booking.ScreenshotAsync(dir);
            return 1;
        }
    }

    // ---- Config ---------------------------------------------------------

    private static AppConfig LoadConfig(string dir)
    {
        var path = Path.Combine(dir, "appsettings.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Missing config file: {path}");

        var config = JsonSerializer.Deserialize<AppConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (config?.Slots == null || config.Slots.Count == 0)
            throw new InvalidOperationException("appsettings.json has no slots defined.");

        if (config.MaxBookingsPerRun < 1)
            throw new InvalidOperationException("maxBookingsPerRun must be at least 1.");

        return config;
    }

    private static (DayOfWeek Day, Slot Slot)[] ParseSlots(AppConfig config)
    {
        return config.Slots.Select(s => (
            Day: Enum.Parse<DayOfWeek>(s.Day, ignoreCase: true),
            Slot: new Slot
            {
                ActivityId = s.ActivityId,
                ActivityGroupId = s.ActivityGroupId,
                LocationId = s.LocationId,
                SiteId = s.SiteId,
                LocalStart = TimeOnly.Parse(s.LocalStart),
                LocalEnd = TimeOnly.Parse(s.LocalEnd)
            }
        )).ToArray();
    }

    private static (string Email, string Password) ReadCredentials()
    {
        var email = Environment.GetEnvironmentVariable("PL_EMAIL")
            ?? throw new InvalidOperationException(
                "PL_EMAIL not set. Run: setx PL_EMAIL \"...\" /M  then restart VS.");

        var password = Environment.GetEnvironmentVariable("PL_PASSWORD")
            ?? throw new InvalidOperationException(
                "PL_PASSWORD not set. Run: setx PL_PASSWORD \"...\" /M  then restart VS.");

        return (email, password);
    }

    // ---- Helpers --------------------------------------------------------

    private static string ProjectDir()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\.."));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Latest date inside the booking window falling on the given weekday.</summary>
    private static DateOnly FurthestBookable(DayOfWeek want, AppConfig config)
    {
        var d = DateOnly.FromDateTime(DateTime.Today.AddDays(config.BookingWindowDays));
        while (d.DayOfWeek != want) d = d.AddDays(-1);
        return d;
    }
}