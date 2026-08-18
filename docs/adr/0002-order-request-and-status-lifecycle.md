# ADR 0002: Order request and order status lifecycle

## Status

Accepted.

## Context

LEAN exposes two independent state machines:

- `OrderRequestStatus` describes LEAN's processing of a submit, cancel, or
  update request.
- `OrderStatus` describes the external order lifecycle after the request has
  reached the Brokerage.

The QMT Gateway uses Big QMT's built-in `passorder` function. `passorder`
returns no value. A successful return only proves that the Gateway invoked the
QMT function without a synchronous exception; it does not prove that QMT or the
counter accepted the order. QMT reports acceptance, rejection, cancellation,
and fills later through order and deal callbacks.

The design therefore treats order submission as a staged operation. It does
not add a database, message queue, transaction coordinator, or automatic trade
request retry.

Official QMT references:

- [passorder](https://dict.thinktrader.net/innerApi/trading_function.html#passorder-综合下单函数)
- [Order and Deal structures](https://dict.thinktrader.net/innerApi/data_structure.html)
- [QMT enum constants](https://dict.thinktrader.net/innerApi/enum_constants.html)

## Decision

### State ownership

| State | Owner | Meaning |
|---|---|---|
| `OrderRequestStatus.Unprocessed` | LEAN | The request has not entered the transaction handler. |
| `OrderRequestStatus.Processing` | LEAN | LEAN is validating and forwarding the request. |
| `OrderRequestStatus.Processed` | LEAN | The Brokerage method completed successfully. For submit, the Gateway invoked `passorder`; this is not a counter acceptance. |
| `OrderRequestStatus.Error` | LEAN | Local validation, transport, Gateway, or a synchronous QMT call failed. |
| `OrderStatus.New` | LEAN | No QMT order acknowledgement has arrived. |
| Remaining `OrderStatus` values | QMT callbacks through the Brokerage | The external order lifecycle. |

`OrderRequestStatus` must not wait for an asynchronous QMT callback. Blocking
the Gateway request handler until a callback arrives could block the same QMT
execution path required to deliver that callback.

### Submit sequence

```text
SubmitOrderRequest
→ OrderRequestStatus.Processing
→ QmtBrokerage.PlaceOrder
→ Gateway validation
→ passorder invocation
→ Gateway response accepted=true
→ QmtBrokerage.PlaceOrder returns true
→ OrderRequestStatus.Processed; OrderStatus remains New
→ QMT order callback 49/50
→ OrderStatus.Submitted
```

If Gateway validation, transport, or `passorder` raises an error,
`PlaceOrder` returns false and LEAN records `OrderRequestStatus.Error`.

If QMT rejects the order asynchronously, the request remains `Processed`
because it was processed successfully, while the external order becomes
`OrderStatus.Invalid`. The QMT error ID and rejection text are included in the
LEAN `OrderEvent` message.

Until the first QMT order callback arrives, the order remains
`OrderStatus.New`. LEAN rejects a cancel request for an order in `New`, so the
strategy must wait for `Submitted` before canceling. If the acknowledgement is
lost, the order can remain `New` even though QMT may have accepted it; this
requires account-side inspection rather than an automatic submit retry.

### Fill sequence

QMT order statuses include cumulative quantities but LEAN fill events require
incremental quantity, fill price, and fee. Consequently, fill state is driven
only by QMT Deal callbacks:

```text
QMT Deal callback
→ deduplicate by deal ID
→ incremental LEAN OrderEvent
→ PartiallyFilled or Filled
```

QMT order callbacks with status 55 or 56 do not independently create fill
events. This prevents the order callback and deal callback from counting the
same fill twice.

QMT status 52 contains two facts: part of the order filled and cancellation of
the remainder is pending. LEAN represents these facts as two events:

```text
Deal callback → PartiallyFilled
Order status 52 → CancelPending
Order status 53 → Canceled
```

### Cancel sequence

```text
CancelOrderRequest
→ OrderRequestStatus.Processing
→ QmtBrokerage.CancelOrder
→ Gateway cancel(order_id)
```

The QMT `cancel` function returns whether it emitted the cancellation signal.
The Brokerage must use the returned `canceled` value:

- `true`: `CancelOrder` returns true and the request becomes `Processed`.
- `false`: `CancelOrder` returns false and the request becomes `Error`.

After a successful request, QMT status 51 or 52 produces `CancelPending`, and
status 53 or 54 produces `Canceled`. A later asynchronous counter rejection of
the cancellation does not rewrite the already processed request; it is
reported as a Brokerage warning and the order remains or returns open.

### QMT to LEAN order status mapping

| QMT `m_nOrderStatus` | QMT meaning | LEAN status | Notes |
|---:|---|---|---|
| 48 | Unreported compatibility state | `Submitted` | Present in native XtQuant documentation but not the current Big QMT `EEntrustStatus` table. Retained for compatibility pending real-log confirmation. |
| 49 | Waiting to report | `Submitted` | The order is active inside QMT. |
| 50 | Reported | `Submitted` | The order was sent to the counter. |
| 51 | Reported, cancel pending | `CancelPending` | |
| 52 | Partially filled, cancel pending | `CancelPending` | The fill is emitted from Deal callbacks. |
| 53 | Partially filled, remainder canceled | `Canceled` | Earlier fill events remain valid. |
| 54 | Canceled | `Canceled` | |
| 55 | Partially filled | `PartiallyFilled` | The status mapper exposes this value, but live fill events come from Deal callbacks. |
| 56 | Filled | `Filled` | The status mapper exposes this value, but live fill events come from Deal callbacks. |
| 57 | Junk/rejected | `Invalid` | Rejection fields are propagated. |
| 255 or unsupported | Unknown | `None` | No order event is emitted; a diagnostic log records the raw values. |

The previous mapping of status 86 to `Submitted` is removed. Big QMT's current
official status table does not define 86 as an order status; it defines 86 as
the price type "own-side best price". An unsupported status remains nonterminal
and is logged rather than guessed.

### QMT submit status

QMT also exposes `m_nOrderSubmitStatus`:

| Value | Meaning |
|---:|---|
| 48 | Order submitted |
| 49 | Cancellation submitted |
| 50 | Update submitted |
| 51 | Accepted |
| 52 | Order rejected |
| 53 | Cancellation rejected |
| 54 | Update rejected |

The QMT data-structure documentation says this field is not required for stock
orders, so it is diagnostic rather than the primary source of truth. One
fail-safe exception is used: when `m_nOrderStatus` is unknown but submit status
52 explicitly says the order was rejected, LEAN emits `OrderStatus.Invalid`.

### Order identity

The current client order ID is the decimal LEAN order ID:

```text
LEAN order.Id
→ protocol client_order_id
→ passorder userOrderId
→ QMT m_strRemark
→ order callback/query client_order_id
```

It is used for correlation, not for automatic retry or exactly-once delivery.
No submit or cancel request is automatically retried after an ambiguous network
failure.

## Failure behavior

| Failure | Request result | Order result |
|---|---|---|
| Unsupported LEAN order | `Error` | No QMT order. |
| Gateway unavailable before dispatch | `Error` | No confirmed QMT order. |
| `passorder` raises synchronously | `Error` | No confirmed QMT order. |
| `passorder` returns, then QMT rejects | `Processed` | `Invalid` with QMT rejection reason. |
| `passorder` returns, but no order callback arrives | `Processed` | Remains `New`; LEAN will not submit a cancel until QMT acknowledgement is observed. |
| QMT `cancel` returns false | `Error` | Existing order state is unchanged. |
| QMT accepts cancellation signal | `Processed` | `CancelPending`, then `Canceled` or an open state if the counter rejects it. |
| Deal arrives before order callback | No request change | Deal is queued by native order ID and processed after correlation is available. |
| Network response is lost after dispatch | Ambiguous, normally `Error` | A later callback may still update the order; no automatic retry is allowed. |

## Consequences

The two LEAN state machines are sufficient for the connected, real-time A-share
scope. The design avoids a second source of truth and avoids duplicate orders
caused by automatic retries.

The remaining accepted limitations are explicit:

- a transport failure after QMT dispatch but before the Gateway response is an
  ambiguous request outcome;
- a missing QMT acknowledgement can leave the order `New`, which prevents a
  LEAN cancel request until the account state is inspected or a callback is
  received;
- trade state is not reconstructed after a LEAN process restart;
- missed order and deal events during a disconnected interval are not replayed;
- QMT submit-status behavior and compatibility status 48 still require evidence
  from captured real-account logs.

## Verification requirements

- Unit-test every documented QMT-to-LEAN status mapping.
- Verify both `canceled=true` and `canceled=false` Gateway responses.
- Verify structured rejection fields reach the LEAN `OrderEvent` message.
- Keep the real-QMT trading test responsible for proving the sequence
  `Processed → Submitted → CancelPending/Canceled` against the configured
  account before production use.
