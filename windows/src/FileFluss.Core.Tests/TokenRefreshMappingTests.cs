// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Providers;

namespace FileFluss.Core.Tests;

public sealed class TokenRefreshMappingTests
{
    [Theory]
    [InlineData(400, CloudErrorKind.NotAuthenticated)]
    [InlineData(401, CloudErrorKind.NotAuthenticated)]
    [InlineData(403, CloudErrorKind.NotAuthenticated)]
    [InlineData(429, CloudErrorKind.RateLimited)]
    [InlineData(500, CloudErrorKind.ServerError)]
    [InlineData(503, CloudErrorKind.ServerError)]
    public void OnlyARejectedGrantAsksTheUserToSignInAgain(int status, CloudErrorKind expected)
    {
        var error = CloudProviderException.TokenRefreshFailed(status);

        Assert.Equal(expected, error.Kind);
        Assert.Equal(expected == CloudErrorKind.NotAuthenticated, error.NeedsReauthentication);
    }
}
