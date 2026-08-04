using ExamTransfer.Desktop.Infrastructure;
using Microsoft.AspNetCore.Http.Connections.Client;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class RealtimeAuthenticationTests
{
    [Fact]
    public async Task AccountRealtime_UsesBearerTokenProvider()
    {
        var options = new HttpConnectionOptions();

        RealtimeService.ConfigureAuthentication(
            options,
            " account-token ",
            RealtimeAuthenticationMode.AccountBearer);

        Assert.False(options.Headers.ContainsKey("X-Exam-Session-Token"));
        Assert.NotNull(options.AccessTokenProvider);
        Assert.Equal("account-token", await options.AccessTokenProvider());
    }

    [Fact]
    public void StudentLanRealtime_UsesParticipantHeaderDuringNegotiate()
    {
        var options = new HttpConnectionOptions();

        RealtimeService.ConfigureAuthentication(
            options,
            " participant-token ",
            RealtimeAuthenticationMode.ParticipantHeader);

        Assert.Null(options.AccessTokenProvider);
        Assert.Equal(
            "participant-token",
            options.Headers["X-Exam-Session-Token"]);
    }
}
