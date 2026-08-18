using System;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Selects account-specific QMT order-session behavior.
    /// </summary>
    public enum QmtTradingEnvironment
    {
        Live,
        Simulation
    }

    /// <summary>
    /// Parses QMT trading environments and validates their order sessions.
    /// </summary>
    public static class QmtTradingEnvironmentResolver
    {
        public const string LiveConfigurationValue = "live";
        public const string SimulationConfigurationValue = "simulation";

        public static bool TryParse(string? value, out QmtTradingEnvironment tradingEnvironment)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case LiveConfigurationValue:
                    tradingEnvironment = QmtTradingEnvironment.Live;
                    return true;
                case SimulationConfigurationValue:
                    tradingEnvironment = QmtTradingEnvironment.Simulation;
                    return true;
                default:
                    tradingEnvironment = default;
                    return false;
            }
        }

        public static string GetConfigurationValue(QmtTradingEnvironment tradingEnvironment)
        {
            return tradingEnvironment switch
            {
                QmtTradingEnvironment.Live => LiveConfigurationValue,
                QmtTradingEnvironment.Simulation => SimulationConfigurationValue,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(tradingEnvironment),
                    tradingEnvironment,
                    "Unsupported QMT trading environment.")
            };
        }

        public static bool IsOrderSubmissionAllowed(
            QmtTradingEnvironment tradingEnvironment,
            DateTime utcTime)
        {
            if (tradingEnvironment == QmtTradingEnvironment.Live)
            {
                return true;
            }
            if (tradingEnvironment != QmtTradingEnvironment.Simulation)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tradingEnvironment),
                    tradingEnvironment,
                    "Unsupported QMT trading environment.");
            }

            var chinaTime = utcTime.ConvertFromUtc(TimeZones.Shanghai);
            return chinaTime.DayOfWeek != DayOfWeek.Saturday &&
                chinaTime.DayOfWeek != DayOfWeek.Sunday &&
                chinaTime.TimeOfDay >= TimeSpan.FromHours(10) &&
                chinaTime.TimeOfDay < TimeSpan.FromHours(17);
        }
    }
}
