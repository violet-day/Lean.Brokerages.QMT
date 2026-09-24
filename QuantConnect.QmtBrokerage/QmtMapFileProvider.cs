using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Interfaces;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Maps full QMT symbols to the six-digit tickers used by local China equity data.
    /// </summary>
    public sealed class QmtMapFileProvider : IMapFileProvider
    {
        private readonly IMapFileProvider _localDiskMapFileProvider;
        private readonly Lazy<MapFileResolver> _chinaEquityMapFileResolver;

        public QmtMapFileProvider()
            : this(new LocalDiskMapFileProvider(), () => Globals.DataFolder)
        {
        }

        internal QmtMapFileProvider(
            IMapFileProvider localDiskMapFileProvider,
            Func<string> dataFolderProvider)
        {
            _localDiskMapFileProvider = localDiskMapFileProvider;
            _chinaEquityMapFileResolver = new Lazy<MapFileResolver>(() =>
                CreateChinaEquityMapFileResolver(dataFolderProvider()));
        }

        public void Initialize(IDataProvider dataProvider)
        {
            _localDiskMapFileProvider.Initialize(dataProvider);
        }

        public MapFileResolver Get(AuxiliaryDataKey auxiliaryDataKey)
        {
            if (auxiliaryDataKey.SecurityType == SecurityType.Equity &&
                string.Equals(
                    auxiliaryDataKey.Market,
                    QmtMarket.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return _chinaEquityMapFileResolver.Value;
            }

            return _localDiskMapFileProvider.Get(auxiliaryDataKey);
        }

        internal static MapFileResolver CreateChinaEquityMapFileResolver(string dataFolder)
        {
            var chinaEquityDataRoot = Path.Combine(dataFolder, "equity", QmtMarket.Name);
            var discoveredTickers = DiscoverChinaEquityTickers(chinaEquityDataRoot);
            var mapFiles = discoveredTickers.Select(ticker => new MapFile(
                ticker,
                new[]
                {
                    new MapFileRow(QmtMarket.CalendarCoverageStart, ticker),
                    new MapFileRow(DateTime.MaxValue.Date, ticker)
                }));

            Log.Trace(
                $"QmtMapFileProvider.CreateChinaEquityMapFileResolver(): status=ok " +
                $"root={chinaEquityDataRoot} tickers={discoveredTickers.Count} " +
                $"validity_start={QmtMarket.CalendarCoverageStart:yyyy-MM-dd} " +
                $"validity_end={DateTime.MaxValue.Date:yyyy-MM-dd}");
            return new MapFileResolver(mapFiles);
        }

        private static IReadOnlyCollection<string> DiscoverChinaEquityTickers(
            string chinaEquityDataRoot)
        {
            var discoveredTickers = new HashSet<string>(StringComparer.Ordinal);
            var minuteDataRoot = Path.Combine(chinaEquityDataRoot, "minute");
            if (Directory.Exists(minuteDataRoot))
            {
                foreach (var tickerDirectory in Directory.EnumerateDirectories(
                    minuteDataRoot,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    var ticker = Path.GetFileName(tickerDirectory);
                    if (IsSixDigitTicker(ticker))
                    {
                        discoveredTickers.Add(ticker);
                    }
                }
            }

            var dailyDataRoot = Path.Combine(chinaEquityDataRoot, "daily");
            if (Directory.Exists(dailyDataRoot))
            {
                foreach (var dailyDataFile in Directory.EnumerateFiles(
                    dailyDataRoot,
                    "*.zip",
                    SearchOption.TopDirectoryOnly))
                {
                    var ticker = Path.GetFileNameWithoutExtension(dailyDataFile);
                    if (IsSixDigitTicker(ticker))
                    {
                        discoveredTickers.Add(ticker);
                    }
                }
            }

            return discoveredTickers.OrderBy(ticker => ticker, StringComparer.Ordinal).ToArray();
        }

        private static bool IsSixDigitTicker(string ticker)
        {
            return ticker.Length == 6 && ticker.All(char.IsDigit);
        }
    }
}
