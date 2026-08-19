using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.ReadOnly
{
    [TestFixture]
    [Explicit("Requires a running real QMT Gateway and QMT_E2E_ACCOUNT_ID.")]
    [Category(QmtE2ETestCategories.ReadOnly)]
    [NonParallelizable]
    public class QmtReadOnlyE2ETests
    {
        private static readonly object EvidenceLogLock = new object();
        private const string StockCode = "600000.SH";
        private readonly QmtSymbolMapper _symbolMapper = new QmtSymbolMapper();
        private QmtGatewayClient _gatewayClient = null!;
        private QmtBrokerage _brokerage = null!;

        [SetUp]
        public void Connect()
        {
            const string stage = "connect";
            WriteCurrentTask();
            WriteEvidence(stage, "start");
            try
            {
                var accountId = Environment.GetEnvironmentVariable("QMT_E2E_ACCOUNT_ID");
                Assert.That(accountId, Is.Not.Null.And.Not.Empty);

                var gatewayHost = Environment.GetEnvironmentVariable("QMT_E2E_GATEWAY_HOST") ?? "127.0.0.1";
                var gatewayPortText = Environment.GetEnvironmentVariable("QMT_E2E_GATEWAY_PORT") ?? "17890";
                Assert.That(int.TryParse(gatewayPortText, out var gatewayPort), Is.True);
                var dataFolder = Environment.GetEnvironmentVariable("QMT_E2E_DATA_FOLDER");
                Assert.That(dataFolder, Is.Not.Null.And.Not.Empty);

                Environment.CurrentDirectory = TestContext.CurrentContext.TestDirectory;
                Config.Reset();
                Config.Set("data-folder", dataFolder);
                Config.Set("data-directory", dataFolder);
                Globals.Reset();
                MarketHoursDatabase.Reset();
                SymbolPropertiesDatabase.Reset();

                _gatewayClient = new QmtGatewayClient(
                    gatewayHost,
                    gatewayPort,
                    accountId!,
                    TimeSpan.FromSeconds(10));
                var algorithm = new QCAlgorithm();
                _brokerage = new QmtBrokerage(
                    _gatewayClient,
                    algorithm.Transactions);
                _brokerage.Connect();

                Assert.That(_brokerage.IsConnected, Is.True);
                Assert.That(_gatewayClient.ServerInformation?.AccountId, Is.EqualTo(accountId));
                Assert.That(
                    _brokerage.AccountProperties.IsSimulation,
                    Is.True,
                    "The connected QMT runtime is not identified as the simulation account.");
                WriteEvidence(stage, "ok", "account_match=true is_simulation=true");
            }
            catch (Exception exception)
            {
                WriteFailure(stage, exception);
                throw;
            }
        }

        [TearDown]
        public void Disconnect()
        {
            _brokerage?.Dispose();
        }

        [Test]
        public void ReturnsCashHoldingsAndOpenOrders()
        {
            RunCase("account", () =>
            {
                var cashBalances = _brokerage.GetCashBalance();
                var holdings = _brokerage.GetAccountHoldings();
                var openOrders = _brokerage.GetOpenOrders();
                Assert.That(cashBalances.Count(cash => cash.Currency == "CNY"), Is.EqualTo(1));
                Assert.That(holdings, Is.Not.Null);
                Assert.That(openOrders, Is.Not.Null);
                WriteEvidence("account", "ok",
                    $"cash_accounts={cashBalances.Count} holdings={holdings.Count} open_orders={openOrders.Count}");
            });
        }

        [Test]
        public Task SupportsConcurrentAccountPositionAndOrderQueries()
        {
            return RunCaseAsync("concurrent-queries", async () =>
            {
                var accountRequestTask = _gatewayClient.SendRequestAsync(QmtProtocol.Operations.QueryAccount);
                var positionsRequestTask = _gatewayClient.SendRequestAsync(QmtProtocol.Operations.QueryPositions);
                var ordersRequestTask = _gatewayClient.SendRequestAsync(QmtProtocol.Operations.QueryOrders);
                await Task.WhenAll(accountRequestTask, positionsRequestTask, ordersRequestTask);
                Assert.That(accountRequestTask.Result.ToPayload<QmtQueryAccountPayload>().Accounts, Is.Not.Empty);
                Assert.That(positionsRequestTask.Result.ToPayload<QmtQueryPositionsPayload>().Positions, Is.Not.Null);
                Assert.That(ordersRequestTask.Result.ToPayload<QmtQueryOrdersPayload>().Orders, Is.Not.Null);
                WriteEvidence("concurrent-queries", "ok", "account=ok positions=ok orders=ok");
            });
        }

        [Test]
        public void ReturnsOrderedDailyAndMinuteHistory()
        {
            RunCase("history", () =>
            {
                var symbol = _symbolMapper.GetLeanSymbol(
                    StockCode,
                    SecurityType.Equity,
                    QmtSymbolMapper.MarketName);
                var dailyHistory = GetHistory(symbol, Resolution.Daily, TimeSpan.FromDays(15));
                var minuteHistory = GetHistory(symbol, Resolution.Minute, TimeSpan.FromDays(7));
                Assert.That(dailyHistory, Has.Count.GreaterThanOrEqualTo(5));
                Assert.That(minuteHistory, Has.Count.GreaterThanOrEqualTo(5));
                Assert.That(dailyHistory.Select(bar => bar.EndTime), Is.Ordered);
                Assert.That(minuteHistory.Select(bar => bar.EndTime), Is.Ordered);
                WriteEvidence(
                    "history",
                    "ok",
                    $"daily_bars={dailyHistory.Count} minute_bars={minuteHistory.Count} ordered=true");
            });
        }

        [Test]
        public void SubscribesAndUnsubscribesTradeTicks()
        {
            RunCase("subscription", () =>
            {
                var subscriptionConfiguration = CreateTradeSubscriptionConfiguration();
                using var enumerator = _brokerage.Subscribe(
                    subscriptionConfiguration,
                    (_, _) => { });
                Assert.That(enumerator, Is.Not.Null);
                _brokerage.Unsubscribe(subscriptionConfiguration);
                WriteEvidence("subscription", "ok", "subscribed=true unsubscribed=true");
            });
        }

        [Test]
        [Timeout(120000)]
        public void StreamsTradeTicksDuringMarketHours()
        {
            var chinaTime = DateTime.UtcNow.ConvertFromUtc(TimeZones.Shanghai);
            if (!IsChinaMarketOpen(chinaTime))
            {
                WriteEvidence("case", "skipped", "operation=live-data reason=market_closed");
                Assert.Ignore("Requires an open China A-share market session.");
            }

            RunCase("live-data", () =>
            {
                var subscriptionConfiguration = CreateTradeSubscriptionConfiguration();
                using var dataAvailable = new ManualResetEventSlim(false);
                using var enumerator = _brokerage.Subscribe(
                    subscriptionConfiguration,
                    (_, _) => dataAvailable.Set());
                try
                {
                    Assert.That(enumerator, Is.Not.Null);
                    Assert.That(
                        dataAvailable.Wait(TimeSpan.FromSeconds(90)),
                        Is.True,
                        "QMT did not publish a trade tick within 90 seconds.");
                    Assert.That(enumerator!.MoveNext(), Is.True);
                    Assert.That(enumerator.Current, Is.TypeOf<Tick>());
                    WriteEvidence("live-data", "ok", "tick_received=true");
                }
                finally
                {
                    _brokerage.Unsubscribe(subscriptionConfiguration);
                }
            });
        }

        [Test]
        public void ReconnectsAndQueriesAccount()
        {
            RunCase("connection-reopen", () =>
            {
                _brokerage.Disconnect();
                Assert.That(_brokerage.IsConnected, Is.False);
                _brokerage.Connect();
                Assert.That(_brokerage.IsConnected, Is.True);
                Assert.That(_brokerage.GetCashBalance(), Is.Not.Empty);
                WriteEvidence("connection-reopen", "ok", "account_query=ok");
            });
        }

        private SubscriptionDataConfig CreateTradeSubscriptionConfiguration()
        {
            var symbol = _symbolMapper.GetLeanSymbol(
                StockCode,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            return new SubscriptionDataConfig(
                typeof(Tick),
                symbol,
                Resolution.Tick,
                TimeZones.Shanghai,
                TimeZones.Shanghai,
                false,
                false,
                false,
                false,
                TickType.Trade);
        }

        private System.Collections.Generic.List<BaseData> GetHistory(
            Symbol symbol,
            Resolution resolution,
            TimeSpan duration)
        {
            var endTimeUtc = DateTime.UtcNow;
            var historyRequest = new HistoryRequest(
                endTimeUtc.Subtract(duration),
                endTimeUtc,
                typeof(TradeBar),
                symbol,
                resolution,
                SecurityExchangeHours.AlwaysOpen(TimeZones.Shanghai),
                TimeZones.Shanghai,
                null,
                false,
                false,
                DataNormalizationMode.Raw,
                TickType.Trade);
            return _brokerage.GetHistory(historyRequest).ToList();
        }

        private static bool IsChinaMarketOpen(DateTime chinaTime)
        {
            if (chinaTime.DayOfWeek == DayOfWeek.Saturday ||
                chinaTime.DayOfWeek == DayOfWeek.Sunday)
            {
                return false;
            }

            var timeOfDay = chinaTime.TimeOfDay;
            return (timeOfDay >= TimeSpan.FromHours(9.5) && timeOfDay < TimeSpan.FromHours(11.5)) ||
                (timeOfDay >= TimeSpan.FromHours(13) && timeOfDay < TimeSpan.FromHours(15));
        }

        private static void WriteEvidence(
            string stage,
            string status,
            string details = "")
        {
            var testName = TestContext.CurrentContext.Test.MethodName ?? TestContext.CurrentContext.Test.Name;
            var message = $"[qmt-e2e] stage={stage} status={status} test={testName}";
            if (!string.IsNullOrWhiteSpace(details))
            {
                message += " " + details;
            }
            WriteLog(message);
        }

        private static void WriteCurrentTask()
        {
            var taskPath = Environment.GetEnvironmentVariable("QMT_E2E_TASK_PATH") ??
                "test-readonly > readonly-e2e";
            var className = TestContext.CurrentContext.Test.ClassName?.Split('.').Last() ?? "unknown-class";
            var testName = TestContext.CurrentContext.Test.MethodName ?? TestContext.CurrentContext.Test.Name;
            WriteLog($"[qmt-task] {taskPath} > {className} > {testName}");
        }

        private static void RunCase(string operation, Action testCase)
        {
            WriteEvidence("case", "start", $"operation={operation}");
            try
            {
                testCase();
                WriteEvidence("case-complete", "ok", $"operation={operation}");
            }
            catch (Exception exception)
            {
                WriteFailure("case", exception);
                throw;
            }
        }

        private static async Task RunCaseAsync(string operation, Func<Task> testCase)
        {
            WriteEvidence("case", "start", $"operation={operation}");
            try
            {
                await testCase();
                WriteEvidence("case-complete", "ok", $"operation={operation}");
            }
            catch (Exception exception)
            {
                WriteFailure("case", exception);
                throw;
            }
        }

        private static void WriteLog(string message)
        {
            var line = $"{DateTimeOffset.Now:O} {message}";
            var evidenceLogPath = Environment.GetEnvironmentVariable("QMT_E2E_LOG_PATH");
            if (!string.IsNullOrWhiteSpace(evidenceLogPath))
            {
                lock (EvidenceLogLock)
                {
                    File.AppendAllText(evidenceLogPath, line + Environment.NewLine);
                }
            }
            TestContext.Progress.WriteLine(line);
        }

        private static void WriteFailure(string stage, Exception exception)
        {
            var reason = exception.Message.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
            WriteEvidence(stage, "failed", $"error_type={exception.GetType().Name} reason=\"{reason}\"");
        }
    }
}
