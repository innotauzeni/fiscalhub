using Fdms.Domain.Enums;

namespace Fdms.Application.Fiscal
{
    /// <summary>Tax configured in FDMS (getConfig.applicableTaxes). Percent is null for exempt.</summary>
    public sealed record TaxDefinition(int TaxId, string? Code, decimal? Percent);

    /// <summary>One line as supplied by the caller. Prices are positive; the calculator signs them per receipt type.</summary>
    public sealed record LineInput(
        string Name,
        string? HsCode,
        decimal Quantity,
        decimal UnitPrice,
        int TaxId,
        ReceiptLineType LineType = ReceiptLineType.Sale);

    public sealed record PaymentInput(MoneyType MoneyType, decimal Amount);

    public sealed record ReceiptLine(
        int LineNo, ReceiptLineType LineType, string? HsCode, string Name,
        decimal Price, decimal Quantity, decimal Total, string? TaxCode, decimal? TaxPercent, int TaxId);

    public sealed record ReceiptTaxLine(int TaxId, string? TaxCode, decimal? TaxPercent, decimal TaxAmount, decimal SalesAmountWithTax);

    public sealed record ReceiptPaymentLine(MoneyType MoneyType, decimal Amount);

    /// <summary>Fully calculated receipt body (everything that is NOT counters/signature/dates).</summary>
    public sealed record CalculatedReceipt(
        ReceiptType Type,
        string Currency,
        bool LinesTaxInclusive,
        IReadOnlyList<ReceiptLine> Lines,
        IReadOnlyList<ReceiptTaxLine> Taxes,
        IReadOnlyList<ReceiptPaymentLine> Payments,
        decimal Total);

    /// <summary>Input to the receipt device signature (spec 13.2.1).</summary>
    public sealed record ReceiptSigningInput(
        int DeviceId,
        ReceiptType Type,
        string Currency,
        long GlobalNo,
        DateTime Date,
        decimal Total,
        IReadOnlyList<ReceiptTaxLine> Taxes,
        string? PreviousReceiptHash);

    /// <summary>One fiscal day counter (spec 6). TaxId/TaxPercent for *ByTax counters, MoneyType for BalanceByMoneyType.</summary>
    public sealed record FiscalCounter(
        FiscalCounterType Type, string Currency, int? TaxId, decimal? TaxPercent, MoneyType? MoneyType, decimal Value);

    public sealed record SignatureResult(string Hash, string Signature);
}
