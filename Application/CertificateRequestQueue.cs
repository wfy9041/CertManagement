using System.Threading.Channels;

namespace CertManagement.Application;

public sealed record CertificateRequest(int CertificateId, bool Renewal);

public sealed class CertificateRequestQueue
{
	private readonly Channel<CertificateRequest> requests = Channel.CreateUnbounded<CertificateRequest>();

	public ValueTask EnqueueAsync(CertificateRequest request, CancellationToken cancellationToken = default) => requests.Writer.WriteAsync(request, cancellationToken);

	public IAsyncEnumerable<CertificateRequest> ReadAllAsync(CancellationToken cancellationToken) => requests.Reader.ReadAllAsync(cancellationToken);
}

public sealed class CertificateRequestWorker(CertificateRequestQueue requests, IServiceScopeFactory scopeFactory, ILogger<CertificateRequestWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await foreach (var request in requests.ReadAllAsync(stoppingToken))
		{
			try
			{
				await using var scope = scopeFactory.CreateAsyncScope();
				await scope.ServiceProvider.GetRequiredService<CertificateService>().ProcessRequestAsync(request.CertificateId, request.Renewal, stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
			catch (Exception ex) { logger.LogError(ex, "后台证书申请失败，证书 {CertificateId}", request.CertificateId); }
		}
	}
}