using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtMapFileProviderTests
    {
        private string _temporaryDataFolder = null!;

        [SetUp]
        public void SetUp()
        {
            _temporaryDataFolder = Path.Combine(
                Path.GetTempPath(),
                $"qmt-map-file-provider-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_temporaryDataFolder);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_temporaryDataFolder, recursive: true);
        }

        [Test]
        public void CreatesPermanentMappingsFromMinuteAndDailyData()
        {
            CreateMinuteTickerDirectory("600000");
            CreateMinuteTickerDirectory("000001");
            CreateDailyTickerFile("000001");
            CreateDailyTickerFile("430047");
            CreateMinuteTickerDirectory("600000.SH");
            CreateDailyTickerFile("invalid");

            var resolver = QmtMapFileProvider.CreateChinaEquityMapFileResolver(
                _temporaryDataFolder);

            Assert.That(resolver.Count(), Is.EqualTo(3));
            foreach (var ticker in new[] { "000001", "430047", "600000" })
            {
                var mapFile = resolver.ResolveMapFile(ticker, new DateTime(2026, 9, 24));
                Assert.Multiple(() =>
                {
                    Assert.That(mapFile.Permtick, Is.EqualTo(ticker.ToUpperInvariant()));
                    Assert.That(mapFile.FirstDate, Is.EqualTo(QmtMarket.CalendarCoverageStart));
                    Assert.That(mapFile.DelistingDate, Is.EqualTo(DateTime.MaxValue.Date));
                    Assert.That(mapFile.HasData(DateTime.MaxValue.Date), Is.True);
                    Assert.That(
                        mapFile.GetMappedSymbol(new DateTime(2026, 9, 24), $"{ticker}.SH"),
                        Is.EqualTo(ticker));
                });
            }
        }

        [Test]
        public void EmptyChinaDataKeepsUnknownSymbolsActive()
        {
            var resolver = QmtMapFileProvider.CreateChinaEquityMapFileResolver(
                _temporaryDataFolder);
            var mapFile = resolver.ResolveMapFile("600000", new DateTime(2026, 9, 24));

            Assert.Multiple(() =>
            {
                Assert.That(resolver, Is.Empty);
                Assert.That(mapFile, Is.Empty);
                Assert.That(mapFile.HasData(DateTime.MaxValue.Date), Is.True);
            });
        }

        [Test]
        public void CachesChinaResolverAndDelegatesOtherMarkets()
        {
            CreateMinuteTickerDirectory("600000");
            var delegatedProvider = new RecordingMapFileProvider();
            var provider = new QmtMapFileProvider(
                delegatedProvider,
                () => _temporaryDataFolder);
            var dataProvider = new DefaultDataProvider();
            provider.Initialize(dataProvider);

            var chinaKey = new AuxiliaryDataKey(QmtMarket.Name, SecurityType.Equity);
            var firstChinaResolver = provider.Get(chinaKey);
            var secondChinaResolver = provider.Get(chinaKey);
            var usaKey = new AuxiliaryDataKey(Market.USA, SecurityType.Equity);
            var delegatedResolver = provider.Get(usaKey);

            Assert.Multiple(() =>
            {
                Assert.That(firstChinaResolver, Is.SameAs(secondChinaResolver));
                Assert.That(firstChinaResolver.Count(), Is.EqualTo(1));
                Assert.That(delegatedProvider.InitializedDataProvider, Is.SameAs(dataProvider));
                Assert.That(delegatedProvider.RequestedKeys, Is.EqualTo(new[] { usaKey }));
                Assert.That(delegatedResolver, Is.SameAs(MapFileResolver.Empty));
            });
        }

        [Test]
        public void ComposerResolvesProviderByFullTypeName()
        {
            var provider = Composer.Instance.GetExportedValueByTypeName<IMapFileProvider>(
                typeof(QmtMapFileProvider).FullName!);

            Assert.That(provider, Is.TypeOf<QmtMapFileProvider>());
        }

        private void CreateMinuteTickerDirectory(string ticker)
        {
            Directory.CreateDirectory(Path.Combine(
                _temporaryDataFolder,
                "equity",
                QmtMarket.Name,
                "minute",
                ticker));
        }

        private void CreateDailyTickerFile(string ticker)
        {
            var dailyDataRoot = Path.Combine(
                _temporaryDataFolder,
                "equity",
                QmtMarket.Name,
                "daily");
            Directory.CreateDirectory(dailyDataRoot);
            File.WriteAllText(Path.Combine(dailyDataRoot, $"{ticker}.zip"), string.Empty);
        }

        private sealed class RecordingMapFileProvider : IMapFileProvider
        {
            public IDataProvider? InitializedDataProvider { get; private set; }
            public List<AuxiliaryDataKey> RequestedKeys { get; } = new List<AuxiliaryDataKey>();

            public void Initialize(IDataProvider dataProvider)
            {
                InitializedDataProvider = dataProvider;
            }

            public MapFileResolver Get(AuxiliaryDataKey auxiliaryDataKey)
            {
                RequestedKeys.Add(auxiliaryDataKey);
                return MapFileResolver.Empty;
            }
        }
    }
}
