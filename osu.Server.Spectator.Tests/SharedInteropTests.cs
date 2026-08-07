// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using osu.Server.Spectator.Services;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class SharedInteropTests
    {
        [Fact]
        public async Task G0v0EndpointsUseExpectedPayloads()
        {
            var requests = new List<CapturedRequest>();
            using var httpClient = new HttpClient(new DelegateHandler(async request =>
            {
                requests.Add(new CapturedRequest(
                    request.Method,
                    request.RequestUri!,
                    request.Content == null ? null : await request.Content.ReadAsStringAsync(),
                    request.Headers.Contains("X-LIO-Signature")));

                string response = request.RequestUri!.AbsolutePath.EndsWith("ruleset-hashes", StringComparison.Ordinal)
                    ? "{\"custom\":{\"latest-version\":\"1.0\",\"versions\":{\"1.0\":\"hash\"}}}"
                    : string.Empty;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
            }));
            var interop = new SharedInterop(httpClient, createLoggerFactory());

            await interop.EnsureBeatmapPresentAsync(123);
            await interop.UploadReplayAsync(42, 99, 123, new MemoryStream([1, 2, 3]));
            var hashes = await interop.GetRulesetHashesAsync();

            Assert.Collection(requests,
                request =>
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("/_lio/beatmaps/ensure", request.Uri.AbsolutePath);
                    using JsonDocument body = JsonDocument.Parse(request.Body!);
                    Assert.Equal(123, body.RootElement.GetProperty("beatmap_id").GetInt32());
                },
                request =>
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("/_lio/scores/replay", request.Uri.AbsolutePath);
                    using JsonDocument body = JsonDocument.Parse(request.Body!);
                    Assert.Equal(42, body.RootElement.GetProperty("user_id").GetInt32());
                    Assert.Equal(99, body.RootElement.GetProperty("score_id").GetInt64());
                    Assert.Equal(123, body.RootElement.GetProperty("beatmap_id").GetInt32());
                    Assert.Equal(Convert.ToBase64String([1, 2, 3]), body.RootElement.GetProperty("mreplay").GetString());
                },
                request =>
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal("/_lio/ruleset-hashes", request.Uri.AbsolutePath);
                });

            Assert.All(requests, request =>
            {
                Assert.Contains("timestamp=", request.Uri.Query);
                Assert.True(request.HasSignature);
            });
            Assert.Equal("hash", hashes["custom"].Versions["1.0"]);
        }

        private static ILoggerFactory createLoggerFactory()
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            return loggerFactory.Object;
        }

        private record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, bool HasSignature);

        private class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
        }
    }
}
