namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Read-only detail types accepted by Big QMT's get_trade_detail_data API.
    /// </summary>
    public static class QmtTradeDetailType
    {
        public const string Account = "ACCOUNT";
        public const string Position = "POSITION";
        public const string Order = "ORDER";
        public const string Deal = "DEAL";
    }
}
