using System;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Provides a strategy-facing QMT security-status query.
    /// </summary>
    public static class QmtSecurityStatus
    {
        /// <summary>
        /// Returns whether QMT reports the security as trading, or null when
        /// the QMT provider or status is unavailable.
        /// </summary>
        public static bool? IsTrading(Symbol symbol)
        {
            try
            {
                var provider = Composer.Instance.GetPart<IQmtSecurityStatusProvider>();
                if (provider == null)
                {
                    Log.Error(
                        $"QmtSecurityStatus.IsTrading(): status=unavailable " +
                        $"symbol={symbol?.Value ?? "null"} reason=provider-not-registered");
                    return null;
                }

                return provider.IsSecurityTrading(symbol);
            }
            catch (Exception exception)
            {
                Log.Error(
                    exception,
                    $"QmtSecurityStatus.IsTrading(): status=unavailable symbol={symbol?.Value ?? "null"}");
                return null;
            }
        }
    }
}
