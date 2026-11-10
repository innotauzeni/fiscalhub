namespace Fdms.Domain.Enums
{
    // Values and order follow "5.4 Enums" of the ZIMRA Fiscal Device Gateway API spec.
    public enum ReceiptType { FiscalInvoice = 0, CreditNote = 1, DebitNote = 2 }

    public enum ReceiptLineType { Sale = 0, Discount = 1 }

    public enum MoneyType { Cash = 0, Card = 1, MobileWallet = 2, Coupon = 3, Credit = 4, BankTransfer = 5, Other = 6 }

    public enum FiscalDayStatus { FiscalDayClosed = 0, FiscalDayOpened = 1, FiscalDayCloseInitiated = 2, FiscalDayCloseFailed = 3 }

    public enum DeviceOperatingMode { Online = 0, Offline = 1 }

    public enum FiscalCounterType
    {
        SaleByTax = 0,
        SaleTaxByTax = 1,
        CreditNoteByTax = 2,
        CreditNoteTaxByTax = 3,
        DebitNoteByTax = 4,
        DebitNoteTaxByTax = 5,
        BalanceByMoneyType = 6
    }

    public enum ReceiptPrintForm { Receipt48 = 0, InvoiceA4 = 1 }

    public enum ValidationColor { None = 0, Grey = 1, Yellow = 2, Red = 3 }
}
