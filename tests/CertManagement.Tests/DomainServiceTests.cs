using CertManagement.Application;
using Microsoft.Extensions.Options;
using Xunit;
using DomainEntity = CertManagement.Domain.Domain;

namespace CertManagement.Tests;

public sealed class DomainServiceTests
{
    [Fact]
    public void Names_NormalizesAndDeduplicatesSans()
    {
        var domain = new DomainEntity { PrimaryDomain = "Example.COM", Sans = "www.example.com\nexample.com\nWWW.EXAMPLE.COM" };
        Assert.Equal(["Example.COM", "www.example.com"], DomainService.Names(domain));
    }

    [Fact]
    public void AdminCredentialService_VerifiesPbkdf2Hash()
    {
        var service = new AdminCredentialService(Options.Create(new AdminOptions { Username = "admin", PasswordHash = AdminCredentialService.Hash("correct horse battery staple") }));
        Assert.True(service.Verify("admin", "correct horse battery staple"));
        Assert.False(service.Verify("admin", "wrong"));
        Assert.False(service.Verify("other", "correct horse battery staple"));
    }
}
