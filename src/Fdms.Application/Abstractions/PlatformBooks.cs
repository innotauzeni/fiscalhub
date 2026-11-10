namespace Fdms.Application.Abstractions
{
    /// <summary>
    /// The platform's own books. The operator's business (selling FiscalHub to companies) is kept in the same accounting module companies use, under one internal company
    /// with this fixed id. It is never listed or billed as a customer. Billing invoices, payments, partner commissions and payouts post into it by themselves.
    /// </summary>
    public static class PlatformBooks
    {
        public static readonly Guid ClientId = new("f15ca1b0-0000-4000-8000-000000000001");
        public static bool Is(Guid clientId) => clientId == ClientId;
    }
}
