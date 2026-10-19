namespace Shabana.Core.Errors;

/// <summary>
/// The error codes raised by the SQLite schema (<c>RAISE(ABORT, 'CODE|...')</c>) and by the service layer: 03 section 9.
/// One member per code; the Arabic message of each lives in <c>labels.native.ar.json</c> group <c>ERR</c> (key = the code string).
/// Add a member here, a message there, and the tests (<c>ErrorCodeTests</c>) tell you if one of them is missing.
/// </summary>
public enum ErrorCode
{
    AttachmentParentNotFound,
    CashFloor,
    CashSiteRequired,
    ClientCreditNegative,
    ConversionReverseInsufficient,
    DiscountBelowPaid,
    DiscountExceedsSubtotal,
    DiscountPercentOver100,
    ExpenseAlreadyVoided,
    ExpenseCategoryInactive,
    ExpenseImmutable,
    LastAdmin,
    LedgerReadonly,
    OrderDeleteBlocked,
    PaymentImmutable,
    PaymentOverpay,
    PlcExceedsRemaining,
    PmNotOwned,
    PoConversionExceedsRemaining,
    PoLineNotFound,
    PoNotFound,
    PoNotGeneral,
    PoTotalKgFrozen,
    RowImmutable,
    SelfRoleChange,
    SoDraftNoPayment,
    SoInvoiceNoLines,
    SoLinesLocked,
    SoNotFound,
    SoStockInsufficient,
    SoTransitionIllegal,
    StockFloor,
    TransferStockInsufficient,
    VendorCreditNegative,
}
