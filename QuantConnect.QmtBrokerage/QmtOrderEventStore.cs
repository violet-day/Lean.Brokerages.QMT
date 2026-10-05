using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using QuantConnect.Configuration;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Persists completed strategy entry events so their investment time survives a restart.
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

        internal static void RecordInvestedAt(
            string accountId,
            Symbol symbol,
            DateTime investedAt)
        {
            RecordInvestedAt(FilePath, accountId, symbol, investedAt);
        }

        internal static void RecordInvestedAt(
            string filePath,
            string accountId,
            Symbol symbol,
            DateTime investedAt)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new ArgumentException("QMT account ID is required.", nameof(accountId));
            }
            if (symbol == null)
            {
                throw new ArgumentNullException(nameof(symbol));
            }

            var directoryPath = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
            var record = new InvestmentRecord
            {
                AccountId = accountId.Trim(),
                Symbol = symbol.Value.ToUpperInvariant(),
                InvestedAt = investedAt.ToString("O", CultureInfo.InvariantCulture)
            };
            var line = JsonConvert.SerializeObject(record) + Environment.NewLine;
            lock (FileLock)
            {
                File.AppendAllText(filePath, line);
            }
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

            DateTime? investedAt = null;
            lock (FileLock)
            {
                foreach (var line in File.ReadLines(filePath))
                {
                    InvestmentRecord? record;
                    try
                    {
                        record = JsonConvert.DeserializeObject<InvestmentRecord>(line);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    if (record == null ||
                        !string.Equals(record.AccountId, accountId, StringComparison.Ordinal) ||
                        !string.Equals(record.Symbol, symbol.Value, StringComparison.OrdinalIgnoreCase) ||
                        !DateTime.TryParseExact(
                            record.InvestedAt,
                            "O",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var recordedInvestedAt))
                    {
                        continue;
                    }
                    investedAt = recordedInvestedAt;
                }
            }
            return investedAt;
        }

        private sealed class InvestmentRecord
        {
            [JsonProperty("account_id")]
            public string AccountId { get; set; } = string.Empty;

            [JsonProperty("symbol")]
            public string Symbol { get; set; } = string.Empty;

            [JsonProperty("invested_at")]
            public string InvestedAt { get; set; } = string.Empty;
        }
    }
}
