using BadmintoBooker.Config;
using BadmintoBooker.Models;
using BadmintoBooker.Services.Interfaces;
using Microsoft.Playwright;
using System.Text.RegularExpressions;

namespace BadmintoBooker.Services;

public class BookingService: IBookingService
{
    private const int LoginTimeoutMs = 30_000;
    private const int PayTimeoutMs = 30_000;
    private const int CookieBannerTimeoutMs = 4_000;
    private const int BasketPollMs = 500;
    private const int BasketPollAttempts = 15;

    private readonly IPage page;
    private readonly ILogService log;
    private readonly AppConfig config;

    public BookingService(IPage page, ILogService log, AppConfig config)
    {
        this.page = page;
        this.log = log;
        this.config = config;
    }

    private string LoginUrl => config.BaseUrl + "/auth/login";
    private string DetailsUrl => config.BaseUrl + "/book/details";

    public async Task LoginAsync(string user, string pass)
    {
        await page.GotoAsync(LoginUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        try
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Accept" })
                   .First.ClickAsync(new() { Timeout = CookieBannerTimeoutMs });
        }
        catch (TimeoutException ex) 
        {
            log.Write($"{ex}, Timed out");
        }

        await page.GetByPlaceholder("Enter your email").FillAsync(user);
        await page.GetByPlaceholder("Enter your password").FillAsync(pass);
        await page.Locator("[data-qa-id='login-submit-btn']").ClickAsync();

        await page.WaitForURLAsync(u => !u.Contains("/auth/login"),
                                new() { Timeout = LoginTimeoutMs });
        log.Write("Logged in.");
    }

    public async Task<bool> TryBookAsync(Slot slot, DateOnly date)
    {
        var url = BuildUrl(slot, date);
        log.Write($"Navigating: {url}");
        await page.GotoAsync(url);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        if (page.Url.TrimEnd('/').EndsWith("/book"))
            throw new Exception("Redirected to /book — slot parameters not accepted.");

        if (await page.GetByText("Do you already have an account?").CountAsync() > 0)
            throw new Exception("Not logged in — session was lost.");

        var bookBtn = page.Locator("[data-qa-id='add-to-basket-btn']");

        if (await bookBtn.CountAsync() == 0)
        {
            log.Write("No book button — not open yet, or full.");
            return false;
        }

        if (await bookBtn.IsDisabledAsync())
        {
            var label = await bookBtn.GetAttributeAsync("aria-label") ?? "";
            log.Write(label.Contains("£0.00")
                ? "Nothing to book — already booked, or nothing selected."
                : $"Book button disabled: {label}");
            return false;
        }

        var spaces = page.GetByText(new Regex(@"\d+ spaces? available"));
        if (await spaces.CountAsync() > 0)
            log.Write(await spaces.First.TextContentAsync() ?? "");

        await bookBtn.ClickAsync();

        var goToBasket = page.GetByRole(AriaRole.Button,
            new() { NameRegex = new Regex("go to your basket", RegexOptions.IgnoreCase) });
        await goToBasket.WaitForAsync(new() { Timeout = config.NavTimeoutMs });
        log.Write("Added to basket.");

        await goToBasket.ClickAsync();
        await page.WaitForURLAsync("**/book/basket", new() { Timeout = config.NavTimeoutMs });

        var continueBtn = page.Locator("[data-qa-id='continue-to-payment-btn']");
        await continueBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible });

        for (var i = 0; i < BasketPollAttempts && !await continueBtn.IsEnabledAsync(); i++)
            await page.WaitForTimeoutAsync(BasketPollMs);

        if (!await continueBtn.IsEnabledAsync())
            throw new Exception(
                $"Basket never priced up: {await continueBtn.GetAttributeAsync("aria-label")}");

        log.Write($"Basket ready: {await continueBtn.GetAttributeAsync("aria-label")}");

        await continueBtn.ClickAsync();
        await page.WaitForURLAsync("**/book/checkout", new() { Timeout = config.NavTimeoutMs });
        log.Write("At checkout.");

        if (!config.ReallyPay)
        {
            log.Write("STOPPED before payment (reallyPay = false).");
            Console.WriteLine("\n>>> Paused on the checkout page. Press Enter to continue...");
            Console.ReadLine();
            return false;
        }

        await page.Locator("[data-qa-id='submit-price-btn']").ClickAsync();
        await page.WaitForURLAsync("**/book/success**", new() { Timeout = PayTimeoutMs });

        log.Write($"BOOKED. {new Uri(page.Url).Query}");
        return true;
    }

    public async Task ScreenshotAsync(string dir)
    {
        try
        {
            var path = Path.Combine(dir, $"error_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await page.ScreenshotAsync(new() { Path = path, FullPage = true });
            log.Write($"Screenshot: {Path.GetFileName(path)}");
        }
        catch { }
    }

    private string BuildUrl(Slot s, DateOnly date)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

        string ToUtc(TimeOnly local, int shaveSeconds)
        {
            var wall = date.ToDateTime(local).AddSeconds(-shaveSeconds);
            var utc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), timeZone);
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        }

        return DetailsUrl +
               $"?activityEndTime={ToUtc(s.LocalEnd, 1)}" +
               $"&activityGroupId={s.ActivityGroupId}" +
               $"&activityId={s.ActivityId}" +
               $"&activityStartTime={ToUtc(s.LocalStart, 0)}" +
               $"&locationId={s.LocationId}" +
               $"&siteId={s.SiteId}";
    }
}