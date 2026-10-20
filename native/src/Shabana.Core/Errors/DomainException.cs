namespace Shabana.Core.Errors;

/// <summary>
/// A business rule was violated. Raised by services and by the data layer when the schema aborts a statement with a
/// <c>RAISE(ABORT, 'CODE|...')</c> (see <c>SqliteErrorMapper</c>). <see cref="Exception.Message"/> is the Arabic text
/// from <see cref="ErrorCodes.Message"/>, safe to show to the user; the stack trace never is.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(ErrorCode code)
        : base(code.Message())
    {
        Code = code;
    }

    public DomainException(ErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public DomainException(ErrorCode code, Exception inner)
        : base(code.Message(), inner)
    {
        Code = code;
    }

    public ErrorCode Code { get; }
}
