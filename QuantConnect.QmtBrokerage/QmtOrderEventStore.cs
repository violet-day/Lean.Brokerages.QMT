using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using QuantConnect.Configuration;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Persists QMT deals and rebuilds the opening time of the current position.
    /// </summary>
    public static class QmtOrderEventStore
    {
        public const string FilePath = "/Storage/qmt-order-events.jsonl";

        private static readonly object FileLock = new object();

        public static DateTime? GetInvestedAt(Symbol symbol)
        {
            try
            {
                return GetInvestedAt(
                    FilePath,
                    Config.Get("qmt-account-id"),
                    symbol);
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException)
            {
                Log.Error(
                    $"QmtOrderEventStore.GetInvestedAt(): status=failed symbol={symbol.Value} " +
                    $"path={FilePath} error={error.GetType().Name}:{error.Message}");
                return null;
            }
        }

        internal static bool RecordDeal(
            string accountId,
            string dealId,
            Symbol symbol,
            DateTime filledAt,
            decimal fillQuantity)
        {
            return RecordDeal(
                FilePath,
                accountId,
                dealId,
                symbol,
                filledAt,
                fillQuantity);
        }

        internal static bool RecordDeal(
            string filePath,
            string accountId,
            string dealId,
            Symbol symbol,
            DateTime filledAt,
            decimal fillQuantity)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new ArgumentException("QMT account ID is required.", nameof(accountId));
            }
            if (string.IsNullOrWhiteSpace(dealId))
            {
                throw new ArgumentException("QMT deal ID is required.", nameof(dealId));
            }
            if (symbol == null)
            {
                throw new ArgumentNullException(nameof(symbol));
            }
            if (fillQuantity == 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fillQuantity),
                    "QMT fill quantity cannot be zero.");
            }

            var directoryPath = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
            var record = new DealRecord
            {
                AccountId = accountId.Trim(),
                DealId = dealId.Trim(),
                Symbol = symbol.Value.ToUpperInvariant(),
                FilledAt = filledAt.ToString("O", CultureInfo.InvariantCulture),
                FillQuantity = fillQuantity
            };
            var line = JsonConvert.SerializeObject(record) + Environment.NewLine;
            lock (FileLock)
            {
                if (File.Exists(filePath))
                {
                    foreach (var existingLine in File.ReadLines(filePath))
                    {
                        var existingRecord = DeserializeRecord(existingLine);
                        if (existingRecord != null &&
                            string.Equals(
                                existingRecord.AccountId,
                                record.AccountId,
                                StringComparison.Ordinal) &&
                            string.Equals(
                                existingRecord.DealId,
                                record.DealId,
                                StringComparison.Ordinal))
                        {
                            return false;
                        }
                    }
                }
                File.AppendAllText(filePath, line);
            }
            return true;
        }

        internal static DateTime? GetInvestedAt(
            string filePath,
            string accountId,
            Symbol symbol)
        {
            if (string.IsNullOrWhiteSpace(accountId) || symbol == null || !File.Exists(filePath))
            {
                return null;
            }

            var positionQuantity = 0m;
            DateTime? investedAt = null;
            lock (FileLock)
            {
                foreach (var line in File.ReadLines(filePath))
                {
                    var record = DeserializeRecord(line);
                    if (record == null ||
                        !string.Equals(record.AccountId, accountId, StringComparison.Ordinal) ||
                        !string.Equals(record.Symbol, symbol.Value, StringComparison.OrdinalIgnoreCase) ||
                        !DateTime.TryParseExact(
                            record.FilledAt,
                            "O",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var filledAt))
                    {
                        continue;
                    }

                    var quantityAfterFill = positionQuantity + record.FillQuantity;
                    if (positionQuantity == 0m && quantityAfterFill > 0m)
                    {
                        investedAt = filledAt;
                    }
                    positionQuantity = Math.Max(0m, quantityAfterFill);
                    if (positionQuantity == 0m)
                    {
                        investedAt = null;
                    }
                }
            }
            return investedAt;
        }

        private static DealRecord? DeserializeRecord(string line)
        {
            try
            {
                return JsonConvert.DeserializeObject<DealRecord>(line);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private sealed class DealRecord
        {
            [JsonProperty("account_id")]
            public string AccountId { get; set; } = string.Empty;

            [JsonProperty("deal_id")]
            public string DealId { get; set; } = string.Empty;

            [JsonProperty("symbol")]
            public string Symbol { get; set; } = string.Empty;

            [JsonProperty("filled_at")]
            public string FilledAt { get; set; } = string.Empty;

            [JsonProperty("fill_quantity")]
            public decimal FillQuantity { get; set; }
        }
    }
}
