using CertManagement.Domain;
using CertManagement.Application;

namespace CertManagement.Web.Pages;

public partial class WafDeployments
{
    private int credentialSourceId;

    private void OnTypeChanged()
    {
        credentialSourceId = 0;
        form.Endpoint = form.Type == DeploymentTargetType.Esa ? EsaDeploymentClient.DefaultEndpoint : "wafopenapi.cn-hangzhou.aliyuncs.com";
        form.InstanceId = string.Empty;
        form.EsaCertificateId = null;
        secret = string.Empty;
    }
}
