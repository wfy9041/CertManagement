using System.Collections.Concurrent;

namespace CertManagement.Application;

public sealed class ChallengeStore
{
	private readonly ConcurrentDictionary<string, ChallengeEntry> _entries = new(StringComparer.Ordinal);

	public void Set(string token, string keyAuthorization, TimeSpan lifetime)
	{
		_entries[token] = new ChallengeEntry(keyAuthorization, DateTimeOffset.UtcNow.Add(lifetime));
	}

	public bool TryGet(string token, out string value)
	{
		if (_entries.TryGetValue(token, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
		{
			value = entry.Value;
			return true;
		}

		_entries.TryRemove(token, out _);
		value = string.Empty;
		return false;
	}

	public void Remove(string token) => _entries.TryRemove(token, out _);

	private sealed record ChallengeEntry(string Value, DateTimeOffset ExpiresAt);
}