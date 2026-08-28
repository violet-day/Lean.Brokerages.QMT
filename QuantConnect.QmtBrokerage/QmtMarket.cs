using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantConnect.Logging;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Registers the China A-share market metadata required by LEAN.
    /// </summary>
    public static class QmtMarket
    {
        public const string Name = "china";
        public const string AccountCurrency = "CNY";
        private const string CalendarResourceName =
            "QuantConnect.Brokerages.Qmt.china-trading-calendar.json";
        private const int MarketIdentifier = 900;
        private static readonly object RegistrationLock = new object();
        private static readonly Lazy<TradingCalendar> Calendar = new Lazy<TradingCalendar>(LoadTradingCalendar);

        public static DateTime CalendarCoverageStart => Calendar.Value.CoverageStart;
        public static DateTime CalendarCoverageEnd => Calendar.Value.CoverageEnd;
        public static string CalendarSource => Calendar.Value.Source;

        public static void RegisterIdentifier()
        {
            if (Market.Encode(Name).HasValue)
            {
                return;
            }

            lock (RegistrationLock)
            {
                if (Market.Encode(Name).HasValue)
                {
                    return;
                }

                Market.Add(Name, MarketIdentifier);
            }
        }

        public static void RegisterMetadata()
        {
            RegisterIdentifier();
            EnsureCalendarCovers(DateTime.UtcNow.ConvertFromUtc(TimeZones.Shanghai).Date);

            lock (RegistrationLock)
            {
                var weekdays = new[]
                {
                    DayOfWeek.Monday,
                    DayOfWeek.Tuesday,
                    DayOfWeek.Wednesday,
                    DayOfWeek.Thursday,
                    DayOfWeek.Friday
                };
                var marketHoursByDay = Enum.GetValues<DayOfWeek>()
                    .ToDictionary(day => day, day => weekdays.Contains(day)
                        ? new LocalMarketHours(
                            day,
                            new MarketHoursSegment(MarketHoursState.Market, new TimeSpan(9, 30, 0), new TimeSpan(11, 30, 0)),
                            new MarketHoursSegment(MarketHoursState.Market, new TimeSpan(13, 0, 0), new TimeSpan(15, 0, 0)))
                        : new LocalMarketHours(day));
                var exchangeHours = new SecurityExchangeHours(
                    TimeZones.Shanghai,
                    Calendar.Value.Holidays,
                    marketHoursByDay,
                    new Dictionary<DateTime, TimeSpan>(),
                    new Dictionary<DateTime, TimeSpan>());

                MarketHoursDatabase.FromDataFolder().SetEntry(Name, null, SecurityType.Equity, exchangeHours);
                SymbolPropertiesDatabase.FromDataFolder().SetEntry(
                    Name,
                    null,
                    SecurityType.Equity,
                    new SymbolProperties(
                        "China A-share equity",
                        AccountCurrency,
                        1m,
                        0.01m,
                        100m,
                        string.Empty));
                Log.Trace(
                    $"QmtMarket.RegisterMetadata(): status=ok market={Name} " +
                    $"calendar_source={CalendarSource} coverage_start={CalendarCoverageStart:yyyy-MM-dd} " +
                    $"coverage_end={CalendarCoverageEnd:yyyy-MM-dd} holidays={Calendar.Value.Holidays.Count}");
            }
        }

        public static bool IsTradingDay(DateTime date)
        {
            EnsureCalendarCovers(date.Date);
            return Calendar.Value.TradingDays.Contains(date.Date);
        }

        public static void EnsureCalendarCovers(DateTime date)
        {
            if (date.Date < CalendarCoverageStart || date.Date > CalendarCoverageEnd)
            {
                throw new InvalidOperationException(
                    $"The QMT China trading calendar does not cover {date:yyyy-MM-dd}; " +
                    $"available range is {CalendarCoverageStart:yyyy-MM-dd} through " +
                    $"{CalendarCoverageEnd:yyyy-MM-dd}. Regenerate the Broker calendar resource.");
            }
        }

        private static TradingCalendar LoadTradingCalendar()
        {
            using var resourceStream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(CalendarResourceName) ??
                throw new InvalidOperationException(
                    $"The embedded QMT trading calendar is missing: {CalendarResourceName}.");
            using var resourceReader = new StreamReader(resourceStream);
            var calendarResource = JsonSerializer.Deserialize<TradingCalendarResource>(
                resourceReader.ReadToEnd()) ??
                throw new InvalidOperationException("The embedded QMT trading calendar is empty.");
            if (!string.Equals(calendarResource.Market, "SH", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The embedded QMT trading calendar market must be SH, not {calendarResource.Market}.");
            }

            var coverageStart = ParseCalendarDate(calendarResource.CoverageStart, "coverage_start");
            var coverageEnd = ParseCalendarDate(calendarResource.CoverageEnd, "coverage_end");
            if (coverageEnd < coverageStart)
            {
                throw new InvalidOperationException(
                    "The embedded QMT trading calendar coverage_end precedes coverage_start.");
            }

            var tradingDays = calendarResource.TradingDays
                .Select(tradingDay => ParseCalendarDate(tradingDay, "trading_days"))
                .ToHashSet();
            if (tradingDays.Count != calendarResource.TradingDays.Count)
            {
                throw new InvalidOperationException(
                    "The embedded QMT trading calendar contains duplicate trading days.");
            }
            if (tradingDays.Count == 0 || tradingDays.Any(tradingDay =>
                tradingDay < coverageStart ||
                tradingDay > coverageEnd ||
                tradingDay.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                throw new InvalidOperationException(
                    "The embedded QMT trading calendar contains invalid trading days.");
            }

            var holidays = new List<DateTime>();
            for (var calendarDate = coverageStart; calendarDate <= coverageEnd; calendarDate = calendarDate.AddDays(1))
            {
                if (calendarDate.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
                    !tradingDays.Contains(calendarDate))
                {
                    holidays.Add(calendarDate);
                }
            }

            return new TradingCalendar(
                calendarResource.Source,
                coverageStart,
                coverageEnd,
                tradingDays,
                holidays);
        }

        private static DateTime ParseCalendarDate(string value, string fieldName)
        {
            if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
            {
                throw new InvalidOperationException(
                    $"The embedded QMT trading calendar has an invalid {fieldName} date: {value}.");
            }
            return parsedDate.Date;
        }

        private sealed class TradingCalendar
        {
            public string Source { get; }
            public DateTime CoverageStart { get; }
            public DateTime CoverageEnd { get; }
            public HashSet<DateTime> TradingDays { get; }
            public IReadOnlyCollection<DateTime> Holidays { get; }

            public TradingCalendar(
                string source,
                DateTime coverageStart,
                DateTime coverageEnd,
                HashSet<DateTime> tradingDays,
                IReadOnlyCollection<DateTime> holidays)
            {
                Source = source;
                CoverageStart = coverageStart;
                CoverageEnd = coverageEnd;
                TradingDays = tradingDays;
                Holidays = holidays;
            }
        }

        private sealed class TradingCalendarResource
        {
            [JsonPropertyName("market")]
            public string Market { get; set; } = string.Empty;

            [JsonPropertyName("source")]
            public string Source { get; set; } = string.Empty;

            [JsonPropertyName("coverage_start")]
            public string CoverageStart { get; set; } = string.Empty;

            [JsonPropertyName("coverage_end")]
            public string CoverageEnd { get; set; } = string.Empty;

            [JsonPropertyName("trading_days")]
            public List<string> TradingDays { get; set; } = new List<string>();
        }
    }
}
