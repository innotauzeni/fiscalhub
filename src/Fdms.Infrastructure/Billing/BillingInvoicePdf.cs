using System.Globalization;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Services;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Fdms.Infrastructure.Billing
{
    /// <summary>The invoice the platform sends to a company for its subscription: the operator's identity, the amount, and how to pay.</summary>
    public static class BillingInvoicePdf
    {
        static BillingInvoicePdf() => QuestPDF.Settings.License = LicenseType.Community;

        public static byte[] Render(BillingInvoice inv, string clientName, string? clientEmail, BillingProfile p, byte[]? logo) => Build(inv, clientName, clientEmail, p, logo).GeneratePdf();

        public static byte[] RenderPng(BillingInvoice inv, string clientName, string? clientEmail, BillingProfile p, byte[]? logo) =>
            Build(inv, clientName, clientEmail, p, logo).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 110 }).First();

        private static IDocument Build(BillingInvoice inv, string clientName, string? clientEmail, BillingProfile p, byte[]? logo)
        {
            var accent = string.IsNullOrWhiteSpace(p.Accent) ? "#2563eb" : p.Accent;
            var showLogo = logo is { Length: > 0 } && ReceiptPdfRenderer.CanDecode(logo);
            var c = CultureInfo.InvariantCulture;
            string M(decimal v) => v.ToString("N2", c) + " " + inv.Currency;
            string D(DateTime d) => d.ToString("d MMM yyyy", c);
            var paid = inv.Status == "Paid";
            var statusColor = paid ? Colors.Green.Darken2 : inv.Status == "Void" ? Colors.Grey.Darken1 : Colors.Orange.Darken2;
            var lines = (p.Address ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return Document.Create(doc => doc.Page(page =>
            {
                page.Size(PageSizes.A4); page.Margin(40); page.DefaultTextStyle(t => t.FontSize(10));
                page.Content().Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c1 =>
                        {
                            if (showLogo) c1.Item().MaxWidth(170).MaxHeight(58).Image(logo!).FitArea();
                            c1.Item().Text(p.CompanyName).Bold().FontSize(15).FontColor(accent);
                            foreach (var l in lines) c1.Item().Text(l);
                            if (!string.IsNullOrEmpty(p.TaxNumber)) c1.Item().Text("Tax no: " + p.TaxNumber);
                            if (!string.IsNullOrEmpty(p.Email)) c1.Item().Text(p.Email);
                            if (!string.IsNullOrEmpty(p.Phone)) c1.Item().Text(p.Phone);
                            if (!string.IsNullOrEmpty(p.Website)) c1.Item().Text(p.Website);
                        });
                        row.ConstantItem(190).Column(c2 =>
                        {
                            c2.Item().AlignRight().Text("INVOICE").Bold().FontSize(24).FontColor(accent);
                            c2.Item().AlignRight().Text(inv.Number).Bold().FontSize(12);
                            c2.Item().AlignRight().Text("Issued " + D(inv.IssuedAt));
                            c2.Item().AlignRight().Text("Due " + D(inv.DueAt));
                            c2.Item().AlignRight().PaddingTop(4).Text(inv.Status.ToUpperInvariant()).Bold().FontSize(12).FontColor(statusColor);
                            if (paid && inv.PaidAt.HasValue) c2.Item().AlignRight().Text("Paid " + D(inv.PaidAt.Value) + (string.IsNullOrEmpty(inv.PaymentMethod) ? "" : " via " + inv.PaymentMethod)).FontSize(9);
                        });
                    });

                    col.Item().Height(3).Background(accent);

                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c1 =>
                        {
                            c1.Item().Text("BILLED TO").Bold().FontSize(8).FontColor(Colors.Grey.Darken1);
                            c1.Item().Text(clientName).Bold().FontSize(12);
                            if (!string.IsNullOrEmpty(clientEmail)) c1.Item().Text(clientEmail);
                        });
                        row.ConstantItem(190).Column(c2 =>
                        {
                            c2.Item().Text("SERVICE PERIOD").Bold().FontSize(8).FontColor(Colors.Grey.Darken1);
                            c2.Item().Text($"{D(inv.PeriodStart)} - {D(inv.PeriodEnd)}");
                        });
                    });

                    col.Item().PaddingTop(6).Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.RelativeColumn(5); cd.RelativeColumn(1.4f); });
                        t.Header(h =>
                        {
                            IContainer Head(IContainer x) => x.BorderBottom(0.8f).BorderColor(accent).PaddingVertical(4);
                            h.Cell().Element(Head).Text("Description").Bold().FontColor(accent);
                            h.Cell().Element(Head).AlignRight().Text("Amount").Bold().FontColor(accent);
                        });
                        t.Cell().PaddingVertical(6).Text(inv.Description);
                        t.Cell().PaddingVertical(6).AlignRight().Text(M(inv.Subtotal));
                    });

                    col.Item().AlignRight().Width(230).Column(c3 =>
                    {
                        c3.Item().Row(r => { r.RelativeItem().Text("Subtotal"); r.ConstantItem(110).AlignRight().Text(M(inv.Subtotal)); });
                        if (inv.TaxPercent > 0) c3.Item().Row(r => { r.RelativeItem().Text($"Tax ({inv.TaxPercent:0.##}%)"); r.ConstantItem(110).AlignRight().Text(M(inv.TaxAmount)); });
                        c3.Item().BorderTop(0.8f).BorderColor(accent).PaddingTop(4).Row(r =>
                        {
                            r.RelativeItem().Text(paid ? "Total paid" : "Total due").Bold().FontSize(12);
                            r.ConstantItem(110).AlignRight().Text(M(inv.Total)).Bold().FontSize(12).FontColor(accent);
                        });
                    });

                    if (inv.Status == "Open")
                    {
                        col.Item().PaddingTop(10).Text("How to pay").Bold().FontColor(accent);
                        col.Item().Text("Sign in and open Billing to pay online: Visa/Mastercard, ZimSwitch, EcoCash, OneMoney and other wallets through Paynow.");
                        if (!string.IsNullOrEmpty(p.AccountNumber))
                        {
                            col.Item().PaddingTop(2).Text("Or pay by bank transfer:").Bold();
                            if (!string.IsNullOrEmpty(p.BankName)) col.Item().Text("Bank: " + p.BankName);
                            if (!string.IsNullOrEmpty(p.AccountName)) col.Item().Text("Account name: " + p.AccountName);
                            col.Item().Text("Account number: " + p.AccountNumber);
                            if (!string.IsNullOrEmpty(p.BranchCode)) col.Item().Text("Branch code: " + p.BranchCode);
                            if (!string.IsNullOrEmpty(p.Swift)) col.Item().Text("SWIFT: " + p.Swift);
                            col.Item().Text("Reference: " + inv.Number).Bold();
                        }
                        if (!string.IsNullOrEmpty(p.PaymentInstructions)) col.Item().PaddingTop(2).Text(p.PaymentInstructions);
                    }

                    if (!string.IsNullOrEmpty(p.Terms)) { col.Item().PaddingTop(8).Text("Terms").Bold().FontColor(accent); col.Item().Text(p.Terms).FontSize(9); }
                    if (!string.IsNullOrEmpty(p.Footer)) col.Item().PaddingTop(10).AlignCenter().Text(p.Footer).Italic().FontColor(Colors.Grey.Darken2);
                });
            }));
        }
    }
}
