# Badminton Booker

A small console app that books my regular No Strings badminton sessions at
Places Leisure Camberley, as I stop forgetting.

## Why

The Tuesday and Friday sessions open for booking 8 days ahead, at the
session's own start time — so the Tuesday 20:15 court unlocks at 20:15 on the
Monday before. They're popular, and the slots get booked within a couple of days.

I kept forgetting, remembering two days later, and finding the slots full.
This books it for me.

## Status

Working. Runs on a schedule via Windows Task Scheduler — see
[Scheduling](#scheduling). Can also be run by hand any time.

## What it does

1. Logs in to GladstoneGo (the booking platform Places Leisure uses)
2. Works out the furthest-out bookable date for each configured weekday
3. Goes straight to that session's booking page
4. Adds it to the basket and pays with the saved card
5. Logs what happened, and screenshots the page if something breaks

It books at most one session per run and skips anything already booked.

## Prerequisites

- **.NET 8 runtime** (or the SDK if you're building from source)
- **Windows** — the app uses Windows environment variables and the
  `GMT Standard Time` timezone ID
- A **Places Leisure account with a saved card**. The app can't enter card
  details; it clicks Pay and expects the saved card to go through.

## Setup

### 1. Credentials

Your Places Leisure email and password are read from Windows environment
variables, so they're never in the code or in this repo.

Open **Command Prompt as Administrator** and run:

```
setx PL_EMAIL "you@example.com" /M
setx PL_PASSWORD "your-password" /M
```

`/M` sets them machine-wide, which will matter once this runs under Task
Scheduler as a possibly different user.

**Restart Visual Studio afterwards** — it only reads environment variables at
startup.

To check they took, open a new Command Prompt and run `echo %PL_EMAIL%`.

### 2. Playwright browsers

Playwright needs its own copy of Chromium, roughly 150MB. It installs to
`%USERPROFILE%\AppData\Local\ms-playwright` and is a one-time thing per
machine.

Build the project once, then run this in the Package Manager Console:

```
pwsh bin\Debug\net8.0\playwright.ps1 install chromium
```

If you don't have PowerShell 7, temporarily add this as the first line of
`Main` and run once, then remove it:

```csharp
Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
```

### 3. Configuration

`appsettings.json` lives in the project folder and is copied next to the exe
on every build. **The app reads the copy beside the exe**, not the project
one.

While developing: edit the project copy, then build (F5 does this) so the
change reaches `bin\Debug\net8.0\`. Don't edit the `bin` copy directly — the
next build overwrites it.

```json
{
  "reallyPay": false,
  "maxBookingsPerRun": 1,
  "bookingWindowDays": 8,
  "headless": false,
  "pauseOnCheckout": true,
  "baseUrl": "https://placesleisure.gladstonego.cloud",
  "navTimeoutMs": 20000,
  "slots": [
    {
      "day": "Tuesday",
      "activityId": "19922015SNS0325",
      "activityGroupId": "NOSTRINGS",
      "locationId": "199ZSPH003",
      "siteId": "199",
      "localStart": "20:15",
      "localEnd": "22:15"
    }
  ]
}
```

| Setting | What it does |
|---|---|
| `reallyPay` | `false` stops at the payment page without paying. Leave it false while testing. |
| `maxBookingsPerRun` | How many sessions one run may book. Keep at 1 unless you want it buying two courts in an evening. |
| `bookingWindowDays` | How far ahead the centre releases slots. Currently 8. |
| `headless` | `true` hides the browser window. |
| `pauseOnCheckout` | Waits on the payment page so you can look at it. |

### Finding the slot IDs

These are specific to each session and each court, so they have to come from
the real site:

1. Log in and go to **Book**, search for the activity at your centre
2. Click through to the session you want
3. Copy the values out of the URL

A details URL looks like:

```
/book/details?activityEndTime=2026-09-08T21:14:59Z
             &activityGroupId=NOSTRINGS
             &activityId=19922015SNS0325
             &activityStartTime=2026-09-08T19:15:00Z
             &locationId=199ZSPH003
             &siteId=199
```

`activityId` identifies the recurring class, not the individual session, so
it stays the same week to week — the date lives entirely in the timestamps,
which the app builds itself.

Note that Tuesday and Friday use **different courts**, so they have different
`activityId` and `locationId` values. Don't assume one works for the other.

## Running it

**From Visual Studio:** press F5.

**Without Visual Studio:** build, then run the exe directly.

```
cd bin\Release\net8.0
BadmintoBooker.exe
```

The exe needs the whole `net8.0` folder alongside it — the DLLs,
`appsettings.json`, and the Playwright files. Copying the exe on its own
won't work.

Output goes to the console and to `booking.log`, which sits next to the exe
along with any error screenshots.

With `reallyPay: false` it walks the whole flow and stops at the payment
page. **It leaves an item in the basket when it does this** — clear it
manually on the site, or the next run may not price up correctly.

## Sharing a build

Build in **Release**, then zip `bin\Release\net8.0\`. `appsettings.json` is
in there and can be edited in Notepad after extracting.

Whoever runs it still needs .NET 8, the Playwright browser install, and their
own environment variables set.

## Scheduling

One task with two triggers covers both courts: Monday's run books Tuesday's
slot, Thursday's run books Friday's. Each run books at most one session, and
the app works out which on its own.

### Before you schedule

Set these in `appsettings.json` and rebuild in Release:

```json
  "reallyPay": true,
  "headless": true,
  "pauseOnCheckout": false,
```

A scheduled run has no console to type into, so anything that waits for
input will hang forever. `pauseOnCheckout` must be off, and the
`Console.ReadLine()` at the end of `Main` is wrapped in an
`Environment.UserInteractive` check for the same reason.

### Creating the task

Open **Task Scheduler** and click **Create Task** — not Create Basic Task,
which doesn't expose the options needed.

**General**
- Name: `Badminton booker`
- Select **Run whether user is logged on or not** (keeps the console window
  off your desktop). Leave **Do not store password** unticked, or it won't
  run while you're logged off.
- Tick **Run with highest privileges**
- Configure for: **Windows 10** (the newest option; covers Windows 11)

**Triggers** — add two, both Weekly, recurring every 1 week, at **20:15:30**:
- One ticking **Monday** — books Tuesday's court
- One ticking **Thursday** — books Friday's court

The thirty seconds past the hour is deliberate: slots release exactly on the
minute, and arriving a moment late is safer than a moment early.

**Actions** → New → Start a program
- Program: the full path to `BadmintoBooker.exe`
- **Start in**: the folder containing it, without the exe name. This one
  matters — without it Windows runs the program from `System32` and relative
  paths break.

**Conditions**
- Tick **Wake the computer to run this task**. A sleeping machine simply
  doesn't run it.
- On a laptop, untick **Start the task only if the computer is on AC power**,
  or it silently skips when unplugged.

**Settings**
- Tick **Run task as soon as possible after a scheduled start is missed**

Click OK and enter your Windows password when prompted.

### Testing it

Right-click the task → **Run**. Nothing visible happens. Check `booking.log`
next to the exe a minute later to see whether it logged in and what it found.

Do that first with `reallyPay: false` so a misconfigured task can't spend
money, then flip it and rebuild.

## Still to do

- Some kind of notification when a run fails, so a silent break doesn't cost
  weeks of badminton before I notice.
- Handle the case where payment succeeds but the final page-load check times
  out — right now the log says failed when the booking actually went through.

## Things that will break it

The site is a single-page app and none of this is a supported API, so:

- **Slots move courts.** If the centre reassigns the session, the URL stops
  resolving and the app logs "Redirected to /book". Get the new IDs from a
  real booking URL.
- **Selectors change.** Every button is matched on its `data-qa-id`
  attribute, which is more stable than visible text, but a redesign could
  still change them.
- **The booking window changes.** If the log says "not open yet" when you
  know a slot is live, check `bookingWindowDays`.
- **Never reload the basket page.** A reload empties it server-side. The code
  polls instead; don't be tempted to add a refresh.

## Notes

- Sessions cost money. `reallyPay: true` means the app spends it without
  asking.
- If the log reports a failure but a confirmation email arrives, the payment
  went through and only the final page-load check timed out. Check
  **Bookings** on the site before re-running.
- Nothing is stored between runs — it logs in fresh each time, because the
  auth token expires in about 30 minutes and can't survive until a scheduled
  run.