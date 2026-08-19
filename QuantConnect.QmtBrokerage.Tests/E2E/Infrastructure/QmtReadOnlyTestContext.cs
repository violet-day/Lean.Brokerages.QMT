using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure
{
    public sealed class QmtReadOnlyTestContext : IDisposable
    {
        private static readonly object EvidenceLogLock = new object();
        public const string StockCode = "600000.SH";

        public QmtGatewayClient GatewayClient { get; }
        public QmtBrokerage Brokerage { get; }
        public Symbol Symbol { get; }

        private QmtReadOnlyTestContext(
            QmtGatewayClient gatewayClient,
            QmtBrokerage brokerage,
            Symbol symbol)
        {
            GatewayClient = gatewayClient;
            Brokerage = brokerage;
            Symbol = symbol;
        }

        public static QmtReadOnlyTestContext Connect()
        {
            const string stage = "connect";
            QmtReadOnlyTestContext? context = null;
            WriteCurrentTask();
            WriteEvidence(stage, "start");
            try
            {
                var accountId = RequiredEnvironmentVariable("QMT_E2E_ACCOUNT_ID");
                var gatewayHost = Environment.GetEnvironmentVariable("QMT_E2E_GATEWAY_HOST") ?? "127.0.0.1";
                var gatewayPortText = Environment.GetEnvironmentVariable("QMT_E2E_GATEWAY_PORT") ?? "17890";
                Assert.That(int.TryParse(gatewayPortText, out var gatewayPort), Is.True);
                var dataFolder = RequiredEnvironmentVariable("QMT_E2E_DATA_FOLDER");

                Environment.CurrentDirectory = TestContext.CurrentContext.TestDirectory;
                Config.Reset();
                Config.Set("data-folder", dataFolder);
                Config.Set("data-directory", dataFolder);
                Globals.Reset();
                MarketHoursDatabase.Reset();
                SymbolPropertiesDatabase.Reset();
                QmtMarket.RegisterMetadata();

                var gatewayClient = new QmtGatewayClient(
                    gatewayHost,
                    gatewayPort,
                    accountId,
                    TimeSpan.FromSeconds(10));
                var brokerage = new QmtBrokerage(
                    gatewayClient,
                    new QCAlgorithm().Transactions);
                var symbol = new QmtSymbolMapper().GetLeanSymbol(
                    StockCode,
                    SecurityType.Equity,
                    QmtSymbolMapper.MarketName);
                context = new QmtReadOnlyTestContext(gatewayClient, brokerage, symbol);
                brokerage.Connect();

                Assert.That(brokerage.IsConnected, Is.True);
                Assert.That(gatewayClient.ServerInformation?.AccountId, Is.EqualTo(accountId));
                WriteEvidence(
                    stage,
                    "ok",
                    $"account_match=true is_simulation=" +
                    brokerage.AccountProperties.IsSimulation.ToString().ToLowerInvariant());
                return context;
            }
            catch (Exception exception)
            {
                context?.Dispose();
                WriteFailure(stage, exception);
                throw;
            }
        }

        public SubscriptionDataConfig CreateTradeSubscriptionConfiguration()
        {
            return new SubscriptionDataConfig(
                typeof(Tick),
                Symbol,
                Resolution.Tick,
                TimeZones.Shanghai,
                TimeZones.Shanghai,
                false,
                false,
                false,
                false,
                TickType.Trade);
        }

        public List<BaseData> GetHistory(Resolution resolution, TimeSpan duration)
        {
            var endTimeUtc = DateTime.UtcNow;
            var historyRequest = new HistoryRequest(
                endTimeUtc.Subtract(duration),
                endTimeUtc,
                typeof(TradeBar),
                Symbol,
                resolution,
                SecurityExchangeHours.AlwaysOpen(TimeZones.Shanghai),
                TimeZones.Shanghai,
                null,
                false,
                false,
                DataNormalizationMode.Raw,
                TickType.Trade);
            return Brokerage.GetHistory(historyRequest).ToList();
        }

        public bool IsMarketOpen()
        {
            var exchangeHours = MarketHoursDatabase
                .FromDataFolder()
                .GetExchangeHours(Symbol.ID.Market, Symbol, Symbol.SecurityType);
            var localTime = DateTime.UtcNow.ConvertFromUtc(exchangeHours.TimeZone);
            return exchangeHours.IsOpen(localTime, false) &&
                GetHistory(Resolution.Minute, TimeSpan.FromDays(2))
                    .Any(bar => bar.Time.Date == localTime.Date);
        }

        public void Run(string operation, Action testCase)
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

        public async Task RunAsync(string operation, Func<Task> testCase)
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

        public void Skip(string operation, string reason)
        {
            WriteEvidence("case", "skipped", $"operation={operation} reason=\"{reason}\"");
            Assert.Ignore(reason);
        }

        public void WriteStage(string stage, string status, string details = "")
        {
            WriteEvidence(stage, status, details);
        }

        public void Dispose()
        {
            Brokerage.Dispose();
        }

        private static string RequiredEnvironmentVariable(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            Assert.That(value, Is.Not.Null.And.Not.Empty, $"{name} is required.");
            return value!;
        }

        private static void WriteCurrentTask()
        {
            var taskPath = Environment.GetEnvironmentVariable("QMT_E2E_TASK_PATH") ??
                "test-readonly > readonly-e2e";
            var className = TestContext.CurrentContext.Test.ClassName?.Split('.').Last() ?? "unknown-class";
            var testName = TestContext.CurrentContext.Test.MethodName ?? TestContext.CurrentContext.Test.Name;
            WriteLog($"[qmt-task] {taskPath} > {className} > {testName}");
        }

        private static void WriteFailure(string stage, Exception exception)
        {
            var reason = exception.Message.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
            WriteEvidence(stage, "failed", $"error_type={exception.GetType().Name} reason=\"{reason}\"");
        }

        private static void WriteEvidence(string stage, string status, string details = "")
        {
            var testName = TestContext.CurrentContext.Test.MethodName ?? TestContext.CurrentContext.Test.Name;
            var message = $"[qmt-e2e] stage={stage} status={status} test={testName}";
            if (!string.IsNullOrWhiteSpace(details))
            {
                message += " " + details;
            }
            WriteLog(message);
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
    }
}
