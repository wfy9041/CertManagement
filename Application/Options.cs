namespace CertManagement.Application;

public sealed class AcmeOptions
{
    public const string SectionName = "Acme";
    public string Environment { get; set; } = "Staging";
    public string Email { get; set; } = string.Empty;
    public string AccountKeyPath { get; set; } = "data/acme/account-key.pem";
    public string CertificateDirectory { get; set; } = "data/certificates";
    public int DnsPropagationTimeoutSeconds { get; set; } = 180;
    public int DnsPollingIntervalSeconds { get; set; } = 5;
    public List<string> DnsQueryEndpoints { get; set; } =
    [
        "https://dns.alidns.com/resolve",
        "https://doh.pub/dns-query"
    ];
}

public sealed class RenewalOptions
{
    public const string SectionName = "Renewal";
    public int ScanIntervalMinutes { get; set; } = 15;
    public int RenewBeforeDays { get; set; } = 30;
    public int MaxAttempts { get; set; } = 3;
}
