using System;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Trading behavior reported by the connected QMT account runtime.
    /// </summary>
    public sealed class QmtAccountProperties
    {
        public bool IsSimulation { get; }

        public QmtMarketOrderStyle MarketOrderStyle => IsSimulation
            ? QmtMarketOrderStyle.LatestPrice
            : QmtMarketOrderStyle.FiveLevelImmediateOrCancel;

        public QmtAccountProperties(bool isSimulation)
        {
            IsSimulation = isSimulation;
        }

        public bool IsOrderSubmissionAllowed(DateTime utcTime)
        {
            if (!IsSimulation)
            {
                return true;
            }

            var chinaTime = utcTime.ConvertFromUtc(TimeZones.Shanghai);
            return chinaTime.DayOfWeek != DayOfWeek.Saturday &&
                chinaTime.DayOfWeek != DayOfWeek.Sunday &&
                chinaTime.TimeOfDay >= TimeSpan.FromHours(10) &&
                chinaTime.TimeOfDay < TimeSpan.FromHours(17);
        }
    }
}
