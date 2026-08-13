using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Registers the China A-share market metadata required by LEAN.
    /// </summary>
    public static class QmtMarket
    {
        public const string Name = "china";
        private const int MarketIdentifier = 900;
        private static readonly object RegistrationLock = new object();

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
                    Array.Empty<DateTime>(),
                    marketHoursByDay,
                    new Dictionary<DateTime, TimeSpan>(),
                    new Dictionary<DateTime, TimeSpan>());

                MarketHoursDatabase.FromDataFolder().SetEntry(Name, null, SecurityType.Equity, exchangeHours);
                SymbolPropertiesDatabase.FromDataFolder().SetEntry(
                    Name,
                    null,
                    SecurityType.Equity,
                    new SymbolProperties("China A-share equity", "CNY", 1m, 0.01m, 1m, string.Empty));
            }
        }
    }
}
