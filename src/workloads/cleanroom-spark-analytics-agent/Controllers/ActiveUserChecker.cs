// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace Controllers;

public class ActiveUserChecker
{
    private readonly ILogger logger;
    private readonly LazyExpiringCache<string> isActiveCache;
    private readonly GovernanceClientManager governanceClientManager;

    public ActiveUserChecker(
        ILogger logger,
        GovernanceClientManager governanceClientManager)
    {
        this.logger = logger;
        this.governanceClientManager = governanceClientManager;
        this.isActiveCache = new LazyExpiringCache<string>(TimeSpan.FromMinutes(15));
    }

    public async Task CheckActive(HttpRequest incomingRequest, bool useCache)
    {
        // Copy over the authorization header which should contain the user token that gets used
        // to identify the user.
        // Local-Authorization is a special header that is used for local development/testing for
        // scenarios that use kubectl proxy and kubectl proxy drops any Authorization header passed
        // to it. We bypass this by using a different header that kubectl proxy does not drop.
        string? authHeader = (string?)incomingRequest.Headers.Authorization ??
            incomingRequest.Headers["Local-Authorization"];
        if (authHeader == null)
        {
            throw new Exception($"Expecting Authorization header to be present.");
        }

        if (!useCache || !this.isActiveCache.TryGet(authHeader, out var _))
        {
            await this.CheckCallerActive(authHeader);
            this.isActiveCache.Set(authHeader, "doesnotmatter");
        }
    }

    private async Task CheckCallerActive(string? authHeader)
    {
        var client = this.governanceClientManager.GetClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "users/isactive");
        if (authHeader != null)
        {
            var parts = authHeader.Split(' ', 2);
            if (parts.Length != 2)
            {
                throw new Exception(
                    $"Expecting Authorization header to have 2 parts but found {parts.Length}");
            }

            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(parts[0], parts[1]);
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
    }

    public class LazyExpiringCache<TValue>
    {
        private readonly ConcurrentDictionary<string, CacheItem> ccache = new();
        private readonly TimeSpan defaulTtl;

        public LazyExpiringCache(TimeSpan defaultTtl)
        {
            this.defaulTtl = defaultTtl;
        }

        public void Set(string key, TValue value, TimeSpan? ttl = null)
        {
            var expiry = DateTime.UtcNow + (ttl ?? this.defaulTtl);
            this.ccache[key] = new CacheItem(value, expiry);
        }

        public bool TryGet(string key, out TValue value)
        {
            if (this.ccache.TryGetValue(key, out var item))
            {
                if (item.ExpiresAt > DateTime.UtcNow)
                {
                    value = item.Value;
                    return true;
                }

                // Expired, remove on access.
                this.ccache.TryRemove(key, out _);
            }

            // Opportunistically exipre any other items also.
            foreach (var entry in this.ccache)
            {
                if (DateTime.UtcNow >= entry.Value.ExpiresAt)
                {
                    // Expired.
                    this.ccache.TryRemove(entry.Key, out _);
                }
            }

            value = default!;
            return false;
        }

        private record CacheItem(TValue Value, DateTime ExpiresAt);
    }
}