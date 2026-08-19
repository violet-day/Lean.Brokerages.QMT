using System;

namespace QuantConnect.Brokerages.Qmt
{
    public class QmtGatewayException : Exception
    {
        public QmtGatewayException(string message)
            : base(message)
        {
        }

        public QmtGatewayException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed class QmtGatewayProtocolException : QmtGatewayException
    {
        public QmtGatewayProtocolException(string message)
            : base(message)
        {
        }
    }

    public sealed class QmtGatewayRequestException : QmtGatewayException
    {
        public string ErrorCode { get; }

        public QmtGatewayRequestException(string operation, string errorCode, string errorMessage)
            : base($"QMT Gateway operation '{operation}' failed ({errorCode}): {errorMessage}")
        {
            ErrorCode = errorCode;
        }
    }

    public sealed class QmtGatewayTimeoutException : QmtGatewayException
    {
        public QmtGatewayTimeoutException(string operation, TimeSpan timeout)
            : base($"QMT Gateway operation '{operation}' timed out after {timeout.TotalSeconds:0.###} seconds.")
        {
        }
    }

    public sealed class QmtOrderSubmissionException : QmtGatewayException
    {
        public string ErrorCode { get; }

        public QmtOrderSubmissionException(string errorCode, string errorMessage)
            : base($"QMT order submission failed ({errorCode}): {errorMessage}")
        {
            ErrorCode = errorCode;
        }
    }
}
