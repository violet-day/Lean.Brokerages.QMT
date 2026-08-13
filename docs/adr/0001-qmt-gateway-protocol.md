# ADR 0001: QMT Gateway Protocol v1

- Status: Accepted for MVP
- Date: 2026-08-13

## Context

QMT runs its strategy code in a customized Python 3.6.8 interpreter on the Windows host. LEAN runs separately and must query the account, subscribe to live quotes, and receive order/deal callbacks without loading QMT's native modules into the LEAN process.

The QMT API must be called on QMT's `handlebar` thread. A socket reader therefore cannot call `get_trade_detail_data`, `passorder`, or `cancel` directly; it only decodes requests and enqueues them for `handlebar`.

## Decision

Use one persistent TCP connection with newline-delimited JSON (NDJSON). The default endpoint is port `17890`; a Windows-hosted Docker container normally reaches it at `host.docker.internal:17890`.

Each line is one complete UTF-8 JSON object. Embedded newlines must be escaped by the JSON encoder. Protocol v1 has no binary frames and no compression.

Every message contains these fields:

```json
{
  "protocol_version": 1,
  "message_type": "request",
  "request_id": "0ba76d916bc64974a5f0a13fb12e21a7",
  "operation": "query_account",
  "success": null,
  "error_code": "",
  "error_message": "",
  "payload": {}
}
```

`message_type` is `request`, `response`, or `event`. Requests and responses have the same non-empty `request_id`; responses can arrive in any order. Events have no request ID. `success`, `error_code`, and `error_message` are response fields. Unknown payload fields must be retained/ignored for forward-compatible rollout.

The C# client rejects messages whose `protocol_version` is not `1`. It keeps the payload as a Newtonsoft.Json `JObject` and offers typed DTO conversion.

## Connection and handshake

Immediately after opening TCP, the client sends:

```json
{"protocol_version":1,"message_type":"request","request_id":"...","operation":"hello","payload":{"account_id":"86033767"}}
```

The server responds with:

```json
{"protocol_version":1,"message_type":"response","request_id":"...","operation":"hello","success":true,"error_code":"","error_message":"","payload":{"server_name":"qmt-python-gateway","account_id":"86033767","trading_enabled":false}}
```

The client must compare the returned `account_id` with its configured account. A mismatch aborts the connection. `trading_enabled` is authoritative: an order-capable client must refuse place/cancel operations when it is false. Trading defaults to disabled on the server.

The Gateway never starts, stops, logs into, or restarts QMT. An operator starts QMT and the imported Python strategy manually.

## Operations

### Read-only queries

`query_account` response:

```json
{"accounts":[{"available_cash":100000.00}]}
```

`query_positions` response:

```json
{"positions":[{"stock_code":"600000.SH","volume":100,"open_price":9.80,"last_price":10.10,"market_value":1010.00}]}
```

`query_orders` response:

```json
{"orders":[{"stock_code":"600000.SH","order_id":"123","client_order_id":"42","direction":"buy","order_type":"limit","status":50,"original_volume":100,"traded_volume":0,"limit_price":10.00,"traded_price":0,"remark":""}]}
```

`direction` is `buy` or `sell`; `order_type` is `market` or `limit`. Decimal financial values are JSON numbers. Native identifiers are strings because QMT/runtime builds may expose different numeric widths.

### Trading

`place_order` request:

```json
{"client_order_id":"42","stock_code":"600000.SH","order_type":"limit","direction":"buy","quantity":100,"limit_price":10.00,"strategy_name":"lean"}
```

For `market`, `limit_price` is null/omitted. Response:

```json
{"accepted":true,"client_order_id":"42","native_order_id":"123"}
```

`cancel_order` request:

```json
{"order_id":"123"}
```

The response payload may be empty. `success: true` means the cancel request was accepted for processing; final order state arrives through an `order` event.

When trading is disabled, the Gateway returns `success: false`, `error_code: "TRADING_DISABLED"`, and a human-readable `error_message`. Automated tests use only a fake Gateway and never enable live trading.

### Market data

`subscribe` request:

```json
{"stock_code":"000001.SZ"}
```

The response returns the protocol subscription identifier owned by the Gateway:

```json
{"subscribed":true,"subscription_id":"37","stock_code":"000001.SZ"}
```

`unsubscribe` uses that identifier, not the stock code:

```json
{"subscription_id":"37"}
```

Its response is:

```json
{"unsubscribed":true,"subscription_id":"37"}
```

The client treats duplicate subscribe requests as safe and stores the returned `subscription_id` for later unsubscribe. The Gateway owns the native QMT subscription handle and emits `quote` events.

## Events

All events use `message_type: "event"`. MVP operations are `quote`, `order`, `deal`, `position`, `account`, and `connection`.

Representative quote payload:

```json
{"stock_code":"000001.SZ","time":"2026-08-13T09:30:01.123+08:00","last_price":10.25,"volume":1200,"amount":12300,"bid_price":10.24,"ask_price":10.25,"bid_volume":500,"ask_volume":300}
```

Representative order payload:

```json
{"stock_code":"600000.SH","order_id":"123","client_order_id":"42","status":55,"direction":"buy","order_type":"limit","original_volume":100,"traded_volume":50,"limit_price":10.00,"traded_price":9.99,"remark":"","time":"2026-08-13T09:31:00+08:00"}
```

Representative deal payload:

```json
{"stock_code":"600000.SH","order_id":"123","deal_id":"456","direction":"buy","volume":50,"price":9.99,"amount":499.50,"commission":1.25,"time":"2026-08-13T09:31:00+08:00"}
```

`position`, `account`, and `connection` payloads may evolve during MVP field discovery. Consumers retain the raw `JObject`; confirmed fields are promoted to typed DTOs.

## Threading and failure behavior

The Python socket thread parses requests and puts them on an inbound queue. QMT `handlebar` drains that queue and calls the QMT API. QMT callbacks put events on an outbound queue. A socket writer serializes responses/events so lines never interleave.

The C# client has one reader loop and one serialized writer. A concurrent map routes responses by request ID. Each request has a timeout. Socket failure completes all pending requests exceptionally, marks the client disconnected, and emits one disconnected notification. Malformed individual JSON lines are logged and skipped; EOF or a transport exception ends the connection.

Reconnect is initiated by the owning Brokerage. After reconnect it must repeat `hello` and restore active subscriptions.

## Security

This MVP protocol has no encryption or authentication and must only bind to a trusted interface. Do not expose port `17890` to the public Internet. Container-to-host use is restricted by Windows firewall rules. A future protocol version may add a shared-secret handshake or TLS if the deployment crosses a trusted-host boundary.

Logs include operation, request ID, connection stage, account ID, and error code. Logs must never include passwords, bearer tokens, or full order payloads.

## Consequences

The approach isolates QMT's Python/native environment from LEAN and is simple to debug with line-oriented logs. It also makes a deterministic fake Gateway possible, so connection, timeout, response ordering, and event behavior can be tested without QMT or a brokerage account.

The single TCP connection is intentionally an MVP constraint. If a stalled writer or market-data burst becomes material, protocol v2 can split command and event channels while keeping the v1 DTO semantics.
