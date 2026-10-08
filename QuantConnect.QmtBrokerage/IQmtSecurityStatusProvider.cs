namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Provides the current QMT trading status for a China equity.
    /// </summary>
    public interface IQmtSecurityStatusProvider
    {
        bool IsSecurityTrading(Symbol symbol);
    }
}
