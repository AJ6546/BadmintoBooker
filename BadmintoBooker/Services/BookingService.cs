using BadmintoBooker.Config;
using BadmintoBooker.Models;
using BadmintoBooker.Services.Interfaces;
using Microsoft.Playwright;
using System.Text.RegularExpressions;

namespace BadmintoBooker.Services;

public class BookingService : IBookingService
{
    private const int LoginTimeoutMs = 30_000;
    private const int PayTimeoutMs = 30_000;
    private const int CookieBannerTimeoutMs = 4_000;

    private const int BasketPollMs = 500;
    private const int BasketPollAttempts = 15;

    // How long to wait after clicking Book before deciding the lease failed.
    private const int LeasePollMs = 500;
    private const int LeasePollAttempts = 30;

    private readonly IPage page;
    private readonly ILogService log;
    private readonly AppConfig config;

    public BookingService(IPage page, ILogService log, AppConfig config)
    {
        this.page = page;
        this.log = log;
        this.config = config;
    }

    private string LoginUrl => config.BaseUrl.TrimEnd('/') + "/auth/login";
    private string DetailsUrl => config.BaseUrl.TrimEnd('/') + "/book/details";

    public async Task LoginAsync(string user, string pass)
    {
        await page.GotoAsync(LoginUrl);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        try
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Accept" })
                      .First.ClickAsync(new() { Timeout = CookieBannerTimeoutMs });
        }
        catch (TimeoutException)
        {
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

        // The site leaves the Book button enabled even when you're already on
        // the session; it only fails server-side. Catch it here instead.
        if (await page.GetByText("You already have a booking at this time").CountAsync() > 0)
        {
            log.Write("Already have a booking at this time — skipping.");
            return false;
        }

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

        // The page doesn't navigate — it swaps in a "Go to your basket" button.
        // A refused lease shows an untranslated error key instead.
        var goToBasket = page.GetByRole(AriaRole.Button,
            new() { NameRegex = new Regex("go to your basket", RegexOptions.IgnoreCase) });
        var leaseError = page.GetByText("ERRORS.CREATE-LEASE");

        var added = false;

        for (var i = 0; i < LeasePollAttempts; i++)
        {
            if (await goToBasket.CountAsync() > 0) { added = true; break; }

            if (await leaseError.CountAsync() > 0)
                throw new Exception("Server refused the lease (CREATE-LEASE). " +
                                    "Usually means already booked, or a stale basket item.");

            await page.WaitForTimeoutAsync(LeasePollMs);
        }

        if (!added)
            throw new Exception("Book was clicked but the basket link never appeared.");

        log.Write("Added to basket.");

        await goToBasket.ClickAsync();
        await page.WaitForURLAsync("**/book/basket", new() { Timeout = config.NavTimeoutMs });

        var continueBtn = page.Locator("[data-qa-id='continue-to-payment-btn']");
        await continueBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible });

        // Poll, never reload — reloading this page empties the basket server-side.
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
            log.Write("STOPPED before payment (reallyPay = false). Clear the basket manually.");

            if (config.PauseOnCheckout)
            {
                Console.WriteLine("\n>>> Paused on the checkout page. Press Enter to continue...");
                Console.ReadLine();
            }

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
        catch
        {
            
        }
    }

    private string BuildUrl(Slot s, DateOnly date)
    {
        // activityId carries the LOCAL start time; the query string carries UTC.
        // TimeZoneInfo handles the BST/GMT switch so this survives October.
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

        string ToUtc(TimeOnly local, int shaveSeconds)
        {
            var wall = date.ToDateTime(local).AddSeconds(-shaveSeconds);
            var utc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), timeZone);
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        }

        return DetailsUrl +
               $"?activityEndTime={ToUtc(s.LocalEnd, 1)}" +   // sessions end at :59
               $"&activityGroupId={s.ActivityGroupId}" +
               $"&activityId={s.ActivityId}" +
               $"&activityStartTime={ToUtc(s.LocalStart, 0)}" +
               $"&locationId={s.LocationId}" +
               $"&siteId={s.SiteId}";
    }
}