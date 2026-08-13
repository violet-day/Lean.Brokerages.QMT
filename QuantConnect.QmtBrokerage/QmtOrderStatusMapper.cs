using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Converts QMT native order states into LEAN order states.
    /// </summary>
    public static class QmtOrderStatusMapper
    {
        public static OrderStatus GetLeanOrderStatus(int qmtOrderStatus)
        {
            return qmtOrderStatus switch
            {
                48 => OrderStatus.Submitted,
                49 => OrderStatus.Submitted,
                50 => OrderStatus.Submitted,
                51 => OrderStatus.CancelPending,
                52 => OrderStatus.CancelPending,
                53 => OrderStatus.Canceled,
                54 => OrderStatus.Canceled,
                55 => OrderStatus.PartiallyFilled,
                56 => OrderStatus.Filled,
                57 => OrderStatus.Invalid,
                86 => OrderStatus.Submitted,
                255 => OrderStatus.None,
                _ => OrderStatus.None
            };
        }
    }
}
