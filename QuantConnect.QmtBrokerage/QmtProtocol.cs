using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Constants and message helpers for QMT Gateway protocol version 1.
    /// </summary>
    public static class QmtProtocol
    {
        public const int Version = 1;

        public static class MessageTypes
        {
            public const string Request = "request";
            public const string Response = "response";
            public const string Event = "event";
        }

        public static class Operations
        {
            public const string Hello = "hello";
            public const string QueryAccount = "query_account";
            public const string QueryPositions = "query_positions";
            public const string QueryOrders = "query_orders";
            public const string QueryHistory = "query_history";
            public const string PlaceOrder = "place_order";
            public const string CancelOrder = "cancel_order";
            public const string Subscribe = "subscribe";
            public const string Unsubscribe = "unsubscribe";
            public const string Quote = "quote";
            public const string Order = "order";
            public const string Deal = "deal";
            public const string Position = "position";
            public const string Account = "account";
            public const string Connection = "connection";
        }
    }

    /// <summary>
    /// One NDJSON message exchanged with the QMT Gateway.
    /// </summary>
    public sealed class QmtProtocolMessage
    {
        [JsonProperty("protocol_version")]
        public int ProtocolVersion { get; set; } = QmtProtocol.Version;

        [JsonProperty("message_type")]
        public string MessageType { get; set; } = string.Empty;

        [JsonProperty("request_id")]
        public string? RequestId { get; set; }

        [JsonProperty("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonProperty("success")]
        public bool? Success { get; set; }

        [JsonProperty("error_code")]
        public string ErrorCode { get; set; } = string.Empty;

        [JsonProperty("error_message")]
        public string ErrorMessage { get; set; } = string.Empty;

        [JsonProperty("payload")]
        public JObject Payload { get; set; } = new JObject();

        public TPayload ToPayload<TPayload>()
        {
            var payload = Payload.ToObject<TPayload>();
            if (payload == null)
            {
                throw new QmtGatewayProtocolException($"Could not deserialize the '{Operation}' payload as {typeof(TPayload).Name}.");
            }

            return payload;
        }

        public static QmtProtocolMessage CreateRequest(string requestId, string operation, object? payload = null)
        {
            return new QmtProtocolMessage
            {
                MessageType = QmtProtocol.MessageTypes.Request,
                RequestId = requestId,
                Operation = operation,
                Payload = payload == null ? new JObject() : JObject.FromObject(payload)
            };
        }
    }

    public sealed class QmtHelloRequest
    {
        [JsonProperty("account_id")]
        public string AccountId { get; set; } = string.Empty;
    }

    public sealed class QmtHelloPayload
    {
        [JsonProperty("server_name")]
        public string ServerName { get; set; } = string.Empty;

        [JsonProperty("account_id")]
        public string AccountId { get; set; } = string.Empty;
    }

    public sealed class QmtQueryAccountPayload
    {
        [JsonProperty("accounts")]
        public List<QmtAccountSnapshot> Accounts { get; set; } = new List<QmtAccountSnapshot>();
    }

    public sealed class QmtAccountSnapshot
    {
        [JsonProperty("available_cash")]
        public decimal AvailableCash { get; set; }
    }

    public sealed class QmtQueryPositionsPayload
    {
        [JsonProperty("positions")]
        public List<QmtPositionSnapshot> Positions { get; set; } = new List<QmtPositionSnapshot>();
    }

    public sealed class QmtPositionSnapshot
    {
        [JsonProperty("stock_code")]
        public string StockCode { get; set; } = string.Empty;

        [JsonProperty("volume")]
        public decimal Volume { get; set; }

        [JsonProperty("open_price")]
        public decimal OpenPrice { get; set; }

        [JsonProperty("last_price")]
        public decimal LastPrice { get; set; }

        [JsonProperty("market_value")]
        public decimal MarketValue { get; set; }
    }

    public sealed class QmtQueryOrdersPayload
    {
        [JsonProperty("orders")]
        public List<QmtOrderSnapshot> Orders { get; set; } = new List<QmtOrderSnapshot>();
    }

    public sealed class QmtOrderSnapshot
    {
        [JsonProperty("stock_code")]
        public string StockCode { get; set; } = string.Empty;

        [JsonProperty("order_id")]
        public string OrderId { get; set; } = string.Empty;

        [JsonProperty("client_order_id")]
        public string ClientOrderId { get; set; } = string.Empty;

        [JsonProperty("direction")]
        public string Direction { get; set; } = string.Empty;

        [JsonProperty("order_type")]
        public string OrderType { get; set; } = string.Empty;

        [JsonProperty("status")]
        public int Status { get; set; }

        [JsonProperty("original_volume")]
        public decimal OriginalVolume { get; set; }

        [JsonProperty("traded_volume")]
        public decimal TradedVolume { get; set; }

        [JsonProperty("limit_price")]
        public decimal LimitPrice { get; set; }

        [JsonProperty("traded_price")]
        public decimal TradedPrice { get; set; }

        [JsonProperty("remark")]
        public string Remark { get; set; } = string.Empty;
    }

    public sealed class QmtOrderEventPayload
    {
        [JsonProperty("stock_code")]
        public string StockCode { get; set; } = string.Empty;

        [JsonProperty("order_id")]
        public string OrderId { get; set; } = string.Empty;

        [JsonProperty("client_order_id")]
        public string ClientOrderId { get; set; } = string.Empty;

        [JsonProperty("status")]
        public int Status { get; set; }

        [JsonProperty("direction")]
        public string Direction { get; set; } = string.Empty;

        [JsonProperty("order_type")]
        public string OrderType { get; set; } = string.Empty;

        [JsonProperty("original_volume")]
        public decimal OriginalVolume { get; set; }

        [JsonProperty("traded_volume")]
        public decimal TradedVolume { get; set; }

        [JsonProperty("limit_price")]
        public decimal LimitPrice { get; set; }

        [JsonProperty("traded_price")]
        public decimal TradedPrice { get; set; }

        [JsonProperty("remark")]
        public string Remark { get; set; } = string.Empty;

        [JsonProperty("submit_status")]
        public int SubmitStatus { get; set; } = -1;

        [JsonProperty("error_id")]
        public int ErrorId { get; set; }

        [JsonProperty("error_message")]
        public string ErrorMessage { get; set; } = string.Empty;

        [JsonProperty("cancel_information")]
        public string CancelInformation { get; set; } = string.Empty;

        [JsonProperty("time")]
        public string Time { get; set; } = string.Empty;
    }

    public sealed class QmtPlaceOrderRequest
    {
        [JsonProperty("client_order_id")]
        public string ClientOrderId { get; set; } = string.Empty;

        [JsonProperty("stock_code")]
        public string StockCode { get; set; } = string.Empty;

        [JsonProperty("order_type")]
        public string OrderType { get; set; } = string.Empty;

        [JsonProperty("direction")]
        public string Direction { get; set; } = string.Empty;

        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        [JsonProperty("limit_price")]
        public decimal? LimitPrice { get; set; }

        [JsonProperty("strategy_name")]
        public string? StrategyName { get; set; }
    }

    public sealed class QmtPlaceOrderPayload
    {
        [JsonProperty("accepted")]
        public bool Accepted { get; set; }

        [JsonProperty("client_order_id")]
        public string ClientOrderId { get; set; } = string.Empty;

        [JsonProperty("native_order_id")]
        public string NativeOrderId { get; set; } = string.Empty;
    }

    public sealed class QmtCancelOrderRequest
    {
        [JsonProperty("order_id")]
        public string OrderId { get; set; } = string.Empty;
    }

    public sealed class QmtCancelOrderPayload
    {
        [JsonProperty("canceled")]
        public bool Canceled { get; set; }

        [JsonProperty("order_id")]
        public string OrderId { get; set; } = string.Empty;
    }

    public sealed class QmtStockCodeRequest
    {
        [JsonProperty("stock_code")]
        public string StockCode { get; set; } = string.Empty;
    }

    public sealed class QmtHistoryRequest
    {
        [JsonProperty("stock_code")]
        public string StockCode { get; set; } = string.Empty;

        [JsonProperty("period")]
        public string Period { get; set; } = string.Empty;

        [JsonProperty("start_time")]
        public string StartTime { get; set; } = string.Empty;

        [JsonProperty("end_time")]
        public string EndTime { get; set; } = string.Empty;
    }

    public sealed class QmtQueryHistoryPayload
    {
        [JsonProperty("bars")]
        public List<QmtHistoryBar> Bars { get; set; } = new List<QmtHistoryBar>();
    }

    public sealed class QmtHistoryBar
    {
        [JsonProperty("time")]
        public string Time { get; set; } = string.Empty;

        [JsonProperty("open")]
        public decimal Open { get; set; }

        [JsonProperty("high")]
        public decimal High { get; set; }

        [JsonProperty("low")]
        public decimal Low { get; set; }

        [JsonProperty("close")]
        public decimal Close { get; set; }

        [JsonProperty("volume")]
        public decimal Volume { get; set; }
    }

    public sealed class QmtSubscribePayload
    {
        [JsonProperty("subscribed")]
        public bool Subscribed { get; set; }

        [JsonProperty("subscription_id")]
        public string SubscriptionId { get; set; } = string.Empty;

        [JsonProperty("stock_code")]
        public string StockCode { get; set; } = string.Empty;
    }

    public sealed class QmtUnsubscribeRequest
    {
        [JsonProperty("subscription_id")]
        public string SubscriptionId { get; set; } = string.Empty;
    }

    public sealed class QmtUnsubscribePayload
    {
        [JsonProperty("unsubscribed")]
        public bool Unsubscribed { get; set; }

        [JsonProperty("subscription_id")]
        public string SubscriptionId { get; set; } = string.Empty;
    }

    public sealed class QmtQuoteEventPayload
    {
        [JsonProperty("stock_code")]
        public string StockCode { get; set; } = string.Empty;

        [JsonProperty("time")]
        public string Time { get; set; } = string.Empty;

        [JsonProperty("last_price")]
        public decimal LastPrice { get; set; }

        [JsonProperty("volume")]
        public decimal Volume { get; set; }

        [JsonProperty("amount")]
        public decimal Amount { get; set; }

        [JsonProperty("bid_price")]
        public decimal BidPrice { get; set; }

        [JsonProperty("ask_price")]
        public decimal AskPrice { get; set; }

        [JsonProperty("bid_volume")]
        public decimal BidVolume { get; set; }

        [JsonProperty("ask_volume")]
        public decimal AskVolume { get; set; }
    }

    public sealed class QmtDealEventPayload
    {
        [JsonProperty("stock_code")]
        public string StockCode { get; set; } = string.Empty;

        [JsonProperty("order_id")]
        public string OrderId { get; set; } = string.Empty;

        [JsonProperty("deal_id")]
        public string DealId { get; set; } = string.Empty;

        [JsonProperty("direction")]
        public string Direction { get; set; } = string.Empty;

        [JsonProperty("volume")]
        public decimal Volume { get; set; }

        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("amount")]
        public decimal Amount { get; set; }

        [JsonProperty("commission")]
        public decimal Commission { get; set; }

        [JsonProperty("time")]
        public string Time { get; set; } = string.Empty;
    }
}
