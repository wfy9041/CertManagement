namespace CertManagement.Domain;

public enum ChallengeType
{
    Http01 = 1,
    Dns01 = 2
}

public enum CertificateStatus
{
    Pending = 1,
    CreatingOrder = 2,
    CreatingChallenge = 3,
    WaitingDns = 4,
    Validating = 5,
    Issuing = 6,
    Issued = 7,
    Renewing = 8,
    Failed = 9
}

public enum DnsProviderType
{
    Cloudflare = 1,
    Aliyun = 2
}
