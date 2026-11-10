namespace Fdms.Application.Abstractions
{
    /// <summary>One credential or option of a payment gateway. Secret values are stored encrypted and are never sent back to the browser.</summary>
    public sealed record GatewayFieldDto(string Key, string Label, bool Secret, bool Required, string? Value, bool HasValue, string? Hint);

    /// <summary>
    /// A payment method the operator can connect. <paramref name="Automatic"/> gateways (Paynow, EcoCash, Stripe) charge and confirm by themselves; manual ones
    /// (EcoCash Send Money, PayPal) only show instructions and are confirmed by the operator. Credentials saved in the console win over the server configuration file.
    /// </summary>
    public sealed record PaymentGatewayDto(string Code, string Name, string Description, bool Automatic, bool Enabled, bool Configured, string Source, IReadOnlyList<GatewayFieldDto> Fields,
        string? CallbackUrl, DateTime? UpdatedAt, string? UpdatedBy);

    /// <summary>Values by field key. A secret left empty keeps the saved one; <paramref name="ClearSecrets"/> removes them.</summary>
    public sealed record SaveGatewayRequest(bool Enabled, Dictionary<string, string?>? Values, bool ClearSecrets);

    /// <summary>One way a company can pay, as offered at checkout. Kind: redirect (leaves for the gateway's page), mobile (a prompt on the phone), manual (pay, then tell us).</summary>
    public sealed record PayMethodDto(string Id, string Name, string Kind, bool Automatic, string? Details);

    public interface IPaymentGatewaySettings
    {
        Task<IReadOnlyList<PaymentGatewayDto>> ListAsync(string apiBase, CancellationToken ct);
        Task<PaymentGatewayDto> SaveAsync(string code, SaveGatewayRequest req, string apiBase, CancellationToken ct);
        /// <summary>The decrypted credentials of an enabled, fully configured gateway; null when it cannot be used.</summary>
        Task<IReadOnlyDictionary<string, string>?> CredentialsAsync(string code, CancellationToken ct);
        /// <summary>The gateway methods (not bank transfer) that are connected right now, in the order they are offered.</summary>
        Task<IReadOnlyList<PayMethodDto>> MethodsAsync(CancellationToken ct);
    }
}
